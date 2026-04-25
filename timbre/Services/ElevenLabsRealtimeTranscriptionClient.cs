using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
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
        var webSocket = new ClientWebSocket();
        webSocket.Options.CollectHttpResponseDetails = true;
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        configureWebSocket?.Invoke(webSocket);
        ElevenLabsRealtimeSession? session = null;
        var sanitizedEndpoint = SanitizeEndpointForLog(endpoint);

        try
        {
            DiagnosticsLogger.Info(
                $"ElevenLabs realtime connection starting. Endpoint={sanitizedEndpoint}, AuthMode={DescribeAuthMode(authMode)}, Model={RealtimeModel}, AudioFormat={AudioFormat}, SampleRate={SampleRate}, Language='{language ?? "auto"}', VadSilenceThresholdSeconds={vadSilenceThresholdSeconds.ToString(CultureInfo.InvariantCulture)}.");

            await webSocket.ConnectAsync(endpoint, cancellationToken);

            session = new ElevenLabsRealtimeSession(webSocket, transcriptChunkHandler);
            await session.InitializeAsync(cancellationToken);
            DiagnosticsLogger.Info(
                $"ElevenLabs realtime connection established. Endpoint={sanitizedEndpoint}, AuthMode={DescribeAuthMode(authMode)}, Model={RealtimeModel}, Language='{language ?? "auto"}', VadSilenceThresholdSeconds={vadSilenceThresholdSeconds.ToString(CultureInfo.InvariantCulture)}.");
            return session;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
            else
            {
                webSocket.Dispose();
            }

            throw new TranscriptionException("Connecting to ElevenLabs timed out.", true);
        }
        catch (Exception exception)
        {
            var responseStatus = webSocket.HttpStatusCode == 0
                ? "<not collected>"
                : $"{(int)webSocket.HttpStatusCode} {webSocket.HttpStatusCode}";

            DiagnosticsLogger.Error(
                $"ElevenLabs realtime connection failed. Endpoint={sanitizedEndpoint}, AuthMode={DescribeAuthMode(authMode)}, ResponseStatus={responseStatus}.",
                exception);

            if (session is not null)
            {
                await session.DisposeAsync();
            }
            else
            {
                webSocket.Dispose();
            }

            throw exception as TranscriptionException
                ?? new TranscriptionException($"The ElevenLabs realtime connection failed: {exception.Message}", true, null, exception);
        }
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
                ExtractHttpErrorMessage(responseBody, (int)response.StatusCode),
                IsTransientStatusCode(response.StatusCode),
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
        var query = new StringBuilder();
        AppendQuery(query, "model_id", RealtimeModel);
        if (!string.IsNullOrWhiteSpace(token))
        {
            AppendQuery(query, "token", token);
        }

        AppendQuery(query, "audio_format", AudioFormat);
        AppendQuery(query, "commit_strategy", "vad");
        AppendQuery(
            query,
            "vad_silence_threshold_secs",
            vadSilenceThresholdSeconds.ToString(CultureInfo.InvariantCulture));
        AppendQuery(query, "include_timestamps", "false");

        if (!string.IsNullOrWhiteSpace(language))
        {
            AppendQuery(query, "language_code", language);
        }

        return new UriBuilder(Endpoint) { Query = query.ToString() }.Uri;
    }

    private static void AppendQuery(StringBuilder builder, string key, string value)
    {
        if (builder.Length > 0)
        {
            builder.Append('&');
        }

        builder.Append(Uri.EscapeDataString(key));
        builder.Append('=');
        builder.Append(Uri.EscapeDataString(value));
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
        var query = ParseQuery(endpoint.Query);
        if (query.ContainsKey("token"))
        {
            query["token"] = "<redacted>";
        }

        var sanitizedQuery = string.Join(
            "&",
            query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        return new UriBuilder(endpoint)
        {
            Query = sanitizedQuery,
        }.Uri.ToString();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return results;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex < 0)
            {
                results[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separatorIndex]);
            var value = Uri.UnescapeDataString(pair[(separatorIndex + 1)..]);
            results[key] = value;
        }

        return results;
    }

    private static string ExtractHttpErrorMessage(string responseBody, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;

            if (TryReadStringProperty(root, "message", out var message))
            {
                return message;
            }

            if (TryReadStringProperty(root, "detail", out var detail))
            {
                return detail;
            }

            if (root.TryGetProperty("detail", out var detailObject) && detailObject.ValueKind == JsonValueKind.Object)
            {
                if (TryReadStringProperty(detailObject, "message", out var detailMessage))
                {
                    return detailMessage;
                }

                if (TryReadStringProperty(detailObject, "msg", out var detailMsg))
                {
                    return detailMsg;
                }
            }

            if (root.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(errorElement.GetString()))
                {
                    return errorElement.GetString()!;
                }

                if (errorElement.ValueKind == JsonValueKind.Object)
                {
                    if (TryReadStringProperty(errorElement, "message", out var nestedMessage))
                    {
                        return nestedMessage;
                    }

                    if (TryReadStringProperty(errorElement, "detail", out var nestedDetail))
                    {
                        return nestedDetail;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return $"ElevenLabs returned HTTP {statusCode}.";
    }

    private static bool TryReadStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        value = property.GetString()!.Trim();
        return true;
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        var numericStatusCode = (int)statusCode;
        return numericStatusCode == 408 || numericStatusCode == 429 || numericStatusCode >= 500;
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
            var payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);

            await _sendLock.WaitAsync(cancellationToken);

            try
            {
                if (_webSocket.State != WebSocketState.Open)
                {
                    throw new TranscriptionException("The ElevenLabs realtime connection is no longer open.", true);
                }

                await _webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
            }
            catch (WebSocketException exception)
            {
                throw new TranscriptionException("Sending data to ElevenLabs failed.", true, null, exception);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[8192];

            try
            {
                while (!_receiveLoopCancellationTokenSource.IsCancellationRequested)
                {
                    using var messageStream = new MemoryStream();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _webSocket.ReceiveAsync(buffer, _receiveLoopCancellationTokenSource.Token);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            DiagnosticsLogger.Info($"ElevenLabs server initiated close. CloseStatus={result.CloseStatus}, CloseStatusDescription='{result.CloseStatusDescription}'.");

                            if (_sessionStarted)
                            {
                                _completionSource.TrySetResult(GetTranscriptSnapshot());
                            }
                            else
                            {
                                _sessionStartedSource.TrySetException(CreateClosedBeforeStartException(result.CloseStatus, result.CloseStatusDescription));
                            }

                            return;
                        }

                        messageStream.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType != WebSocketMessageType.Text || messageStream.Length == 0)
                    {
                        continue;
                    }

                    var message = Encoding.UTF8.GetString(messageStream.ToArray());

                    if (!_sessionStarted)
                    {
                        DiagnosticsLogger.Info(
                            $"ElevenLabs realtime initialization message received. Length={message.Length}, Preview='{CreateTranscriptPreview(message)}'.");
                    }

                    await HandleMessageAsync(message, _receiveLoopCancellationTokenSource.Token);
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
                    DiagnosticsLogger.Info($"ElevenLabs realtime partial transcript received. TextLength={envelope.Text?.Length ?? 0}, Preview='{CreateTranscriptPreview(envelope.Text)}'.");
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
            var transcript = NormalizeTranscriptText(text);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return;
            }

            string chunkToPaste;

            lock (_transcriptLock)
            {
                chunkToPaste = BuildAppendChunk(_completedTranscript, transcript);
                _completedTranscript += chunkToPaste;
            }

            if (!string.IsNullOrWhiteSpace(chunkToPaste))
            {
                DiagnosticsLogger.Info($"ElevenLabs realtime committed transcript received. TextLength={chunkToPaste.Length}, Preview='{CreateTranscriptPreview(chunkToPaste)}'.");
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

        private static string NormalizeTranscriptText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string BuildAppendChunk(string existingTranscript, string nextTranscript)
        {
            if (string.IsNullOrWhiteSpace(nextTranscript))
            {
                return string.Empty;
            }

            if (string.IsNullOrEmpty(existingTranscript))
            {
                return nextTranscript;
            }

            return NeedsSeparator(existingTranscript[^1], nextTranscript[0])
                ? $" {nextTranscript}"
                : nextTranscript;
        }

        private static bool NeedsSeparator(char previousCharacter, char nextCharacter)
        {
            if (char.IsWhiteSpace(previousCharacter) || char.IsWhiteSpace(nextCharacter))
            {
                return false;
            }

            return !IsLeadingPunctuation(nextCharacter) && !IsTrailingPunctuation(previousCharacter);
        }

        private static bool IsLeadingPunctuation(char value)
        {
            return value is '.' or ',' or '!' or '?' or ';' or ':' or ')' or ']' or '}' or '\'' or '"';
        }

        private static bool IsTrailingPunctuation(char value)
        {
            return value is '(' or '[' or '{' or '/' or '-' or '\'' or '"';
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
                if (errorElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(errorElement.GetString()))
                {
                    return errorElement.GetString()!.Trim();
                }

                if (errorElement.ValueKind == JsonValueKind.Object)
                {
                    if (TryReadStringProperty(errorElement, "message", out var nestedMessage))
                    {
                        return nestedMessage;
                    }

                    if (TryReadStringProperty(errorElement, "detail", out var nestedDetail))
                    {
                        return nestedDetail;
                    }
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

        private static bool TryReadStringProperty(JsonElement element, string propertyName, out string value)
        {
            value = string.Empty;

            if (!element.TryGetProperty(propertyName, out var property) ||
                property.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(property.GetString()))
            {
                return false;
            }

            value = property.GetString()!.Trim();
            return true;
        }

        private static string CreateTranscriptPreview(string? transcript)
        {
            if (string.IsNullOrEmpty(transcript))
            {
                return string.Empty;
            }

            return transcript.Length <= 120 ? transcript : transcript[..120] + "...";
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
