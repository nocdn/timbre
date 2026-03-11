using NAudio.Wave;
using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class DictationController : IDictationController
{
    private const long GroqUploadLimitBytes = 25L * 1024 * 1024;
    private const long FireworksUploadLimitBytes = 1024L * 1024 * 1024;
    private const long DeepgramUploadLimitBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxRetryCount = 2;
    private static readonly TimeSpan MinimumTranscribableDuration = TimeSpan.FromSeconds(0.25);

    private readonly IAppSettingsStore _settingsStore;
    private readonly IAudioDeviceService _audioDeviceService;
    private readonly ITranscriptionClientFactory _transcriptionClientFactory;
    private readonly IClipboardPasteService _clipboardPasteService;
    private readonly ITranscriptHistoryStore _transcriptHistoryStore;
    private readonly INotificationService _notificationService;
    private readonly DeepgramStreamingTranscriptionClient _deepgramStreamingClient;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _streamingPasteLock = new(1, 1);
    private readonly object _streamingFailureLock = new();

    private AudioRecorder? _activeRecorder;
    private DeepgramStreamingSession? _activeDeepgramSession;
    private CancellationTokenSource? _transcriptionCancellationTokenSource;
    private Exception? _streamingFailure;
    private bool _isTranscribing;
    private string? _lastTranscript;

    public DictationController(
        IAppSettingsStore settingsStore,
        IAudioDeviceService audioDeviceService,
        ITranscriptionClientFactory transcriptionClientFactory,
        IClipboardPasteService clipboardPasteService,
        ITranscriptHistoryStore transcriptHistoryStore,
        INotificationService notificationService,
        DeepgramStreamingTranscriptionClient deepgramStreamingClient)
    {
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;
        _transcriptionClientFactory = transcriptionClientFactory;
        _clipboardPasteService = clipboardPasteService;
        _transcriptHistoryStore = transcriptHistoryStore;
        _notificationService = notificationService;
        _deepgramStreamingClient = deepgramStreamingClient;
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

            var recorder = new AudioRecorder();
            DeepgramStreamingSession? deepgramSession = null;
            CancellationTokenSource? transcriptionCancellationTokenSource = null;

            try
            {
                if (UsesDeepgramStreaming(settings))
                {
                    DiagnosticsLogger.Info("Preparing Deepgram live streaming session before microphone start.");
                    transcriptionCancellationTokenSource = new CancellationTokenSource();
                    ResetStreamingFailure();
                    deepgramSession = await _deepgramStreamingClient.ConnectAsync(
                        GetApiKey(settings),
                        GetModel(settings),
                        GetLanguage(settings),
                        AppendStreamingTranscriptChunkAsync,
                        transcriptionCancellationTokenSource.Token);
                    recorder.ChunkAvailable += OnRecorderChunkAvailable;
                    _activeDeepgramSession = deepgramSession;
                    _transcriptionCancellationTokenSource = transcriptionCancellationTokenSource;
                }

                var device = _audioDeviceService.OpenPreferredCaptureDevice(settings.SelectedInputDeviceId);
                recorder.Start(device);
                _activeRecorder = recorder;
                DiagnosticsLogger.Info($"Recording started. Provider={settings.Provider}, Device='{recorder.DeviceName}', WaveFormat='{recorder.WaveFormatDescription}'.");
                PublishStatus(
                    DictationState.Recording,
                    UsesDeepgramStreaming(settings)
                        ? "Recording and streaming... release or press the hotkey again to stop."
                        : "Recording... release or press the hotkey again to stop.",
                    false);
                return true;
            }
            catch (Exception exception)
            {
                recorder.ChunkAvailable -= OnRecorderChunkAvailable;
                recorder.Dispose();

                if (deepgramSession is not null)
                {
                    await deepgramSession.DisposeAsync();
                }

                transcriptionCancellationTokenSource?.Cancel();
                transcriptionCancellationTokenSource?.Dispose();
                _activeDeepgramSession = null;
                _transcriptionCancellationTokenSource = null;
                PublishStatus(DictationState.Error, exception.Message, false);
                _notificationService.ShowNotification(
                    UsesDeepgramStreaming(settings) && deepgramSession is null
                        ? "Deepgram connection failed"
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
        DeepgramStreamingSession? deepgramSession;

        await _stateLock.WaitAsync();

        try
        {
            recorder = _activeRecorder;
            deepgramSession = _activeDeepgramSession;

            if (recorder is null)
            {
                return false;
            }

            _activeRecorder = null;
            _activeDeepgramSession = null;
            _isTranscribing = true;
        }
        finally
        {
            _stateLock.Release();
        }

        try
        {
            DiagnosticsLogger.Info($"Stopping dictation. HasDeepgramSession={deepgramSession is not null}.");
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

                if (deepgramSession is not null)
                {
                    await deepgramSession.DisposeAsync();
                }

                PublishStatus(DictationState.Idle, string.Empty, false);
                return true;
            }

            var settings = _settingsStore.CurrentSettings;
            if (!HasProviderApiKey(settings))
            {
                if (deepgramSession is not null)
                {
                    await deepgramSession.DisposeAsync();
                }

                PublishStatus(DictationState.Error, GetMissingApiKeyMessage(settings.Provider), false);
                _notificationService.ShowNotification("API key missing", GetMissingApiKeyMessage(settings.Provider), true);
                return true;
            }

            if (TryGetUploadLimitBytes(settings, out var uploadLimitBytes) && audioBytes.LongLength > uploadLimitBytes)
            {
                if (deepgramSession is not null)
                {
                    await deepgramSession.DisposeAsync();
                }

                var providerName = GetProviderDisplayName(settings.Provider);
                PublishStatus(DictationState.Error, $"The recording exceeded {providerName}'s upload limit.", false);
                _notificationService.ShowNotification("Recording too large", $"The recording exceeded {providerName}'s speech-to-text upload limit.", true);
                return true;
            }

            _transcriptionCancellationTokenSource ??= new CancellationTokenSource();
            PublishStatus(
                DictationState.Transcribing,
                UsesDeepgramStreaming(settings) ? "Finalizing Deepgram transcript..." : "Transcribing...",
                true);

            string transcription;

            try
            {
                transcription = deepgramSession is not null
                    ? await FinalizeDeepgramStreamingAsync(deepgramSession, _transcriptionCancellationTokenSource.Token)
                    : await TranscribeWithRetryAsync(audioBytes, settings, _transcriptionCancellationTokenSource.Token);

                DiagnosticsLogger.Info($"Transcription completed. Provider={settings.Provider}, TranscriptLength={transcription.Length}.");
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

            if (deepgramSession is null)
            {
                try
                {
                    await _clipboardPasteService.PasteTextAsync(transcription);
                }
                catch (Exception exception)
                {
                    PublishStatus(DictationState.Error, exception.Message, false);
                    _notificationService.ShowNotification("Paste failed", exception.Message, true);
                    return true;
                }
            }

            PublishStatus(DictationState.Idle, "Transcript pasted successfully.", false);
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
            await _clipboardPasteService.PasteTextAsync(lastTranscript, triggeringHotkey);
            PublishStatus(DictationState.Idle, "Latest transcript pasted.", false);
            return true;
        }
        catch (Exception exception)
        {
            PublishStatus(DictationState.Error, exception.Message, false);
            _notificationService.ShowNotification("Paste failed", exception.Message, true);
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

        if (_activeDeepgramSession is not null)
        {
            try
            {
                _activeDeepgramSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        _streamingPasteLock.Dispose();
        _stateLock.Dispose();
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

    private async Task<string> FinalizeDeepgramStreamingAsync(DeepgramStreamingSession deepgramSession, CancellationToken cancellationToken)
    {
        var failure = GetStreamingFailure();
        if (failure is not null)
        {
            throw WrapStreamingException(failure);
        }

        var transcription = await deepgramSession.CompleteAsync(cancellationToken);
        failure = GetStreamingFailure();
        if (failure is not null)
        {
            throw WrapStreamingException(failure);
        }

        if (string.IsNullOrWhiteSpace(transcription))
        {
            throw new TranscriptionException("Deepgram returned an empty transcription.", false);
        }

        return transcription.Trim();
    }

    private async Task AppendStreamingTranscriptChunkAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        DiagnosticsLogger.Info($"Pasting Deepgram finalized chunk. TextLength={text.Length}, Preview='{CreateTranscriptPreview(text)}'.");
        await _streamingPasteLock.WaitAsync(cancellationToken);

        try
        {
            await _clipboardPasteService.PasteTextAsync(text, cancellationToken: cancellationToken);
        }
        finally
        {
            _streamingPasteLock.Release();
        }
    }

    private void OnRecorderChunkAvailable(object? sender, AudioChunkAvailableEventArgs eventArgs)
    {
        var deepgramSession = _activeDeepgramSession;
        var cancellationTokenSource = _transcriptionCancellationTokenSource;

        if (deepgramSession is null || cancellationTokenSource is null || cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        _ = SendStreamingAudioChunkAsync(deepgramSession, eventArgs.AudioBytes, cancellationTokenSource.Token);
    }

    private async Task SendStreamingAudioChunkAsync(DeepgramStreamingSession deepgramSession, byte[] audioBytes, CancellationToken cancellationToken)
    {
        try
        {
            await deepgramSession.SendAudioAsync(audioBytes, cancellationToken);
        }
        catch (Exception exception)
        {
            HandleStreamingFailure(exception);
        }
    }

    private void HandleStreamingFailure(Exception exception)
    {
        lock (_streamingFailureLock)
        {
            _streamingFailure ??= exception;
        }

        DiagnosticsLogger.Error("Deepgram streaming failed.", exception);
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

    private static bool UsesDeepgramStreaming(AppSettings settings)
    {
        return settings.Provider == TranscriptionProvider.Deepgram && settings.DeepgramStreamingEnabled;
    }

    private static string GetApiKey(AppSettings settings)
    {
        return settings.Provider switch
        {
            TranscriptionProvider.Fireworks => settings.FireworksApiKey ?? string.Empty,
            TranscriptionProvider.Deepgram => settings.DeepgramApiKey ?? string.Empty,
            _ => settings.GroqApiKey ?? string.Empty,
        };
    }

    private static string GetModel(AppSettings settings)
    {
        return settings.Provider switch
        {
            TranscriptionProvider.Fireworks => settings.FireworksModel,
            TranscriptionProvider.Deepgram => settings.DeepgramModel,
            _ => settings.GroqModel,
        };
    }

    private static string GetLanguage(AppSettings settings)
    {
        return settings.Provider switch
        {
            TranscriptionProvider.Fireworks => settings.FireworksLanguage,
            TranscriptionProvider.Deepgram => settings.DeepgramLanguage,
            _ => settings.GroqLanguage,
        };
    }

    private static string GetMissingApiKeyMessage(TranscriptionProvider provider)
    {
        return provider switch
        {
            TranscriptionProvider.Fireworks => "Open Settings and save a Fireworks API key before dictating.",
            TranscriptionProvider.Deepgram => "Open Settings and save a Deepgram API key before dictating.",
            _ => "Open Settings and save a Groq API key before dictating.",
        };
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
            _ => "Groq",
        };
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
