using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class MistralRealtimeTranscriptionClient
{
    private static readonly Uri Endpoint = new("wss://api.mistral.ai/v1/audio/transcriptions/realtime");
    private const string Model = "voxtral-mini-transcribe-realtime-2602";
    private const string AudioEncoding = "pcm_s16le";
    private const int SampleRate = 16000;
    private const int MaxAudioBytesPerAppend = 262144;

    public async Task<MistralRealtimeSession> ConnectAsync(
        string apiKey,
        int targetStreamingDelayMs,
        Func<string, CancellationToken, Task>? transcriptChunkHandler = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new TranscriptionException("The Mistral API key is missing.", false);
        }

        if (targetStreamingDelayMs <= 0)
        {
            throw new TranscriptionException("The Mistral streaming delay is invalid.", false);
        }

        var endpoint = BuildEndpoint();
        var webSocket = new ClientWebSocket();
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        webSocket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey.Trim()}");
        MistralRealtimeSession? session = null;

        try
        {
            DiagnosticsLogger.Info(
                $"Mistral realtime connection starting. Endpoint={endpoint}, Model={Model}, TargetDelayMs={targetStreamingDelayMs}, AudioEncoding={AudioEncoding}, SampleRate={SampleRate}.");

            await webSocket.ConnectAsync(endpoint, cancellationToken);

            session = new MistralRealtimeSession(webSocket, targetStreamingDelayMs, transcriptChunkHandler);
            await session.InitializeAsync(cancellationToken);
            DiagnosticsLogger.Info(
                $"Mistral realtime connection established. Endpoint={endpoint}, Model={Model}, TargetDelayMs={targetStreamingDelayMs}.");
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

            throw new TranscriptionException("Connecting to Mistral timed out.", true);
        }
        catch (Exception exception)
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
            else
            {
                webSocket.Dispose();
            }

            throw exception as TranscriptionException
                ?? new TranscriptionException($"The Mistral realtime connection failed: {exception.Message}", true, null, exception);
        }
    }

    private static Uri BuildEndpoint()
    {
        return new UriBuilder(Endpoint)
        {
            Query = $"model={Uri.EscapeDataString(Model)}",
        }.Uri;
    }

    public sealed class MistralRealtimeSession : IRealtimeTranscriptionSession
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };
        private static readonly TimeSpan SessionInitializationTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(12);

        private readonly ClientWebSocket _webSocket;
        private readonly int _targetStreamingDelayMs;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly CancellationTokenSource _receiveLoopCancellationTokenSource = new();
        private readonly TaskCompletionSource _sessionCreatedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _sessionUpdatedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _receiveLoopTask;
        private readonly StringBuilder _deltaTranscriptBuilder = new();
        private readonly object _transcriptLock = new();
        private readonly Func<string, CancellationToken, Task>? _transcriptChunkHandler;
        private string _finalTranscript = string.Empty;
        private string _committedTranscript = string.Empty;
        private string _detectedLanguage = string.Empty;

        public MistralRealtimeSession(
            ClientWebSocket webSocket,
            int targetStreamingDelayMs,
            Func<string, CancellationToken, Task>? transcriptChunkHandler)
        {
            _webSocket = webSocket;
            _targetStreamingDelayMs = targetStreamingDelayMs;
            _transcriptChunkHandler = transcriptChunkHandler;
            _receiveLoopTask = Task.Run(ReceiveLoopAsync);
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await _sessionCreatedSource.Task.WaitAsync(SessionInitializationTimeout, cancellationToken);
                await SendSessionUpdateAsync(cancellationToken);
                await _sessionUpdatedSource.Task.WaitAsync(SessionInitializationTimeout, cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new TranscriptionException("Mistral did not finish initializing the realtime session in time.", true, null, exception);
            }
        }

        public async Task SendAudioAsync(byte[] audioBytes, CancellationToken cancellationToken = default)
        {
            if (audioBytes.Length == 0)
            {
                return;
            }

            for (var offset = 0; offset < audioBytes.Length; offset += MaxAudioBytesPerAppend)
            {
                var chunkLength = Math.Min(MaxAudioBytesPerAppend, audioBytes.Length - offset);
                var base64Audio = Convert.ToBase64String(audioBytes, offset, chunkLength);
                await SendJsonMessageAsync(new InputAudioAppendMessage
                {
                    Audio = base64Audio,
                }, cancellationToken);
            }
        }

        public async Task<string> CompleteAsync(CancellationToken cancellationToken = default)
        {
            DiagnosticsLogger.Info($"Mistral realtime completion requested. TargetDelayMs={_targetStreamingDelayMs}.");
            await SendJsonMessageAsync(new InputAudioFlushMessage(), cancellationToken);
            await SendJsonMessageAsync(new InputAudioEndMessage(), cancellationToken);

            try
            {
                return await _completionSource.Task.WaitAsync(CompletionTimeout, cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new TranscriptionException("Mistral did not finish finalizing the realtime stream in time.", true, null, exception);
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

        private async Task SendSessionUpdateAsync(CancellationToken cancellationToken)
        {
            await SendJsonMessageAsync(new SessionUpdateMessage
            {
                Session = new SessionUpdatePayload
                {
                    AudioFormat = new AudioFormatPayload
                    {
                        Encoding = AudioEncoding,
                        SampleRate = SampleRate,
                    },
                    TargetStreamingDelayMs = _targetStreamingDelayMs,
                },
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
                    throw new TranscriptionException("The Mistral realtime connection is no longer open.", true);
                }

                await _webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
            }
            catch (WebSocketException exception)
            {
                throw new TranscriptionException("Sending data to Mistral failed.", true, null, exception);
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
                            _completionSource.TrySetResult(GetTranscriptSnapshot());
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
                    await HandleMessageAsync(message, _receiveLoopCancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException)
            {
                _completionSource.TrySetResult(GetTranscriptSnapshot());
            }
            catch (Exception exception)
            {
                _completionSource.TrySetException(
                    exception as TranscriptionException
                    ?? new TranscriptionException("The Mistral realtime session failed.", true, null, exception));
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
                throw new TranscriptionException("Mistral returned an invalid realtime response.", true, null, exception);
            }

            if (envelope is null || string.IsNullOrWhiteSpace(envelope.Type))
            {
                return;
            }

            switch (envelope.Type)
            {
                case "session.created":
                {
                    _sessionCreatedSource.TrySetResult();
                    return;
                }
                case "session.updated":
                {
                    _sessionUpdatedSource.TrySetResult();
                    return;
                }
                case "transcription.language":
                {
                    var languageMessage = JsonSerializer.Deserialize<TranscriptionLanguageMessage>(message, SerializerOptions);
                    _detectedLanguage = languageMessage?.AudioLanguage?.Trim() ?? string.Empty;
                    return;
                }
                case "transcription.text.delta":
                {
                    var deltaMessage = JsonSerializer.Deserialize<TranscriptionTextDeltaMessage>(message, SerializerOptions);
                    if (deltaMessage is not null && !string.IsNullOrEmpty(deltaMessage.Text))
                    {
                        await CommitDeltaAsync(deltaMessage, cancellationToken);
                    }

                    return;
                }
                case "transcription.segment":
                {
                    var segmentMessage = JsonSerializer.Deserialize<TranscriptionSegmentMessage>(message, SerializerOptions);
                    // We rely on text.delta for live pasting to avoid double-pasting segments.
                    if (segmentMessage is not null)
                    {
                        DiagnosticsLogger.Info($"Mistral realtime segment received. Start={segmentMessage.Start}, End={segmentMessage.End}, TextLength={segmentMessage.Text?.Length}");
                    }

                    return;
                }
                case "transcription.done":
                {
                    var doneMessage = JsonSerializer.Deserialize<TranscriptionDoneMessage>(message, SerializerOptions);
                    if (doneMessage is not null)
                    {
                        await FlushRemainingTranscriptAsync(doneMessage, cancellationToken);
                    }

                    lock (_transcriptLock)
                    {
                        _finalTranscript = NormalizeTranscriptText(doneMessage?.Text);
                    }

                    if (!string.IsNullOrWhiteSpace(doneMessage?.Language))
                    {
                        _detectedLanguage = doneMessage.Language.Trim();
                    }

                    _completionSource.TrySetResult(GetTranscriptSnapshot());
                    return;
                }
                case "error":
                {
                    var errorMessage = JsonSerializer.Deserialize<RealtimeErrorMessage>(message, SerializerOptions);
                    var errorText = errorMessage?.Error?.Message?.Trim();
                    throw new TranscriptionException(
                        string.IsNullOrWhiteSpace(errorText)
                            ? "Mistral returned a realtime transcription error."
                            : errorText,
                        false);
                }
                default:
                {
                    return;
                }
            }
        }

        private async Task CommitDeltaAsync(TranscriptionTextDeltaMessage deltaMessage, CancellationToken cancellationToken)
        {
            var deltaText = deltaMessage.Text;
            if (string.IsNullOrEmpty(deltaText))
            {
                return;
            }

            lock (_transcriptLock)
            {
                _deltaTranscriptBuilder.Append(deltaText);
                _committedTranscript += deltaText;
            }

            if (_transcriptChunkHandler is not null)
            {
                await _transcriptChunkHandler(deltaText, cancellationToken);
            }
        }

        private async Task FlushRemainingTranscriptAsync(TranscriptionDoneMessage doneMessage, CancellationToken cancellationToken)
        {
            if (_transcriptChunkHandler is null)
            {
                return;
            }

            string chunkToPaste;

            lock (_transcriptLock)
            {
                var finalTranscript = NormalizeTranscriptText(doneMessage.Text);
                if (string.IsNullOrWhiteSpace(finalTranscript))
                {
                    return;
                }

                var normalizedCommitted = NormalizeTranscriptText(_committedTranscript);
                var remainingTranscript = BuildStreamingAppendChunk(normalizedCommitted, finalTranscript);
                if (string.IsNullOrWhiteSpace(remainingTranscript))
                {
                    if (!string.IsNullOrWhiteSpace(normalizedCommitted) &&
                        !string.Equals(normalizedCommitted, finalTranscript, StringComparison.Ordinal))
                    {
                        DiagnosticsLogger.Info(
                            $"Mistral final transcript diverged from committed realtime chunks. CommittedLength={normalizedCommitted.Length}, FinalLength={finalTranscript.Length}.");
                    }

                    return;
                }

                chunkToPaste = BuildAppendChunk(normalizedCommitted, remainingTranscript);
                if (string.IsNullOrWhiteSpace(chunkToPaste))
                {
                    return;
                }

                _committedTranscript += chunkToPaste;
            }

            await _transcriptChunkHandler(chunkToPaste, cancellationToken);
        }

        private string GetTranscriptSnapshot()
        {
            lock (_transcriptLock)
            {
                if (!string.IsNullOrWhiteSpace(_finalTranscript))
                {
                    return _finalTranscript;
                }

                return _deltaTranscriptBuilder.ToString().Trim();
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

        private static bool NeedsSeparator(char previous, char next)
        {
            return !char.IsWhiteSpace(previous) && !char.IsWhiteSpace(next) && !IsPunctuation(next);
        }

        private static bool IsPunctuation(char value)
        {
            return char.IsPunctuation(value) || value is ')' or ']' or '}';
        }

        private sealed class RealtimeEnvelope
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }
        }

        private sealed class AudioFormatPayload
        {
            [JsonPropertyName("encoding")]
            public string Encoding { get; set; } = string.Empty;

            [JsonPropertyName("sample_rate")]
            public int SampleRate { get; set; }
        }

        private sealed class SessionUpdatePayload
        {
            [JsonPropertyName("audio_format")]
            public AudioFormatPayload? AudioFormat { get; set; }

            [JsonPropertyName("target_streaming_delay_ms")]
            public int TargetStreamingDelayMs { get; set; }
        }

        private sealed class SessionUpdateMessage
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = "session.update";

            [JsonPropertyName("session")]
            public SessionUpdatePayload? Session { get; set; }
        }

        private sealed class InputAudioAppendMessage
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = "input_audio.append";

            [JsonPropertyName("audio")]
            public string Audio { get; set; } = string.Empty;
        }

        private sealed class InputAudioFlushMessage
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = "input_audio.flush";
        }

        private sealed class InputAudioEndMessage
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = "input_audio.end";
        }

        private sealed class TranscriptionLanguageMessage
        {
            [JsonPropertyName("audio_language")]
            public string? AudioLanguage { get; set; }
        }

        private sealed class TranscriptionTextDeltaMessage
        {
            [JsonPropertyName("text")]
            public string? Text { get; set; }
        }

        private sealed class TranscriptionSegmentMessage
        {
            [JsonPropertyName("text")]
            public string? Text { get; set; }

            [JsonPropertyName("start")]
            public double? Start { get; set; }

            [JsonPropertyName("end")]
            public double? End { get; set; }
        }

        private sealed class TranscriptionDoneMessage
        {
            [JsonPropertyName("text")]
            public string? Text { get; set; }

            [JsonPropertyName("language")]
            public string? Language { get; set; }
        }

        private sealed class RealtimeErrorMessage
        {
            [JsonPropertyName("error")]
            public RealtimeErrorDetail? Error { get; set; }
        }

        private sealed class RealtimeErrorDetail
        {
            [JsonPropertyName("message")]
            public string? Message { get; set; }
        }
    }
}
