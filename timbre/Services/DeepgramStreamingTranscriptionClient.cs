using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using timbre.Models;

namespace timbre.Services;

public sealed class DeepgramStreamingTranscriptionClient
{
    private const string FluxEndOfTurnThreshold = "0.7";
    private const string FluxEagerEndOfTurnThreshold = "0.6";
    private static readonly Uri V1Endpoint = new("wss://api.deepgram.com/v1/listen");
    private static readonly Uri V2Endpoint = new("wss://api.deepgram.com/v2/listen");
    private static readonly Uri AuthGrantEndpoint = new("https://api.deepgram.com/v1/auth/grant");
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<DeepgramStreamingSession> ConnectAsync(
        string apiKey,
        string model,
        string? language,
        Func<string, CancellationToken, Task> transcriptChunkHandler,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new TranscriptionException("The Deepgram API key is missing.", false);
        }

        var resolvedApiKey = apiKey.Trim();
        var resolvedModel = ResolveModel(model);
        var resolvedLanguage = ResolveLanguage(language);
        var endpoint = BuildEndpoint(resolvedModel, resolvedLanguage);
        var authAttempts = await BuildAuthAttemptsAsync(resolvedApiKey, cancellationToken);

        DiagnosticsLogger.Info($"Deepgram streaming connection starting. Endpoint={endpoint}, Model={resolvedModel}, Language='{resolvedLanguage}', UsesV2={UsesV2Endpoint(resolvedModel)}, AuthAttempts={string.Join(", ", authAttempts.Select(attempt => attempt.Description))}.");

        Exception? lastException = null;

        foreach (var authAttempt in authAttempts)
        {
            ClientWebSocket? webSocket = null;

            try
            {
                DiagnosticsLogger.Info($"Attempting Deepgram WebSocket connect. Endpoint={endpoint}, AuthMode={authAttempt.Description}.");
                webSocket = CreateWebSocket(authAttempt.HeaderValue);
                await webSocket.ConnectAsync(endpoint, cancellationToken);
                DiagnosticsLogger.Info($"Deepgram streaming connection established. Endpoint={endpoint}, Model={resolvedModel}, Language='{resolvedLanguage}', AuthMode={authAttempt.Description}.");
                return new DeepgramStreamingSession(webSocket, transcriptChunkHandler, resolvedModel, resolvedLanguage);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                webSocket?.Dispose();
                DiagnosticsLogger.Error($"Deepgram connection timed out. Endpoint={endpoint}, AuthMode={authAttempt.Description}.", new TimeoutException("ConnectAsync timed out."));
                throw new TranscriptionException("Connecting to Deepgram timed out.", true);
            }
            catch (WebSocketException exception)
            {
                lastException = new TranscriptionException($"The streaming connection to Deepgram failed: {exception.Message}", true, null, exception);

                if (webSocket is not null)
                {
                    await LogFailedConnectionAsync(webSocket, endpoint, authAttempt, exception, cancellationToken);
                    webSocket.Dispose();
                }
            }
            catch (Exception exception)
            {
                webSocket?.Dispose();
                DiagnosticsLogger.Error($"Deepgram connection failed. Endpoint={endpoint}, AuthMode={authAttempt.Description}.", exception);
                lastException = new TranscriptionException($"The Deepgram connection failed: {exception.Message}", true, null, exception);
            }
        }

        throw lastException ?? new TranscriptionException("The streaming connection to Deepgram failed.", true);
    }

    private static ClientWebSocket CreateWebSocket(string authorizationHeaderValue)
    {
        var webSocket = new ClientWebSocket();
        webSocket.Options.CollectHttpResponseDetails = true;
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        webSocket.Options.SetRequestHeader("Authorization", authorizationHeaderValue);
        return webSocket;
    }

    private static async Task<List<AuthAttempt>> BuildAuthAttemptsAsync(string apiKey, CancellationToken cancellationToken)
    {
        var attempts = new List<AuthAttempt>();
        var accessToken = await TryCreateAccessTokenAsync(apiKey, cancellationToken);

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            attempts.Add(new AuthAttempt($"Bearer {accessToken}", "temporary access token"));
        }

        attempts.Add(new AuthAttempt($"Token {apiKey}", "API key"));
        return attempts;
    }

    private static async Task<string?> TryCreateAccessTokenAsync(string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, AuthGrantEndpoint)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Token {apiKey}");

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                DiagnosticsLogger.Info($"Deepgram access token grant failed. Status={(int)response.StatusCode} {response.StatusCode}, Body='{TruncateForLog(responseBody)}'.");
                return null;
            }

            GrantTokenResponse? payload;

            try
            {
                payload = JsonSerializer.Deserialize<GrantTokenResponse>(responseBody, SerializerOptions);
            }
            catch (JsonException exception)
            {
                DiagnosticsLogger.Error($"Deepgram access token response could not be parsed. Body='{TruncateForLog(responseBody)}'.", exception);
                return null;
            }

            if (string.IsNullOrWhiteSpace(payload?.AccessToken))
            {
                DiagnosticsLogger.Info($"Deepgram access token grant returned no token. Body='{TruncateForLog(responseBody)}'.");
                return null;
            }

            DiagnosticsLogger.Info($"Deepgram temporary access token granted successfully. ExpiresIn={payload.ExpiresIn}.");
            return payload.AccessToken.Trim();
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error("Deepgram access token grant failed unexpectedly.", exception);
            return null;
        }
    }

    private static async Task LogFailedConnectionAsync(
        ClientWebSocket webSocket,
        Uri endpoint,
        AuthAttempt authAttempt,
        WebSocketException exception,
        CancellationToken cancellationToken)
    {
        var responseStatus = webSocket.HttpStatusCode == 0
            ? "<not collected>"
            : $"{(int)webSocket.HttpStatusCode} {webSocket.HttpStatusCode}";
        var responseHeaders = FormatHeaders(webSocket.HttpResponseHeaders);

        DiagnosticsLogger.Error($"Deepgram WebSocket connection failed. Endpoint={endpoint}, AuthMode={authAttempt.Description}, ResponseStatus={responseStatus}, ResponseHeaders={responseHeaders}.", exception);

        await LogHandshakeDiagnosticAsync(endpoint, authAttempt.HeaderValue, authAttempt.Description, cancellationToken);
    }

    private static async Task LogHandshakeDiagnosticAsync(
        Uri endpoint,
        string authorizationHeaderValue,
        string authDescription,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };

            request.Headers.TryAddWithoutValidation("Authorization", authorizationHeaderValue);
            request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
            request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var responseHeaders = FormatHeaders(response.Headers, response.Content.Headers);

            DiagnosticsLogger.Info($"Deepgram handshake diagnostic response. Endpoint={endpoint}, AuthMode={authDescription}, Status={(int)response.StatusCode} {response.StatusCode}, Headers={responseHeaders}, Body='{TruncateForLog(responseBody)}'.");
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error($"Deepgram handshake diagnostic failed. Endpoint={endpoint}, AuthMode={authDescription}.", exception);
        }
    }

    private static Uri BuildEndpoint(string model, string language)
    {
        var baseEndpoint = UsesV2Endpoint(model) ? V2Endpoint : V1Endpoint;
        var isFluxModel = IsFluxModel(model);
        var query = new StringBuilder();
        AppendQuery(query, "model", model);

        if (!isFluxModel)
        {
            AppendQuery(query, "language", language);
        }

        AppendQuery(query, "encoding", "linear16");
        AppendQuery(query, "sample_rate", "16000");

        if (isFluxModel)
        {
            AppendQuery(query, "eot_threshold", FluxEndOfTurnThreshold);
            AppendQuery(query, "eager_eot_threshold", FluxEagerEndOfTurnThreshold);
        }
        else
        {
            AppendQuery(query, "interim_results", "true");
            AppendQuery(query, "punctuate", "true");
            AppendQuery(query, "smart_format", "true");
        }

        return new UriBuilder(baseEndpoint) { Query = query.ToString() }.Uri;
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

    private static string ResolveModel(string? model)
    {
        var normalized = string.IsNullOrWhiteSpace(model) ? "flux" : model.Trim().ToLowerInvariant();
        return normalized == "flux" ? "flux-general-en" : normalized;
    }

    private static string ResolveLanguage(string? language)
    {
        return string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant();
    }

    private static bool UsesV2Endpoint(string model)
    {
        return IsFluxModel(model);
    }

    private static bool IsFluxModel(string model)
    {
        return model.StartsWith("flux", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatHeaders(IReadOnlyDictionary<string, IEnumerable<string>>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return "<none>";
        }

        return string.Join("; ", headers.Select(header => $"{header.Key}={string.Join(",", header.Value)}"));
    }

    private static string FormatHeaders(HttpResponseHeaders headers, HttpContentHeaders contentHeaders)
    {
        var pairs = headers.Select(header => $"{header.Key}={string.Join(",", header.Value)}")
            .Concat(contentHeaders.Select(header => $"{header.Key}={string.Join(",", header.Value)}"))
            .ToArray();
        return pairs.Length == 0 ? "<none>" : string.Join("; ", pairs);
    }

    private static string TruncateForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 800 ? normalized : normalized[..800] + "...";
    }

    private readonly record struct AuthAttempt(string HeaderValue, string Description);

    private sealed class GrantTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public double? ExpiresIn { get; set; }
    }
}

public sealed class DeepgramStreamingSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ClientWebSocket _webSocket;
    private readonly Func<string, CancellationToken, Task> _transcriptChunkHandler;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource<string> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _receiveLoopCancellationTokenSource = new();
    private readonly object _transcriptLock = new();
    private readonly Task _receiveLoopTask;
    private string _completedTranscript = string.Empty;
    private string _activeTurnLatestTranscript = string.Empty;
    private string _activeTurnCommittedTranscript = string.Empty;
    private double? _activeTurnIndex;
    private bool _completionRequested;
    private bool _closeStreamRequested;
    private int _chunksSent;
    private long _bytesSent;

    public DeepgramStreamingSession(
        ClientWebSocket webSocket,
        Func<string, CancellationToken, Task> transcriptChunkHandler,
        string model,
        string language)
    {
        _webSocket = webSocket;
        _transcriptChunkHandler = transcriptChunkHandler;
        Model = model;
        Language = language;
        DiagnosticsLogger.Info($"Deepgram session created. Model={Model}, Language='{Language}'.");
        _receiveLoopTask = Task.Run(ReceiveLoopAsync);
    }

    public string Model { get; }

    public string Language { get; }

    private bool IsFluxSession => Model.StartsWith("flux", StringComparison.OrdinalIgnoreCase);

    public async Task SendAudioAsync(byte[] audioBytes, CancellationToken cancellationToken = default)
    {
        if (audioBytes.Length == 0)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken);

        try
        {
            if (_webSocket.State != WebSocketState.Open)
            {
                throw new TranscriptionException("The Deepgram streaming connection is no longer open.", true);
            }

            await _webSocket.SendAsync(audioBytes, WebSocketMessageType.Binary, true, cancellationToken);
            _chunksSent++;
            _bytesSent += audioBytes.Length;

            if (_chunksSent == 1 || _chunksSent % 25 == 0)
            {
                DiagnosticsLogger.Info($"Deepgram audio chunk sent. ChunkIndex={_chunksSent}, ChunkBytes={audioBytes.Length}, TotalBytesSent={_bytesSent}.");
            }
        }
        catch (WebSocketException exception)
        {
            throw new TranscriptionException("Sending audio to Deepgram failed.", true, null, exception);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<string> CompleteAsync(CancellationToken cancellationToken = default)
    {
        DiagnosticsLogger.Info($"Deepgram completion requested. ChunksSent={_chunksSent}, TotalBytesSent={_bytesSent}, IsFluxSession={IsFluxSession}.");
        _completionRequested = true;
        await SendControlMessageAsync(IsFluxSession ? "CloseStream" : "Finalize", cancellationToken);

        try
        {
            return await _completionSource.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TranscriptionException("Deepgram did not finish finalizing the stream in time.", true, null, exception);
        }
        finally
        {
            DiagnosticsLogger.Info("Deepgram session completion finished. Disposing session.");
            await DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        DiagnosticsLogger.Info($"Disposing Deepgram session. WebSocketState={_webSocket.State}, ChunksSent={_chunksSent}, TotalBytesSent={_bytesSent}.");

        try
        {
            if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived && !_closeStreamRequested)
            {
                try
                {
                    await SendControlMessageAsync("CloseStream", CancellationToken.None);
                }
                catch
                {
                }
            }

            if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent)
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

            try
            {
                await _receiveLoopTask;
            }
            catch
            {
            }

            DiagnosticsLogger.Info("Deepgram session disposed.");
            _webSocket.Dispose();
            _receiveLoopCancellationTokenSource.Dispose();
            _sendLock.Dispose();
        }
    }

    private async Task SendControlMessageAsync(string type, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);

        try
        {
            if (_webSocket.State != WebSocketState.Open)
            {
                return;
            }

            var payload = Encoding.UTF8.GetBytes($"{{\"type\":\"{type}\"}}");
            await _webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
            DiagnosticsLogger.Info($"Deepgram control message sent. Type={type}.");

            if (type == "CloseStream")
            {
                _closeStreamRequested = true;
            }
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
                        DiagnosticsLogger.Info($"Deepgram server initiated close. CloseStatus={result.CloseStatus}, CloseStatusDescription='{result.CloseStatusDescription}'.");
                        await FlushPendingFluxTranscriptAsync(CancellationToken.None);
                        _completionSource.TrySetResult(GetFinalTranscriptSnapshot());
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
                DiagnosticsLogger.Info($"Deepgram text message received. Length={message.Length}, Preview='{CreateTranscriptPreview(message)}'.");
                await HandleMessageAsync(message, _receiveLoopCancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            DiagnosticsLogger.Info("Deepgram receive loop cancelled.");
            _completionSource.TrySetResult(GetFinalTranscriptSnapshot());
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error("Deepgram receive loop failed.", exception);
            _completionSource.TrySetException(WrapException(exception));
        }
    }

    private async Task HandleMessageAsync(string message, CancellationToken cancellationToken)
    {
        StreamingMessageEnvelope? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<StreamingMessageEnvelope>(message, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new TranscriptionException("Deepgram returned an invalid streaming response.", true, null, exception);
        }

        if (envelope is null)
        {
            return;
        }

        if (string.Equals(envelope.Type, "Connected", StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticsLogger.Info($"Deepgram connected message received. RequestId='{envelope.RequestId ?? string.Empty}', SequenceId={envelope.SequenceId?.ToString() ?? string.Empty}.");
            return;
        }

        if (string.Equals(envelope.Type, "TurnInfo", StringComparison.OrdinalIgnoreCase))
        {
            await HandleFluxTurnInfoAsync(envelope, cancellationToken);
            return;
        }

        if (string.Equals(envelope.Type, "Results", StringComparison.OrdinalIgnoreCase))
        {
            await HandleLegacyResultsAsync(envelope, cancellationToken);
            return;
        }

        if (string.Equals(envelope.Type, "Metadata", StringComparison.OrdinalIgnoreCase) && _completionRequested)
        {
            DiagnosticsLogger.Info("Deepgram metadata received after completion request; completing session.");
            _completionSource.TrySetResult(GetFinalTranscriptSnapshot());
            return;
        }

        if (string.Equals(envelope.Type, "ConfigureSuccess", StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticsLogger.Info("Deepgram configure success received.");
            return;
        }

        if (string.Equals(envelope.Type, "ConfigureFailure", StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticsLogger.Info("Deepgram configure failure received.");
            return;
        }

        if (string.Equals(envelope.Type, "Error", StringComparison.OrdinalIgnoreCase))
        {
            var description = NormalizeTranscriptText(envelope.Description);
            var messageText = NormalizeTranscriptText(envelope.Message);
            var errorText = !string.IsNullOrWhiteSpace(description)
                ? description
                : !string.IsNullOrWhiteSpace(messageText)
                    ? messageText
                    : "Deepgram returned an error.";
            DiagnosticsLogger.Error($"Deepgram error message received. Code='{envelope.Code ?? string.Empty}', Description='{errorText}'.", new InvalidOperationException(errorText));
            throw new TranscriptionException(errorText, false);
        }

        DiagnosticsLogger.Info($"Deepgram unhandled message type received. Type='{envelope.Type ?? "<null>"}'.");
    }

    private async Task HandleFluxTurnInfoAsync(StreamingMessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var transcript = NormalizeTranscriptText(envelope.Transcript);
        DiagnosticsLogger.Info($"Deepgram TurnInfo received. Event='{envelope.Event ?? string.Empty}', TurnIndex={envelope.TurnIndex?.ToString() ?? string.Empty}, TranscriptLength={transcript.Length}, Confidence={envelope.EndOfTurnConfidence?.ToString() ?? string.Empty}, TranscriptPreview='{CreateTranscriptPreview(transcript)}'.");

        TrackFluxTurnUpdate(envelope.TurnIndex, transcript);

        if (!ShouldCommitFluxTurn(envelope.Event))
        {
            return;
        }

        var chunkToPaste = CommitFluxTurn(envelope.TurnIndex, transcript, envelope.Event ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(chunkToPaste))
        {
            await _transcriptChunkHandler(chunkToPaste, cancellationToken);
        }

        if (_completionRequested && string.Equals(envelope.Event, "EndOfTurn", StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticsLogger.Info("Deepgram completion satisfied by EndOfTurn event.");
            _completionSource.TrySetResult(GetFinalTranscriptSnapshot());
        }
    }

    private async Task HandleLegacyResultsAsync(StreamingMessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var transcript = NormalizeTranscriptText(envelope.Channel?.Alternatives?.FirstOrDefault()?.Transcript);
        DiagnosticsLogger.Info($"Deepgram results message. IsFinal={envelope.IsFinal}, FromFinalize={envelope.FromFinalize}, TranscriptLength={transcript.Length}, TranscriptPreview='{CreateTranscriptPreview(transcript)}'.");
        if (!envelope.IsFinal || string.IsNullOrWhiteSpace(transcript))
        {
            return;
        }

        var chunkToAppend = AppendLegacyTranscriptChunk(transcript);
        if (!string.IsNullOrWhiteSpace(chunkToAppend))
        {
            await _transcriptChunkHandler(chunkToAppend, cancellationToken);
        }

        if (_completionRequested && envelope.FromFinalize)
        {
            _completionSource.TrySetResult(GetFinalTranscriptSnapshot());
        }
    }

    private void TrackFluxTurnUpdate(double? turnIndex, string transcript)
    {
        lock (_transcriptLock)
        {
            if (turnIndex.HasValue && _activeTurnIndex != turnIndex)
            {
                _activeTurnIndex = turnIndex;
                _activeTurnLatestTranscript = string.Empty;
                _activeTurnCommittedTranscript = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(transcript))
            {
                return;
            }

            _activeTurnLatestTranscript = transcript;
        }
    }

    private string CommitFluxTurn(double? turnIndex, string transcript, string triggerEvent)
    {
        lock (_transcriptLock)
        {
            if (turnIndex.HasValue && _activeTurnIndex != turnIndex)
            {
                _activeTurnIndex = turnIndex;
                _activeTurnLatestTranscript = string.Empty;
                _activeTurnCommittedTranscript = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(transcript))
            {
                return string.Empty;
            }

            if (string.Equals(_activeTurnCommittedTranscript, transcript, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            var turnChunk = BuildStreamingAppendChunk(_activeTurnCommittedTranscript, transcript);
            if (string.IsNullOrWhiteSpace(turnChunk))
            {
                DiagnosticsLogger.Info($"Deepgram turn revision could not be appended safely. Event='{triggerEvent}', ExistingCommitted='{CreateTranscriptPreview(_activeTurnCommittedTranscript)}', Incoming='{CreateTranscriptPreview(transcript)}'.");
                return string.Empty;
            }

            var chunkToPaste = BuildAppendChunk(_completedTranscript, turnChunk);

            _completedTranscript += chunkToPaste;
            _activeTurnIndex = turnIndex;
            _activeTurnLatestTranscript = transcript;
            _activeTurnCommittedTranscript = transcript;

            return chunkToPaste;
        }
    }

    private async Task FlushPendingFluxTranscriptAsync(CancellationToken cancellationToken)
    {
        if (!IsFluxSession)
        {
            return;
        }

        string chunkToPaste;

        lock (_transcriptLock)
        {
            if (string.IsNullOrWhiteSpace(_activeTurnLatestTranscript))
            {
                return;
            }

            var pendingTurnChunk = BuildStreamingAppendChunk(_activeTurnCommittedTranscript, _activeTurnLatestTranscript);
            if (string.IsNullOrWhiteSpace(pendingTurnChunk))
            {
                return;
            }

            chunkToPaste = BuildAppendChunk(_completedTranscript, pendingTurnChunk);

            if (!string.IsNullOrWhiteSpace(chunkToPaste))
            {
                _completedTranscript += chunkToPaste;
                _activeTurnCommittedTranscript = _activeTurnLatestTranscript;
            }
        }

        if (!string.IsNullOrWhiteSpace(chunkToPaste))
        {
            DiagnosticsLogger.Info($"Flushing pending Deepgram transcript chunk on completion. TextLength={chunkToPaste.Length}, Preview='{CreateTranscriptPreview(chunkToPaste)}'.");
            await _transcriptChunkHandler(chunkToPaste, cancellationToken);
        }
    }

    private string AppendLegacyTranscriptChunk(string transcript)
    {
        lock (_transcriptLock)
        {
            var chunk = BuildAppendChunk(_completedTranscript, transcript);
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                _completedTranscript += chunk;
            }

            return chunk;
        }
    }

    private string GetFinalTranscriptSnapshot()
    {
        lock (_transcriptLock)
        {
            var transcript = _completedTranscript;

            if (!string.IsNullOrWhiteSpace(_activeTurnLatestTranscript))
            {
                var pendingTurnChunk = BuildStreamingAppendChunk(_activeTurnCommittedTranscript, _activeTurnLatestTranscript);
                if (!string.IsNullOrWhiteSpace(pendingTurnChunk))
                {
                    transcript += BuildAppendChunk(transcript, pendingTurnChunk);
                }
            }

            return transcript.Trim();
        }
    }

    private static bool ShouldCommitFluxTurn(string? eventName)
    {
        return string.Equals(eventName, "EagerEndOfTurn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventName, "EndOfTurn", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTranscriptText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string BuildStreamingAppendChunk(string existingTranscript, string nextTranscript)
    {
        if (string.IsNullOrWhiteSpace(nextTranscript))
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(existingTranscript))
        {
            return nextTranscript;
        }

        if (!nextTranscript.StartsWith(existingTranscript, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return nextTranscript[existingTranscript.Length..];
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

    private static TranscriptionException WrapException(Exception exception)
    {
        return exception as TranscriptionException
            ?? new TranscriptionException("The Deepgram streaming session failed.", true, null, exception);
    }

    private static string CreateTranscriptPreview(string transcript)
    {
        if (string.IsNullOrEmpty(transcript))
        {
            return string.Empty;
        }

        return transcript.Length <= 120 ? transcript : transcript[..120] + "...";
    }

    private sealed class StreamingMessageEnvelope
    {
        public string? Type { get; set; }

        [JsonPropertyName("is_final")]
        public bool IsFinal { get; set; }

        [JsonPropertyName("from_finalize")]
        public bool FromFinalize { get; set; }

        public string? Message { get; set; }

        public string? Description { get; set; }

        public string? Code { get; set; }

        public string? Event { get; set; }

        public string? Transcript { get; set; }

        [JsonPropertyName("turn_index")]
        public double? TurnIndex { get; set; }

        [JsonPropertyName("end_of_turn_confidence")]
        public double? EndOfTurnConfidence { get; set; }

        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }

        [JsonPropertyName("sequence_id")]
        public double? SequenceId { get; set; }

        public StreamingChannel? Channel { get; set; }
    }

    private sealed class StreamingChannel
    {
        public List<StreamingAlternative>? Alternatives { get; set; }
    }

    private sealed class StreamingAlternative
    {
        public string? Transcript { get; set; }
    }
}
