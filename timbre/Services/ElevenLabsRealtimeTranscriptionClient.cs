using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class ElevenLabsRealtimeTranscriptionClient
{
    private static readonly Uri Endpoint = new("wss://api.elevenlabs.io/v1/speech-to-text/realtime");
    private static readonly Uri SingleUseTokenEndpoint = new("https://api.elevenlabs.io/v1/single-use-token/realtime_scribe");
    private const string RealtimeModel = TranscriptionProviderCatalog.DefaultElevenLabsStreamingModel;
    private const string AudioFormat = "pcm_16000";
    private const int SampleRate = 16000;
    private readonly HttpClient _httpClient;
    private volatile bool _preferSingleUseTokenAuth;

    public ElevenLabsRealtimeTranscriptionClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public async Task<ElevenLabsRealtimeSession> ConnectAsync(
        string apiKey,
        string model,
        string? language,
        double vadSilenceThresholdSeconds,
        Func<string, CancellationToken, Task> transcriptChunkHandler,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new TranscriptionException("The ElevenLabs API key is missing.", false);
        }

        if (!string.Equals(ResolveModel(model), RealtimeModel, StringComparison.OrdinalIgnoreCase))
        {
            throw new TranscriptionException("ElevenLabs realtime streaming requires the scribe_v2_realtime model.", false);
        }

        var resolvedApiKey = apiKey.Trim();
        var resolvedLanguage = NormalizeLanguage(language);
        var resolvedVadSilenceThresholdSeconds = TranscriptionProviderCatalog.NormalizeElevenLabsVadSilenceThresholdSeconds(vadSilenceThresholdSeconds);

        if (_preferSingleUseTokenAuth)
        {
            return await ConnectWithSingleUseTokenAsync(
                resolvedApiKey,
                resolvedLanguage,
                resolvedVadSilenceThresholdSeconds,
                transcriptChunkHandler,
                cancellationToken);
        }

        try
        {
            return await ConnectWithApiKeyHeaderAsync(
                resolvedApiKey,
                resolvedLanguage,
                resolvedVadSilenceThresholdSeconds,
                transcriptChunkHandler,
                cancellationToken);
        }
        catch (TranscriptionException exception) when (ShouldRetryWithSingleUseToken(exception))
        {
            DiagnosticsLogger.Info(
                $"ElevenLabs realtime API-key websocket authentication failed during initialization. Retrying with a single-use token. Message='{exception.Message}'.");

            var session = await ConnectWithSingleUseTokenAsync(
                resolvedApiKey,
                resolvedLanguage,
                resolvedVadSilenceThresholdSeconds,
                transcriptChunkHandler,
                cancellationToken);
            _preferSingleUseTokenAuth = true;
            DiagnosticsLogger.Info("ElevenLabs realtime single-use token authentication succeeded. Future sessions will prefer token auth for this app run.");
            return session;
        }
    }

    private async Task<ElevenLabsRealtimeSession> ConnectWithApiKeyHeaderAsync(
        string apiKey,
        string? language,
        double vadSilenceThresholdSeconds,
        Func<string, CancellationToken, Task> transcriptChunkHandler,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(language, vadSilenceThresholdSeconds);
        return await ConnectWithWebSocketAsync(
            endpoint,
            ConnectionAuthMode.ApiKeyHeader,
            webSocket => webSocket.Options.SetRequestHeader("xi-api-key", apiKey),
            language,
            vadSilenceThresholdSeconds,
            transcriptChunkHandler,
            cancellationToken);
    }

    private async Task<ElevenLabsRealtimeSession> ConnectWithSingleUseTokenAsync(
        string apiKey,
        string? language,
        double vadSilenceThresholdSeconds,
        Func<string, CancellationToken, Task> transcriptChunkHandler,
        CancellationToken cancellationToken)
    {
        var singleUseToken = await CreateSingleUseTokenAsync(apiKey, cancellationToken);
        var endpoint = BuildEndpoint(language, vadSilenceThresholdSeconds, singleUseToken);
        return await ConnectWithWebSocketAsync(
            endpoint,
            ConnectionAuthMode.SingleUseToken,
            configureWebSocket: null,
            language,
            vadSilenceThresholdSeconds,
            transcriptChunkHandler,
            cancellationToken);
    }

    private async Task<ElevenLabsRealtimeSession> ConnectWithWebSocketAsync(
        Uri endpoint,
        ConnectionAuthMode authMode,
        Action<ClientWebSocket>? configureWebSocket,
        string? language,
        double vadSilenceThresholdSeconds,
        Func<string, CancellationToken, Task> transcriptChunkHandler,
        CancellationToken cancellationToken)
    {
        var sanitizedEndpoint = SanitizeEndpointForLog(endpoint);

        return await RealtimeWebSocketConnector.ConnectAsync(
            endpoint,
            webSocket =>
            {
                webSocket.Options.CollectHttpResponseDetails = true;
                configureWebSocket?.Invoke(webSocket);
            },
            webSocket => new ElevenLabsRealtimeSession(webSocket, transcriptChunkHandler),
            (session, token) => session.InitializeAsync(token),
            "Connecting to ElevenLabs timed out.",
            "The ElevenLabs realtime connection failed",
            () => $"ElevenLabs realtime connection starting. Endpoint={sanitizedEndpoint}, AuthMode={DescribeAuthMode(authMode)}, Model={RealtimeModel}, AudioFormat={AudioFormat}, SampleRate={SampleRate}, Language='{language ?? "auto"}', VadSilenceThresholdSeconds={vadSilenceThresholdSeconds.ToString(CultureInfo.InvariantCulture)}.",
            () => $"ElevenLabs realtime connection established. Endpoint={sanitizedEndpoint}, AuthMode={DescribeAuthMode(authMode)}, Model={RealtimeModel}, Language='{language ?? "auto"}', VadSilenceThresholdSeconds={vadSilenceThresholdSeconds.ToString(CultureInfo.InvariantCulture)}.",
            (webSocket, exception) =>
            {
                var responseStatus = webSocket.HttpStatusCode == 0
                    ? "<not collected>"
                    : $"{(int)webSocket.HttpStatusCode} {webSocket.HttpStatusCode}";

                DiagnosticsLogger.Error(
                    $"ElevenLabs realtime connection failed. Endpoint={sanitizedEndpoint}, AuthMode={DescribeAuthMode(authMode)}, ResponseStatus={responseStatus}.",
                    exception);
            },
            cancellationToken);
    }

    private async Task<string> CreateSingleUseTokenAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SingleUseTokenEndpoint);
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey);

        DiagnosticsLogger.Info("Requesting ElevenLabs single-use token for realtime Scribe.");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        DiagnosticsLogger.Info(
            $"ElevenLabs single-use token response received. Status={(int)response.StatusCode} {response.StatusCode}, BodyLength={responseBody.Length}.");

        if (!response.IsSuccessStatusCode)
        {
            throw new TranscriptionException(
                JsonErrorMessageExtractor.Extract(responseBody, "ElevenLabs", (int)response.StatusCode),
                HttpStatusUtilities.IsTransient(response.StatusCode),
                response.StatusCode);
        }

        SingleUseTokenResponse? payload;

        try
        {
            payload = JsonSerializer.Deserialize<SingleUseTokenResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException exception)
        {
            throw new TranscriptionException("ElevenLabs returned an invalid single-use token response.", true, null, exception);
        }

        if (string.IsNullOrWhiteSpace(payload?.Token))
        {
            throw new TranscriptionException("ElevenLabs returned an empty single-use token for realtime transcription.", true);
        }

        return payload.Token.Trim();
    }

    internal static Uri BuildEndpoint(string? language, double vadSilenceThresholdSeconds, string? token = null)
    {
        return UriQuery.Build(
            Endpoint,
            ("model_id", RealtimeModel),
            ("token", token),
            ("audio_format", AudioFormat),
            ("commit_strategy", "vad"),
            ("vad_silence_threshold_secs", vadSilenceThresholdSeconds.ToString(CultureInfo.InvariantCulture)),
            ("include_timestamps", "false"),
            ("language_code", language));
    }

    private static string ResolveModel(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? RealtimeModel : value.Trim();
    }

    private static string? NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized == "auto" ? null : normalized;
    }

    private static bool ShouldRetryWithSingleUseToken(TranscriptionException exception)
    {
        return exception.Message.Contains("authenticated", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("auth", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeAuthMode(ConnectionAuthMode authMode)
    {
        return authMode switch
        {
            ConnectionAuthMode.ApiKeyHeader => "xi-api-key header",
            ConnectionAuthMode.SingleUseToken => "single-use token",
            _ => authMode.ToString(),
        };
    }

    private static string SanitizeEndpointForLog(Uri endpoint)
    {
        return UriQuery.Redact(endpoint, "token").ToString();
    }

    private enum ConnectionAuthMode
    {
        ApiKeyHeader,
        SingleUseToken,
    }

    public sealed class ElevenLabsRealtimeSession : IRealtimeTranscriptionSession
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };
        private static readonly TimeSpan SessionInitializationTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(12);
        private static readonly TimeSpan CompletionQuietPeriod = TimeSpan.FromMilliseconds(650);

        private readonly ClientWebSocket _webSocket;
        private readonly Func<string, CancellationToken, Task> _transcriptChunkHandler;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly CancellationTokenSource _receiveLoopCancellationTokenSource = new();
        private readonly TaskCompletionSource _sessionStartedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _receiveLoopTask;
        private readonly object _transcriptLock = new();
        private readonly object _completionDebounceLock = new();
        private CancellationTokenSource? _completionDebounceCancellationTokenSource;
        private string _completedTranscript = string.Empty;
        private bool _sessionStarted;
        private bool _completionRequested;
        private int _chunksSent;
        private long _bytesSent;

        public ElevenLabsRealtimeSession(
            ClientWebSocket webSocket,
            Func<string, CancellationToken, Task> transcriptChunkHandler)
        {
            _webSocket = webSocket;
            _transcriptChunkHandler = transcriptChunkHandler;
            _receiveLoopTask = Task.Run(ReceiveLoopAsync);
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _sessionStartedSource.Task.WaitAsync(SessionInitializationTimeout, cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new TranscriptionException("ElevenLabs did not finish initializing the realtime session in time.", true, null, exception);
            }
        }

        public async Task SendAudioAsync(byte[] audioBytes, CancellationToken cancellationToken = default)
        {
            if (audioBytes.Length == 0)
            {
                return;
            }

            var base64Audio = Convert.ToBase64String(audioBytes);
            await SendJsonMessageAsync(new InputAudioChunkMessage
            {
                AudioBase64 = base64Audio,
                Commit = false,
                SampleRate = SampleRate,
            }, cancellationToken);

            _chunksSent++;
            _bytesSent += audioBytes.Length;

            if (_chunksSent == 1 || _chunksSent % 25 == 0)
            {
                DiagnosticsLogger.Info($"ElevenLabs realtime audio chunk sent. ChunkIndex={_chunksSent}, ChunkBytes={audioBytes.Length}, TotalBytesSent={_bytesSent}.");
            }
        }

        public async Task<string> CompleteAsync(CancellationToken cancellationToken = default)
        {
            DiagnosticsLogger.Info($"ElevenLabs realtime completion requested. ChunksSent={_chunksSent}, TotalBytesSent={_bytesSent}.");
            _completionRequested = true;
            await SendCommitAsync(cancellationToken);

            try
            {
                return await _completionSource.Task.WaitAsync(CompletionTimeout, cancellationToken);
            }
            catch (TimeoutException) when (!string.IsNullOrWhiteSpace(GetTranscriptSnapshot()))
            {
                DiagnosticsLogger.Info("ElevenLabs realtime final commit timed out after receiving committed transcript chunks. Returning current transcript snapshot.");
                return GetTranscriptSnapshot();
            }
            catch (TimeoutException exception)
            {
                throw new TranscriptionException("ElevenLabs did not finish finalizing the realtime stream in time.", true, null, exception);
            }
            finally
            {
                await DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                _receiveLoopCancellationTokenSource.Cancel();
                CancelCompletionDebounce();

                try
                {
                    await _receiveLoopTask;
                }
                catch
                {
                }

                _webSocket.Dispose();
                _receiveLoopCancellationTokenSource.Dispose();
                _sendLock.Dispose();
            }
        }

        private Task SendCommitAsync(CancellationToken cancellationToken)
        {
            return SendJsonMessageAsync(new InputAudioChunkMessage
            {
                AudioBase64 = string.Empty,
                Commit = true,
                SampleRate = SampleRate,
            }, cancellationToken);
        }

        private async Task SendJsonMessageAsync<TMessage>(TMessage message, CancellationToken cancellationToken)
        {
            await WebSocketUtilities.SendJsonAsync(
                _webSocket,
                _sendLock,
                message,
                SerializerOptions,
                "The ElevenLabs realtime connection is no longer open.",
                "Sending data to ElevenLabs failed.",
                cancellationToken);
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[8192];

            try
            {
                while (!_receiveLoopCancellationTokenSource.IsCancellationRequested)
                {
                    var message = await WebSocketUtilities.ReceiveTextMessageAsync(
                        _webSocket,
                        buffer,
                        _receiveLoopCancellationTokenSource.Token);

                    if (message.IsClose)
                    {
                        DiagnosticsLogger.Info($"ElevenLabs server initiated close. CloseStatus={message.CloseStatus}, CloseStatusDescription='{message.CloseStatusDescription}'.");

                        if (_sessionStarted)
                        {
                            _completionSource.TrySetResult(GetTranscriptSnapshot());
                        }
                        else
                        {
                            _sessionStartedSource.TrySetException(CreateClosedBeforeStartException(message.CloseStatus, message.CloseStatusDescription));
                        }

                        return;
                    }

                    if (!message.HasText)
                    {
                        continue;
                    }

                    if (!_sessionStarted)
                    {
                        DiagnosticsLogger.Info(
                            $"ElevenLabs realtime initialization message received. Length={message.Text!.Length}, Preview='{TranscriptText.Preview(message.Text)}'.");
                    }

                    await HandleMessageAsync(message.Text!, _receiveLoopCancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException)
            {
                if (_sessionStarted)
                {
                    _completionSource.TrySetResult(GetTranscriptSnapshot());
                }
            }
            catch (Exception exception)
            {
                var wrappedException = exception as TranscriptionException
                    ?? new TranscriptionException("The ElevenLabs realtime session failed.", true, null, exception);

                DiagnosticsLogger.Error("ElevenLabs realtime receive loop failed.", wrappedException);
                _sessionStartedSource.TrySetException(wrappedException);

                if (_sessionStarted)
                {
                    _completionSource.TrySetException(wrappedException);
                }
            }
        }

        private async Task HandleMessageAsync(string message, CancellationToken cancellationToken)
        {
            RealtimeEnvelope? envelope;

            try
            {
                envelope = JsonSerializer.Deserialize<RealtimeEnvelope>(message, SerializerOptions);
            }
            catch (JsonException exception)
            {
                throw new TranscriptionException("ElevenLabs returned an invalid realtime response.", true, null, exception);
            }

            if (envelope is null || string.IsNullOrWhiteSpace(envelope.MessageType))
            {
                return;
            }

            switch (envelope.MessageType)
            {
                case "session_started":
                {
                    _sessionStarted = true;
                    DiagnosticsLogger.Info($"ElevenLabs realtime session started. SessionId='{envelope.SessionId ?? string.Empty}'.");
                    _sessionStartedSource.TrySetResult();
                    return;
                }
                case "partial_transcript":
                {
                    DiagnosticsLogger.Info($"ElevenLabs realtime partial transcript received. TextLength={envelope.Text?.Length ?? 0}, Preview='{TranscriptText.Preview(envelope.Text)}'.");
                    return;
                }
                case "committed_transcript":
                case "committed_transcript_with_timestamps":
                {
                    await CommitTranscriptAsync(envelope.Text, cancellationToken);

                    if (_completionRequested)
                    {
                        ScheduleCompletionAfterQuietPeriod();
                    }

                    return;
                }
                default:
                {
                    if (IsErrorMessageType(envelope.MessageType))
                    {
                        var errorMessage = GetRealtimeErrorMessage(envelope);
                        DiagnosticsLogger.Info(
                            $"ElevenLabs realtime error message received during {(_sessionStarted ? "active streaming" : "session initialization")}. MessageType='{envelope.MessageType}', Message='{errorMessage}'.");
                        throw new TranscriptionException(
                            errorMessage,
                            IsTransientRealtimeError(envelope.MessageType));
                    }

                    DiagnosticsLogger.Info($"ElevenLabs realtime unhandled message type received. MessageType='{envelope.MessageType}'.");
                    return;
                }
            }
        }

        private void ScheduleCompletionAfterQuietPeriod()
        {
            CancellationTokenSource debounceCancellationTokenSource;

            lock (_completionDebounceLock)
            {
                _completionDebounceCancellationTokenSource?.Cancel();
                _completionDebounceCancellationTokenSource?.Dispose();
                _completionDebounceCancellationTokenSource = new CancellationTokenSource();
                debounceCancellationTokenSource = _completionDebounceCancellationTokenSource;
            }

            _ = CompleteAfterQuietPeriodAsync(debounceCancellationTokenSource.Token);
        }

        private async Task CompleteAfterQuietPeriodAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(CompletionQuietPeriod, cancellationToken);
                _completionSource.TrySetResult(GetTranscriptSnapshot());
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void CancelCompletionDebounce()
        {
            lock (_completionDebounceLock)
            {
                _completionDebounceCancellationTokenSource?.Cancel();
                _completionDebounceCancellationTokenSource?.Dispose();
                _completionDebounceCancellationTokenSource = null;
            }
        }

        private async Task CommitTranscriptAsync(string? text, CancellationToken cancellationToken)
        {
            var transcript = TranscriptText.NormalizeWhitespace(text);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return;
            }

            string chunkToPaste;

            lock (_transcriptLock)
            {
                chunkToPaste = TranscriptText.BuildAppendChunk(_completedTranscript, transcript);
                _completedTranscript += chunkToPaste;
            }

            if (!string.IsNullOrWhiteSpace(chunkToPaste))
            {
                DiagnosticsLogger.Info($"ElevenLabs realtime committed transcript received. TextLength={chunkToPaste.Length}, Preview='{TranscriptText.Preview(chunkToPaste)}'.");
                await _transcriptChunkHandler(chunkToPaste, cancellationToken);
            }
        }

        private string GetTranscriptSnapshot()
        {
            lock (_transcriptLock)
            {
                return _completedTranscript.Trim();
            }
        }

        private static bool IsErrorMessageType(string messageType)
        {
            return messageType is "error"
                or "auth_error"
                or "quota_exceeded"
                or "quota_exceeded_error"
                or "commit_throttled"
                or "throttled"
                or "throttled_error"
                or "unaccepted_terms"
                or "unaccepted_terms_error"
                or "rate_limited"
                or "rate_limited_error"
                or "queue_overflow"
                or "queue_overflow_error"
                or "resource_exhausted"
                or "resource_exhausted_error"
                or "session_time_limit_exceeded"
                or "session_time_limit_exceeded_error"
                or "input_error"
                or "chunk_size_exceeded"
                or "chunk_size_exceeded_error"
                or "insufficient_audio_activity"
                or "insufficient_audio_activity_error"
                or "transcriber_error";
        }

        private static bool IsTransientRealtimeError(string messageType)
        {
            return messageType is "error"
                or "rate_limited"
                or "rate_limited_error"
                or "queue_overflow"
                or "queue_overflow_error"
                or "resource_exhausted"
                or "resource_exhausted_error"
                or "throttled"
                or "throttled_error"
                or "transcriber_error";
        }

        private static string GetRealtimeErrorMessage(RealtimeEnvelope envelope)
        {
            if (!string.IsNullOrWhiteSpace(envelope.Message))
            {
                return envelope.Message.Trim();
            }

            if (envelope.Error.HasValue)
            {
                var errorElement = envelope.Error.Value;
                if (JsonErrorMessageExtractor.TryExtract(errorElement, out var errorMessage))
                {
                    return errorMessage;
                }
            }

            return $"ElevenLabs returned a realtime transcription error: {envelope.MessageType}.";
        }

        private static TranscriptionException CreateClosedBeforeStartException(WebSocketCloseStatus? closeStatus, string? closeStatusDescription)
        {
            var details = string.IsNullOrWhiteSpace(closeStatusDescription)
                ? closeStatus?.ToString() ?? "no close status provided"
                : $"{closeStatus} ({closeStatusDescription})";

            return new TranscriptionException(
                $"ElevenLabs closed the realtime connection before the session started. Close details: {details}.",
                true);
        }

        private sealed class InputAudioChunkMessage
        {
            [JsonPropertyName("message_type")]
            public string MessageType { get; set; } = "input_audio_chunk";

            [JsonPropertyName("audio_base_64")]
            public string AudioBase64 { get; set; } = string.Empty;

            [JsonPropertyName("commit")]
            public bool Commit { get; set; }

            [JsonPropertyName("sample_rate")]
            public int SampleRate { get; set; }
        }

        private sealed class RealtimeEnvelope
        {
            [JsonPropertyName("message_type")]
            public string? MessageType { get; set; }

            [JsonPropertyName("session_id")]
            public string? SessionId { get; set; }

            [JsonPropertyName("text")]
            public string? Text { get; set; }

            [JsonPropertyName("message")]
            public string? Message { get; set; }

            [JsonPropertyName("error")]
            public JsonElement? Error { get; set; }
        }
    }

    private sealed class SingleUseTokenResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }
    }
}
