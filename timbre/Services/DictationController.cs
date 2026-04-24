using NAudio.Wave;
using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class DictationController : IDictationController
{
    private const long GroqUploadLimitBytes = 25L * 1024 * 1024;
    private const long FireworksUploadLimitBytes = 1024L * 1024 * 1024;
    private const long DeepgramUploadLimitBytes = 2L * 1024 * 1024 * 1024;
    private const long ElevenLabsUploadLimitBytes = 3L * 1024 * 1024 * 1024;
    private const int MistralFastStreamingDelayMs = 240;
    private const int MistralSlowStreamingDelayMs = 2400;
    private const int MaxRetryCount = 2;
    private static readonly TimeSpan MinimumTranscribableDuration = TimeSpan.FromSeconds(0.25);

    private readonly IAppSettingsStore _settingsStore;
    private readonly IAudioDeviceService _audioDeviceService;
    private readonly ITranscriptionClientFactory _transcriptionClientFactory;
    private readonly ITextInsertionService _textInsertionService;
    private readonly ITranscriptHistoryStore _transcriptHistoryStore;
    private readonly INotificationService _notificationService;
    private readonly LlmTranscriptPostProcessor _llmTranscriptPostProcessor;
    private readonly DeepgramStreamingTranscriptionClient _deepgramStreamingClient;
    private readonly MistralRealtimeTranscriptionClient _mistralRealtimeClient;
    private readonly ElevenLabsRealtimeTranscriptionClient _elevenLabsRealtimeClient;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _streamingPasteLock = new(1, 1);
    private readonly object _streamingFailureLock = new();
    private readonly object _streamingSendChainLock = new();

    private AudioRecorder? _activeRecorder;
    private IRealtimeTranscriptionSession? _activeStreamingSession;
    private CancellationTokenSource? _transcriptionCancellationTokenSource;
    private Exception? _streamingFailure;
    private Task _streamingSendChain = Task.CompletedTask;
    private bool _isTranscribing;
    private string? _lastTranscript;

    public DictationController(
        IAppSettingsStore settingsStore,
        IAudioDeviceService audioDeviceService,
        ITranscriptionClientFactory transcriptionClientFactory,
        ITextInsertionService textInsertionService,
        ITranscriptHistoryStore transcriptHistoryStore,
        INotificationService notificationService,
        LlmTranscriptPostProcessor llmTranscriptPostProcessor,
        DeepgramStreamingTranscriptionClient deepgramStreamingClient,
        MistralRealtimeTranscriptionClient mistralRealtimeClient,
        ElevenLabsRealtimeTranscriptionClient elevenLabsRealtimeClient)
    {
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;
        _transcriptionClientFactory = transcriptionClientFactory;
        _textInsertionService = textInsertionService;
        _transcriptHistoryStore = transcriptHistoryStore;
        _notificationService = notificationService;
        _llmTranscriptPostProcessor = llmTranscriptPostProcessor;
        _deepgramStreamingClient = deepgramStreamingClient;
        _mistralRealtimeClient = mistralRealtimeClient;
        _elevenLabsRealtimeClient = elevenLabsRealtimeClient;
    }

    public event EventHandler<DictationStatusChangedEventArgs>? StatusChanged;

    public async Task<bool> StartDictationAsync()
    {
        await _stateLock.WaitAsync();

        try
        {
            if (_activeRecorder is not null)
            {
                return false;
            }

            if (_isTranscribing)
            {
                PublishStatus(DictationState.Transcribing, "Transcription in progress. Wait for the previous dictation to finish.", true);
                _notificationService.ShowNotification("Transcription in progress", "Wait for the previous dictation to finish before recording again.", true);
                return false;
            }

            var settings = _settingsStore.CurrentSettings;
            DiagnosticsLogger.Info($"Starting dictation. Provider={settings.Provider}, PushToTalk={settings.PushToTalk}.");
            if (!HasProviderApiKey(settings))
            {
                PublishStatus(DictationState.Error, GetMissingApiKeyMessage(settings.Provider), false);
                _notificationService.ShowNotification("API key missing", GetMissingApiKeyMessage(settings.Provider), true);
                return false;
            }

            if (settings.LlmPostProcessingEnabled && !HasLlmPostProcessingApiKey(settings))
            {
                var missingLlmApiKeyMessage = GetMissingLlmPostProcessingApiKeyMessage(settings.LlmPostProcessingProvider);
                PublishStatus(DictationState.Error, missingLlmApiKeyMessage, false);
                _notificationService.ShowNotification("LLM API key missing", missingLlmApiKeyMessage, true);
                return false;
            }

            var recorder = new AudioRecorder();
            IRealtimeTranscriptionSession? streamingSession = null;
            CancellationTokenSource? transcriptionCancellationTokenSource = null;

            try
            {
                if (UsesRealtimeStreaming(settings))
                {
                    DiagnosticsLogger.Info($"Preparing {GetProviderDisplayName(settings.Provider)} live streaming session before microphone start.");
                    transcriptionCancellationTokenSource = new CancellationTokenSource();
                    ResetStreamingFailure();
                    ResetStreamingSendChain();
                    streamingSession = await CreateRealtimeStreamingSessionAsync(settings, transcriptionCancellationTokenSource.Token);
                    recorder.ChunkAvailable += OnRecorderChunkAvailable;
                    _activeStreamingSession = streamingSession;
                    _transcriptionCancellationTokenSource = transcriptionCancellationTokenSource;
                }

                var device = _audioDeviceService.OpenPreferredCaptureDevice(settings.SelectedInputDeviceId);
                recorder.Start(device);
                _activeRecorder = recorder;
                DiagnosticsLogger.Info($"Recording started. Provider={settings.Provider}, Device='{recorder.DeviceName}', WaveFormat='{recorder.WaveFormatDescription}'.");
                PublishStatus(
                    DictationState.Recording,
                    UsesRealtimeStreaming(settings)
                        ? "Recording and streaming... release or press the hotkey again to stop."
                        : "Recording... release or press the hotkey again to stop.",
                    false);
                return true;
            }
            catch (Exception exception)
            {
                recorder.ChunkAvailable -= OnRecorderChunkAvailable;
                recorder.Dispose();

                if (streamingSession is not null)
                {
                    await streamingSession.DisposeAsync();
                }

                transcriptionCancellationTokenSource?.Cancel();
                transcriptionCancellationTokenSource?.Dispose();
                _activeStreamingSession = null;
                _transcriptionCancellationTokenSource = null;
                PublishStatus(DictationState.Error, exception.Message, false);
                _notificationService.ShowNotification(
                    UsesRealtimeStreaming(settings) && streamingSession is null
                        ? $"{GetProviderDisplayName(settings.Provider)} connection failed"
                        : "Microphone unavailable",
                    exception.Message,
                    true);
                return false;
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<bool> StopDictationAsync()
    {
        AudioRecorder? recorder;
        IRealtimeTranscriptionSession? streamingSession;

        await _stateLock.WaitAsync();

        try
        {
            recorder = _activeRecorder;
            streamingSession = _activeStreamingSession;

            if (recorder is null)
            {
                return false;
            }

            _activeRecorder = null;
            _activeStreamingSession = null;
            _isTranscribing = true;
        }
        finally
        {
            _stateLock.Release();
        }

        try
        {
            DiagnosticsLogger.Info($"Stopping dictation. HasStreamingSession={streamingSession is not null}.");
            recorder.ChunkAvailable -= OnRecorderChunkAvailable;

            byte[] audioBytes;

            try
            {
                audioBytes = await recorder.StopAsync();
            }
            catch (Exception exception)
            {
                PublishStatus(DictationState.Error, exception.Message, false);
                _notificationService.ShowNotification("Recording failed", exception.Message, true);
                return true;
            }

            if (audioBytes.Length == 0)
            {
                PublishStatus(DictationState.Error, "No audio was captured.", false);
                _notificationService.ShowNotification("Recording failed", "No audio was captured.", true);
                return true;
            }

            var audioDuration = GetAudioDuration(audioBytes);
            DiagnosticsLogger.Info($"Recording stopped. AudioBytes={audioBytes.Length}, DurationMs={audioDuration.TotalMilliseconds:F0}, Provider={_settingsStore.CurrentSettings.Provider}.");
            if (audioDuration < MinimumTranscribableDuration)
            {
                DiagnosticsLogger.Info(
                    $"Recording ignored because it was shorter than the minimum transcription threshold. DurationMs={audioDuration.TotalMilliseconds:F0}, ThresholdMs={MinimumTranscribableDuration.TotalMilliseconds:F0}, AudioBytes={audioBytes.Length}.");

                if (streamingSession is not null)
                {
                    await streamingSession.DisposeAsync();
                }

                PublishStatus(DictationState.Idle, string.Empty, false);
                return true;
            }

            var settings = _settingsStore.CurrentSettings;
            if (!HasProviderApiKey(settings))
            {
                if (streamingSession is not null)
                {
                    await streamingSession.DisposeAsync();
                }

                PublishStatus(DictationState.Error, GetMissingApiKeyMessage(settings.Provider), false);
                _notificationService.ShowNotification("API key missing", GetMissingApiKeyMessage(settings.Provider), true);
                return true;
            }

            if (TryGetUploadLimitBytes(settings, out var uploadLimitBytes) && audioBytes.LongLength > uploadLimitBytes)
            {
                if (streamingSession is not null)
                {
                    await streamingSession.DisposeAsync();
                }

                var providerName = GetProviderDisplayName(settings.Provider);
                PublishStatus(DictationState.Error, $"The recording exceeded {providerName}'s upload limit.", false);
                _notificationService.ShowNotification("Recording too large", $"The recording exceeded {providerName}'s speech-to-text upload limit.", true);
                return true;
            }

            _transcriptionCancellationTokenSource ??= new CancellationTokenSource();
            PublishStatus(
                DictationState.Transcribing,
                UsesRealtimeStreaming(settings) ? $"Finalizing {GetProviderDisplayName(settings.Provider)} transcript..." : "Transcribing...",
                true);

            string transcription;

            try
            {
                transcription = streamingSession is not null
                    ? await FinalizeRealtimeStreamingAsync(streamingSession, settings.Provider, _transcriptionCancellationTokenSource.Token)
                    : await TranscribeWithRetryAsync(audioBytes, settings, _transcriptionCancellationTokenSource.Token);

                DiagnosticsLogger.Info($"Transcription completed. Provider={settings.Provider}, TranscriptLength={transcription.Length}.");
                transcription = await ApplyLlmPostProcessingWithFallbackAsync(transcription, settings, _transcriptionCancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                PublishStatus(DictationState.Idle, "Transcription cancelled.", false);
                return true;
            }
            catch (Exception exception)
            {
                PublishStatus(DictationState.Error, exception.Message, false);
                _notificationService.ShowNotification("Transcription failed", exception.Message, true);
                return true;
            }

            await _stateLock.WaitAsync();

            try
            {
                _lastTranscript = transcription;
            }
            finally
            {
                _stateLock.Release();
            }

            try
            {
                await _transcriptHistoryStore.AppendAsync(transcription, settings.TranscriptHistoryLimit);
            }
            catch (Exception exception)
            {
                _notificationService.ShowNotification("Transcript history failed", exception.Message, true);
            }

            if (streamingSession is null || !UsesLiveChunkPasting(settings))
            {
                try
                {
                    await _textInsertionService.InsertTextAsync(transcription);
                }
                catch (Exception exception)
                {
                    PublishStatus(DictationState.Error, exception.Message, false);
                    _notificationService.ShowNotification("Text insertion failed", exception.Message, true);
                    return true;
                }
            }

            PublishStatus(DictationState.Idle, "Transcript inserted successfully.", false);
            return true;
        }
        finally
        {
            recorder.Dispose();

            await _stateLock.WaitAsync();

            try
            {
                _transcriptionCancellationTokenSource?.Dispose();
                _transcriptionCancellationTokenSource = null;
                _isTranscribing = false;
                ResetStreamingFailure();
                ResetStreamingSendChain();
            }
            finally
            {
                _stateLock.Release();
            }
        }
    }

    public async Task<bool> CancelTranscriptionAsync()
    {
        await _stateLock.WaitAsync();

        try
        {
            if (!_isTranscribing || _transcriptionCancellationTokenSource is null)
            {
                return false;
            }

            PublishStatus(DictationState.Cancelling, "Cancelling transcription...", false);
            _transcriptionCancellationTokenSource.Cancel();
            return true;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<bool> PasteLastTranscriptAsync(HotkeyBinding? triggeringHotkey = null)
    {
        string? lastTranscript = null;

        try
        {
            lastTranscript = await _transcriptHistoryStore.GetLatestTranscriptAsync();
        }
        catch
        {
        }

        await _stateLock.WaitAsync();

        try
        {
            lastTranscript ??= _lastTranscript;
        }
        finally
        {
            _stateLock.Release();
        }

        if (string.IsNullOrWhiteSpace(lastTranscript))
        {
            PublishStatus(DictationState.Error, "No transcript is available yet.", false);
            _notificationService.ShowNotification("No transcript available", "Record and transcribe audio before using paste last transcript.", true);
            return false;
        }

        try
        {
            await _textInsertionService.InsertTextAsync(lastTranscript, triggeringHotkey);
            PublishStatus(DictationState.Idle, "Latest transcript inserted.", false);
            return true;
        }
        catch (Exception exception)
        {
            PublishStatus(DictationState.Error, exception.Message, false);
            _notificationService.ShowNotification("Text insertion failed", exception.Message, true);
            return false;
        }
    }

    public void Dispose()
    {
        _transcriptionCancellationTokenSource?.Cancel();
        _transcriptionCancellationTokenSource?.Dispose();

        if (_activeRecorder is not null)
        {
            _activeRecorder.ChunkAvailable -= OnRecorderChunkAvailable;
            _activeRecorder.Dispose();
        }

        if (_activeStreamingSession is not null)
        {
            try
            {
                _activeStreamingSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        _streamingPasteLock.Dispose();
        _stateLock.Dispose();
    }

    internal async Task<string> ApplyLlmPostProcessingWithFallbackAsync(
        string transcription,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.LlmPostProcessingEnabled)
        {
            return transcription;
        }

        var providerName = GetLlmPostProcessingProviderDisplayName(settings.LlmPostProcessingProvider);

        try
        {
            PublishStatus(
                DictationState.Transcribing,
                $"Cleaning transcript with {providerName}...",
                true);

            var cleanedTranscript = await _llmTranscriptPostProcessor.CleanTranscriptAsync(transcription, settings, cancellationToken);
            DiagnosticsLogger.Info(
                $"LLM transcript post-processing completed. Provider={settings.LlmPostProcessingProvider}, TranscriptLength={cleanedTranscript.Length}.");
            return cleanedTranscript;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error(
                $"LLM transcript post-processing failed. Provider={settings.LlmPostProcessingProvider}. Falling back to raw transcript.",
                exception);

            PublishStatus(
                DictationState.Transcribing,
                $"{providerName} post-processing failed. Inserting the raw transcript instead.",
                true);
            _notificationService.ShowNotification(
                $"{providerName} post-processing failed",
                FormatPostProcessingFallbackNotificationMessage(exception.Message),
                true);

            return transcription;
        }
    }

    private async Task<string> TranscribeWithRetryAsync(byte[] audioBytes, AppSettings settings, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (attempt > 1)
                {
                    PublishStatus(DictationState.Transcribing, $"Retrying transcription ({attempt}/{MaxRetryCount + 1})...", true);
                }

                return await GetTranscriptionClient(settings).TranscribeAsync(
                    audioBytes,
                    GetApiKey(settings),
                    GetModel(settings),
                    GetLanguage(settings),
                    cancellationToken);
            }
            catch (TranscriptionException exception) when (exception.IsTransient && attempt <= MaxRetryCount)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken);
            }
        }
    }

    private async Task<IRealtimeTranscriptionSession> CreateRealtimeStreamingSessionAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        return settings.Provider switch
        {
            TranscriptionProvider.Deepgram => await _deepgramStreamingClient.ConnectAsync(
                GetApiKey(settings),
                GetModel(settings),
                GetLanguage(settings),
                AppendStreamingTranscriptChunkAsync,
                cancellationToken),
            TranscriptionProvider.Mistral => await _mistralRealtimeClient.ConnectAsync(
                GetApiKey(settings),
                GetModel(settings),
                GetMistralStreamingDelayMilliseconds(settings),
                AppendStreamingTranscriptChunkAsync,
                cancellationToken),
            TranscriptionProvider.ElevenLabs => await _elevenLabsRealtimeClient.ConnectAsync(
                GetApiKey(settings),
                GetModel(settings),
                GetLanguage(settings),
                AppendStreamingTranscriptChunkAsync,
                cancellationToken),
            _ => throw new InvalidOperationException($"{GetProviderDisplayName(settings.Provider)} does not support realtime streaming."),
        };
    }

    private async Task<string> FinalizeRealtimeStreamingAsync(
        IRealtimeTranscriptionSession streamingSession,
        TranscriptionProvider provider,
        CancellationToken cancellationToken)
    {
        var failure = GetStreamingFailure();
        if (failure is not null)
        {
            throw WrapStreamingException(failure);
        }

        try
        {
            await WaitForPendingStreamingSendsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (GetStreamingFailure() is { } pendingSendFailure)
        {
            throw WrapStreamingException(pendingSendFailure);
        }

        failure = GetStreamingFailure();
        if (failure is not null)
        {
            throw WrapStreamingException(failure);
        }

        var transcription = await streamingSession.CompleteAsync(cancellationToken);
        failure = GetStreamingFailure();
        if (failure is not null)
        {
            throw WrapStreamingException(failure);
        }

        if (string.IsNullOrWhiteSpace(transcription))
        {
            throw new TranscriptionException($"{GetProviderDisplayName(provider)} returned an empty transcription.", false);
        }

        return transcription.Trim();
    }

    private async Task AppendStreamingTranscriptChunkAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (_settingsStore.CurrentSettings.LlmPostProcessingEnabled)
        {
            return;
        }

        DiagnosticsLogger.Info($"Inserting realtime transcript chunk. TextLength={text.Length}, Preview='{CreateTranscriptPreview(text)}'.");
        await _streamingPasteLock.WaitAsync(cancellationToken);

        try
        {
            await _textInsertionService.InsertTextAsync(text, cancellationToken: cancellationToken);
        }
        finally
        {
            _streamingPasteLock.Release();
        }
    }

    private void OnRecorderChunkAvailable(object? sender, AudioChunkAvailableEventArgs eventArgs)
    {
        var streamingSession = _activeStreamingSession;
        var cancellationTokenSource = _transcriptionCancellationTokenSource;

        if (streamingSession is null || cancellationTokenSource is null || cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        EnqueueStreamingAudioChunk(streamingSession, eventArgs.AudioBytes, cancellationTokenSource.Token);
    }

    private void EnqueueStreamingAudioChunk(IRealtimeTranscriptionSession streamingSession, byte[] audioBytes, CancellationToken cancellationToken)
    {
        lock (_streamingSendChainLock)
        {
            _streamingSendChain = _streamingSendChain
                .ContinueWith(
                    _ => SendStreamingAudioChunkAsync(streamingSession, audioBytes, cancellationToken),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task SendStreamingAudioChunkAsync(IRealtimeTranscriptionSession streamingSession, byte[] audioBytes, CancellationToken cancellationToken)
    {
        try
        {
            await streamingSession.SendAudioAsync(audioBytes, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HandleStreamingFailure(exception);
        }
    }

    private async Task WaitForPendingStreamingSendsAsync(CancellationToken cancellationToken)
    {
        Task pendingSends;

        lock (_streamingSendChainLock)
        {
            pendingSends = _streamingSendChain;
        }

        await pendingSends.WaitAsync(cancellationToken);
    }

    private void ResetStreamingSendChain()
    {
        lock (_streamingSendChainLock)
        {
            _streamingSendChain = Task.CompletedTask;
        }
    }

    private void HandleStreamingFailure(Exception exception)
    {
        lock (_streamingFailureLock)
        {
            _streamingFailure ??= exception;
        }

        DiagnosticsLogger.Error("Realtime transcription streaming failed.", exception);
        _transcriptionCancellationTokenSource?.Cancel();
    }

    private static string CreateTranscriptPreview(string transcript)
    {
        if (string.IsNullOrEmpty(transcript))
        {
            return string.Empty;
        }

        return transcript.Length <= 80 ? transcript : transcript[..80] + "...";
    }

    private Exception? GetStreamingFailure()
    {
        lock (_streamingFailureLock)
        {
            return _streamingFailure;
        }
    }

    private void ResetStreamingFailure()
    {
        lock (_streamingFailureLock)
        {
            _streamingFailure = null;
        }
    }

    private void PublishStatus(DictationState state, string message, bool canCancel)
    {
        DiagnosticsLogger.Info($"Dictation status changed. State={state}, Message='{message}', CanCancel={canCancel}.");
        StatusChanged?.Invoke(this, new DictationStatusChangedEventArgs(state, message, canCancel));
    }

    private ITranscriptionClient GetTranscriptionClient(AppSettings settings)
    {
        return _transcriptionClientFactory.GetClient(settings.Provider);
    }

    private static bool HasProviderApiKey(AppSettings settings)
    {
        return !string.IsNullOrWhiteSpace(GetApiKey(settings));
    }

    private static bool HasLlmPostProcessingApiKey(AppSettings settings)
    {
        return !string.IsNullOrWhiteSpace(GetLlmPostProcessingApiKey(settings));
    }

    private static bool UsesRealtimeStreaming(AppSettings settings)
    {
        return UsesDeepgramStreaming(settings) || UsesMistralStreaming(settings) || UsesElevenLabsRealtime(settings);
    }

    private static bool UsesLiveChunkPasting(AppSettings settings)
    {
        return !settings.LlmPostProcessingEnabled && (UsesDeepgramStreaming(settings) || UsesMistralStreaming(settings) || UsesElevenLabsRealtime(settings));
    }

    private static bool UsesDeepgramStreaming(AppSettings settings)
    {
        return settings.Provider == TranscriptionProvider.Deepgram && settings.DeepgramStreamingEnabled;
    }

    private static bool UsesMistralStreaming(AppSettings settings)
    {
        return settings.Provider == TranscriptionProvider.Mistral && settings.MistralStreamingEnabled;
    }

    private static bool UsesElevenLabsRealtime(AppSettings settings)
    {
        return settings.Provider == TranscriptionProvider.ElevenLabs && settings.ElevenLabsStreamingEnabled;
    }

    private static string GetApiKey(AppSettings settings)
    {
        return settings.Provider switch
        {
            TranscriptionProvider.Fireworks => settings.FireworksApiKey ?? string.Empty,
            TranscriptionProvider.Deepgram => settings.DeepgramApiKey ?? string.Empty,
            TranscriptionProvider.Mistral => settings.MistralApiKey ?? string.Empty,
            TranscriptionProvider.Cohere => settings.CohereApiKey ?? string.Empty,
            TranscriptionProvider.ElevenLabs => settings.ElevenLabsApiKey ?? string.Empty,
            _ => settings.GroqApiKey ?? string.Empty,
        };
    }

    private static string GetLlmPostProcessingApiKey(AppSettings settings)
    {
        return settings.LlmPostProcessingProvider == LlmPostProcessingProvider.Groq
            ? settings.LlmGroqApiKey ?? string.Empty
            : settings.CerebrasApiKey ?? string.Empty;
    }

    private static string GetModel(AppSettings settings)
    {
        return settings.Provider switch
        {
            TranscriptionProvider.Fireworks => settings.FireworksModel,
            TranscriptionProvider.Deepgram => settings.DeepgramModel,
            TranscriptionProvider.Mistral => TranscriptionModelCatalog.NormalizeMistralModel(settings.MistralModel, settings.MistralStreamingEnabled),
            TranscriptionProvider.Cohere => settings.CohereModel,
            TranscriptionProvider.ElevenLabs => TranscriptionModelCatalog.NormalizeElevenLabsModel(settings.ElevenLabsModel, settings.ElevenLabsStreamingEnabled),
            _ => settings.GroqModel,
        };
    }

    private static string GetLanguage(AppSettings settings)
    {
        return settings.Provider switch
        {
            TranscriptionProvider.Fireworks => settings.FireworksLanguage,
            TranscriptionProvider.Deepgram => settings.DeepgramLanguage,
            TranscriptionProvider.Mistral => "en",
            TranscriptionProvider.Cohere => settings.CohereLanguage,
            TranscriptionProvider.ElevenLabs => settings.ElevenLabsLanguage,
            _ => settings.GroqLanguage,
        };
    }

    private static int GetMistralStreamingDelayMilliseconds(AppSettings settings)
    {
        return settings.MistralRealtimeMode == MistralRealtimeMode.Slow
            ? MistralSlowStreamingDelayMs
            : MistralFastStreamingDelayMs;
    }

    private static string GetMissingApiKeyMessage(TranscriptionProvider provider)
    {
        return provider switch
        {
            TranscriptionProvider.Fireworks => "Open Settings and save a Fireworks API key before dictating.",
            TranscriptionProvider.Deepgram => "Open Settings and save a Deepgram API key before dictating.",
            TranscriptionProvider.Mistral => "Open Settings and save a Mistral API key before dictating.",
            TranscriptionProvider.Cohere => "Open Settings and save a Cohere API key before dictating.",
            TranscriptionProvider.ElevenLabs => "Open Settings and save an ElevenLabs API key before dictating.",
            _ => "Open Settings and save a Groq API key before dictating.",
        };
    }

    private static string GetMissingLlmPostProcessingApiKeyMessage(LlmPostProcessingProvider provider)
    {
        return provider == LlmPostProcessingProvider.Groq
            ? "Open Settings and save a Groq API key for LLM post-processing before dictating."
            : "Open Settings and save a Cerebras API key for LLM post-processing before dictating.";
    }

    private static bool TryGetUploadLimitBytes(AppSettings settings, out long uploadLimitBytes)
    {
        switch (settings.Provider)
        {
            case TranscriptionProvider.Fireworks:
                uploadLimitBytes = FireworksUploadLimitBytes;
                return true;
            case TranscriptionProvider.Deepgram:
                uploadLimitBytes = DeepgramUploadLimitBytes;
                return true;
            case TranscriptionProvider.ElevenLabs:
                uploadLimitBytes = ElevenLabsUploadLimitBytes;
                return true;
            case TranscriptionProvider.Groq:
                uploadLimitBytes = GroqUploadLimitBytes;
                return true;
            default:
                uploadLimitBytes = 0;
                return false;
        }
    }

    private static string GetProviderDisplayName(TranscriptionProvider provider)
    {
        return provider switch
        {
            TranscriptionProvider.Fireworks => "Fireworks",
            TranscriptionProvider.Deepgram => "Deepgram",
            TranscriptionProvider.Mistral => "Mistral",
            TranscriptionProvider.Cohere => "Cohere",
            TranscriptionProvider.ElevenLabs => "ElevenLabs",
            _ => "Groq",
        };
    }

    private static string GetLlmPostProcessingProviderDisplayName(LlmPostProcessingProvider provider)
    {
        return provider == LlmPostProcessingProvider.Groq ? "Groq" : "Cerebras";
    }

    private static string FormatPostProcessingFallbackNotificationMessage(string errorMessage)
    {
        var message = string.IsNullOrWhiteSpace(errorMessage)
            ? "The post-processing request failed."
            : errorMessage.Trim();

        if (!message.EndsWith('.') && !message.EndsWith('!') && !message.EndsWith('?'))
        {
            message += ".";
        }

        return $"Error: {message} Inserting the raw transcript instead.";
    }

    private static TimeSpan GetAudioDuration(byte[] audioBytes)
    {
        using var audioStream = new MemoryStream(audioBytes);
        using var waveReader = new WaveFileReader(audioStream);
        return waveReader.TotalTime;
    }

    private static Exception WrapStreamingException(Exception exception)
    {
        return exception as TranscriptionException
            ?? new TranscriptionException(exception.Message, true, null, exception);
    }
}
