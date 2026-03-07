using whisper_windows.Interfaces;
using whisper_windows.Models;

namespace whisper_windows.Services;

public sealed class DictationController : IDictationController
{
    private const int GroqUploadLimitBytes = 25 * 1024 * 1024;
    private const int MaxRetryCount = 2;

    private readonly IAppSettingsStore _settingsStore;
    private readonly IAudioDeviceService _audioDeviceService;
    private readonly ITranscriptionClientFactory _transcriptionClientFactory;
    private readonly IClipboardPasteService _clipboardPasteService;
    private readonly ITranscriptHistoryStore _transcriptHistoryStore;
    private readonly INotificationService _notificationService;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private AudioRecorder? _activeRecorder;
    private CancellationTokenSource? _transcriptionCancellationTokenSource;
    private bool _isTranscribing;
    private string? _lastTranscript;

    public DictationController(
        IAppSettingsStore settingsStore,
        IAudioDeviceService audioDeviceService,
        ITranscriptionClientFactory transcriptionClientFactory,
        IClipboardPasteService clipboardPasteService,
        ITranscriptHistoryStore transcriptHistoryStore,
        INotificationService notificationService)
    {
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;
        _transcriptionClientFactory = transcriptionClientFactory;
        _clipboardPasteService = clipboardPasteService;
        _transcriptHistoryStore = transcriptHistoryStore;
        _notificationService = notificationService;
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
            if (!HasProviderApiKey(settings))
            {
                PublishStatus(DictationState.Error, GetMissingApiKeyMessage(settings.Provider), false);
                _notificationService.ShowNotification("API key missing", GetMissingApiKeyMessage(settings.Provider), true);
                return false;
            }

            var recorder = new AudioRecorder();

            try
            {
                var device = _audioDeviceService.OpenPreferredCaptureDevice(settings.SelectedInputDeviceId);
                recorder.Start(device);
                _activeRecorder = recorder;
                PublishStatus(DictationState.Recording, "Recording... release or press the hotkey again to stop.", false);
                return true;
            }
            catch (Exception exception)
            {
                recorder.Dispose();
                PublishStatus(DictationState.Error, exception.Message, false);
                _notificationService.ShowNotification("Microphone unavailable", exception.Message, true);
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

        await _stateLock.WaitAsync();

        try
        {
            recorder = _activeRecorder;

            if (recorder is null)
            {
                return false;
            }

            _activeRecorder = null;
            _isTranscribing = true;
            _transcriptionCancellationTokenSource = new CancellationTokenSource();
            PublishStatus(DictationState.Transcribing, "Transcribing...", true);
        }
        finally
        {
            _stateLock.Release();
        }

        try
        {
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

            if (audioBytes.Length > GroqUploadLimitBytes)
            {
                PublishStatus(DictationState.Error, "The recording exceeded Groq's upload limit.", false);
                _notificationService.ShowNotification("Recording too large", "The recording exceeded Groq's speech-to-text upload limit.", true);
                return true;
            }

            var settings = _settingsStore.CurrentSettings;
            if (!HasProviderApiKey(settings))
            {
                PublishStatus(DictationState.Error, GetMissingApiKeyMessage(settings.Provider), false);
                _notificationService.ShowNotification("API key missing", GetMissingApiKeyMessage(settings.Provider), true);
                return true;
            }

            string transcription;

            try
            {
                transcription = await TranscribeWithRetryAsync(audioBytes, settings, _transcriptionCancellationTokenSource.Token);
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
        _activeRecorder?.Dispose();
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

    private static string GetApiKey(AppSettings settings)
    {
        return settings.Provider == TranscriptionProvider.Fireworks
            ? settings.FireworksApiKey ?? string.Empty
            : settings.GroqApiKey ?? string.Empty;
    }

    private static string GetModel(AppSettings settings)
    {
        return settings.Provider == TranscriptionProvider.Fireworks
            ? settings.FireworksModel
            : settings.GroqModel;
    }

    private static string GetLanguage(AppSettings settings)
    {
        return settings.Provider == TranscriptionProvider.Fireworks
            ? settings.FireworksLanguage
            : settings.GroqLanguage;
    }

    private static string GetMissingApiKeyMessage(TranscriptionProvider provider)
    {
        return provider == TranscriptionProvider.Fireworks
            ? "Open Settings and save a Fireworks API key before dictating."
            : "Open Settings and save a Groq API key before dictating.";
    }
}
