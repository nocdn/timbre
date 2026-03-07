using System.Threading;
using whisper_windows.Models;

namespace whisper_windows.Services;

public sealed class DictationController : IDisposable
{
    private const int GroqUploadLimitBytes = 25 * 1024 * 1024;

    private readonly AppSettingsStore _settingsStore;
    private readonly AudioDeviceService _audioDeviceService;
    private readonly GroqTranscriptionClient _transcriptionClient;
    private readonly ClipboardPasteService _clipboardPasteService;
    private readonly TranscriptHistoryStore _transcriptHistoryStore;
    private readonly TrayIconService _trayIconService;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private AudioRecorder? _activeRecorder;
    private bool _isTranscribing;
    private string? _lastTranscript;

    public DictationController(
        AppSettingsStore settingsStore,
        AudioDeviceService audioDeviceService,
        GroqTranscriptionClient transcriptionClient,
        ClipboardPasteService clipboardPasteService,
        TranscriptHistoryStore transcriptHistoryStore,
        TrayIconService trayIconService)
    {
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;
        _transcriptionClient = transcriptionClient;
        _clipboardPasteService = clipboardPasteService;
        _transcriptHistoryStore = transcriptHistoryStore;
        _trayIconService = trayIconService;
    }

    public async Task<bool> StartDictationAsync()
    {
        DiagnosticsLogger.Info("StartDictationAsync entered.");
        await _stateLock.WaitAsync();

        try
        {
            if (_activeRecorder is not null)
            {
                DiagnosticsLogger.Info("StartDictationAsync ignored because a recording is already active.");
                return false;
            }

            if (_isTranscribing)
            {
                DiagnosticsLogger.Info("StartDictationAsync ignored because transcription is already in progress.");
                _trayIconService.ShowNotification("Transcription in progress", "Wait for the previous dictation to finish before recording again.", true);
                return false;
            }

            AppSettings settings;

            try
            {
                settings = await _settingsStore.LoadAsync();
                DiagnosticsLogger.Info("Settings loaded successfully for dictation start.");
            }
            catch (Exception exception)
            {
                DiagnosticsLogger.Error("Settings load failed during StartDictationAsync.", exception);
                _trayIconService.ShowNotification("Settings load failed", exception.Message, true);
                return false;
            }

            if (string.IsNullOrWhiteSpace(settings.GroqApiKey))
            {
                _trayIconService.ShowNotification("Groq API key missing", "Open Settings and save a Groq API key before dictating.", true);
                return false;
            }

            var recorder = new AudioRecorder();

            try
            {
                var device = _audioDeviceService.OpenPreferredCaptureDevice(settings.SelectedInputDeviceId);
                recorder.Start(device);
                _activeRecorder = recorder;
                DiagnosticsLogger.Info(
                    $"Recording started. Device='{recorder.DeviceName}', Format='{recorder.WaveFormatDescription}'.");
                return true;
            }
            catch (Exception exception)
            {
                DiagnosticsLogger.Error("Recording failed to start.", exception);
                recorder.Dispose();
                _trayIconService.ShowNotification("Microphone unavailable", exception.Message, true);
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
        DiagnosticsLogger.Info("StopDictationAsync entered.");
        AudioRecorder? recorder;

        await _stateLock.WaitAsync();

        try
        {
            recorder = _activeRecorder;

            if (recorder is null)
            {
                DiagnosticsLogger.Info("StopDictationAsync ignored because no recording is active.");
                return false;
            }

            _activeRecorder = null;
            _isTranscribing = true;
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
                DiagnosticsLogger.Info(
                    $"Recording stopped. CapturedBytes={recorder.LastCompletedBytesCaptured}, PayloadBytes={audioBytes.Length}, Device='{recorder.LastCompletedDeviceName}', Format='{recorder.LastCompletedWaveFormatDescription}'.");
            }
            catch (Exception exception)
            {
                DiagnosticsLogger.Error("Recording stop failed.", exception);
                _trayIconService.ShowNotification("Recording failed", exception.Message, true);
                return true;
            }

            if (audioBytes.Length == 0)
            {
                _trayIconService.ShowNotification("Recording failed", "No audio was captured.", true);
                return true;
            }

            if (audioBytes.Length > GroqUploadLimitBytes)
            {
                _trayIconService.ShowNotification("Recording too large", "The recording exceeded Groq's 25 MB speech-to-text limit.", true);
                return true;
            }

            AppSettings settings;

            try
            {
                settings = await _settingsStore.LoadAsync();
                DiagnosticsLogger.Info("Settings loaded successfully for transcription.");
            }
            catch (Exception exception)
            {
                DiagnosticsLogger.Error("Settings load failed during StopDictationAsync.", exception);
                _trayIconService.ShowNotification("Settings load failed", exception.Message, true);
                return true;
            }

            if (string.IsNullOrWhiteSpace(settings.GroqApiKey))
            {
                _trayIconService.ShowNotification("Groq API key missing", "Open Settings and save a Groq API key before dictating.", true);
                return true;
            }

            string transcription;

            try
            {
                transcription = await _transcriptionClient.TranscribeAsync(audioBytes, settings.GroqApiKey, settings.GroqModel);
                DiagnosticsLogger.Info($"Transcription completed. TextLength={transcription.Length}.");
            }
            catch (Exception exception)
            {
                DiagnosticsLogger.Error("Transcription request failed.", exception);
                _trayIconService.ShowNotification("Transcription failed", exception.Message, true);
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
                DiagnosticsLogger.Error("Transcript history could not be updated.", exception);
                _trayIconService.ShowNotification("Transcript history failed", exception.Message, true);
            }

            try
            {
                await _clipboardPasteService.PasteTextAsync(transcription);
                DiagnosticsLogger.Info("PasteTextAsync completed successfully.");
            }
            catch (Exception exception)
            {
                DiagnosticsLogger.Error("PasteTextAsync failed after transcription.", exception);
                _trayIconService.ShowNotification("Paste failed", exception.Message, true);
            }

            return true;
        }
        finally
        {
            recorder.Dispose();

            await _stateLock.WaitAsync();

            try
            {
                _isTranscribing = false;
            }
            finally
            {
                _stateLock.Release();
            }
        }
    }

    public async Task<bool> PasteLastTranscriptAsync(HotkeyBinding? triggeringHotkey = null)
    {
        DiagnosticsLogger.Info("PasteLastTranscriptAsync entered.");
        string? lastTranscript = null;

        try
        {
            lastTranscript = await _transcriptHistoryStore.GetLatestTranscriptAsync();
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error("Transcript history could not be read for paste.", exception);
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
            DiagnosticsLogger.Info("PasteLastTranscriptAsync ignored because no transcript is available.");
            _trayIconService.ShowNotification("No transcript available", "Record and transcribe audio before using paste last transcript.", true);
            return false;
        }

        try
        {
            await _clipboardPasteService.PasteTextAsync(lastTranscript, triggeringHotkey);
            DiagnosticsLogger.Info("PasteLastTranscriptAsync completed successfully.");
            return true;
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error("PasteLastTranscriptAsync failed.", exception);
            _trayIconService.ShowNotification("Paste failed", exception.Message, true);
            return false;
        }
    }

    public void Dispose()
    {
        _activeRecorder?.Dispose();
        _stateLock.Dispose();
    }
}
