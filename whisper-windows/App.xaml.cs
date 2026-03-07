using Microsoft.UI.Xaml;
using whisper_windows.Interop;
using whisper_windows.Models;
using whisper_windows.Services;

namespace whisper_windows;

public partial class App : Application
{
    private readonly AppSettingsStore _settingsStore = new();
    private readonly AudioDeviceService _audioDeviceService = new();
    private readonly TranscriptHistoryStore _transcriptHistoryStore = new();

    private MainWindow? _window;
    private TrayIconService? _trayIconService;
    private KeyboardHookService? _keyboardHookService;
    private DictationController? _dictationController;
    private AppSettings _currentSettings = new();
    private bool _isQuitting;
    private bool _toggleRecordingActive;

    public App()
    {
        DiagnosticsLogger.Initialize();
        DiagnosticsLogger.HookGlobalExceptionLogging();
        DiagnosticsLogger.Info("App constructor start.");
        InitializeComponent();
        DiagnosticsLogger.Info("App constructor completed.");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DiagnosticsLogger.Info("OnLaunched entered.");

        try
        {
            _currentSettings = _settingsStore.LoadAsync().GetAwaiter().GetResult();
            DiagnosticsLogger.Info($"Initial settings loaded. Hotkey='{_currentSettings.Hotkey.ToDisplayString()}', PasteLastTranscriptHotkey='{_currentSettings.PasteLastTranscriptHotkey.ToDisplayString()}', TranscriptHistoryLimit={_currentSettings.TranscriptHistoryLimit}, PushToTalk={_currentSettings.PushToTalk}, GroqModel='{_currentSettings.GroqModel}'.");
            ApplyTranscriptHistoryRetention(_currentSettings.TranscriptHistoryLimit);

            DiagnosticsLogger.Info("Creating MainWindow.");
            _window = new MainWindow(_settingsStore, _audioDeviceService);
            _window.SettingsSaved += OnSettingsSaved;
            DiagnosticsLogger.Info("MainWindow created.");

            DiagnosticsLogger.Info("Creating TrayIconService.");
            _trayIconService = new TrayIconService(
                () => _window.ShowSettingsWindowAsync(),
                QuitApplication);
            DiagnosticsLogger.Info("TrayIconService created.");

            DiagnosticsLogger.Info("Attaching tray icon.");
            _window.AttachTrayIcon(_trayIconService);
            DiagnosticsLogger.Info("Tray icon attached.");

            DiagnosticsLogger.Info("Creating DictationController.");
            _dictationController = new DictationController(
                _settingsStore,
                _audioDeviceService,
                new GroqTranscriptionClient(),
                new ClipboardPasteService(_window.DispatcherQueue),
                _transcriptHistoryStore,
                _trayIconService);
            DiagnosticsLogger.Info("DictationController created.");

            DiagnosticsLogger.Info("Creating KeyboardHookService.");
            _keyboardHookService = new KeyboardHookService(_window.DispatcherQueue);
            _keyboardHookService.UpdateHotkeys(_currentSettings.Hotkey, _currentSettings.PasteLastTranscriptHotkey);
            _keyboardHookService.RecordingHotkeyStarted += OnRecordingHotkeyStarted;
            _keyboardHookService.RecordingHotkeyEnded += OnRecordingHotkeyEnded;
            _keyboardHookService.PasteLastTranscriptHotkeyPressed += OnPasteLastTranscriptHotkeyPressed;
            _window.AttachKeyboardHookService(_keyboardHookService);
            DiagnosticsLogger.Info("KeyboardHookService created, configured, and attached.");

            DiagnosticsLogger.Info("Activating window.");
            _window.Activate();
            DiagnosticsLogger.Info("Window activated.");

            DiagnosticsLogger.Info("Showing settings window.");
            _ = _window.ShowSettingsWindowAsync();
            DiagnosticsLogger.Info("ShowSettingsWindowAsync dispatched.");

            try
            {
                DiagnosticsLogger.Info("Starting keyboard hook.");
                _keyboardHookService.Start();
                DiagnosticsLogger.Info("Keyboard hook started.");
            }
            catch (Exception exception)
            {
                DiagnosticsLogger.Error("Keyboard hook startup failed.", exception);
                _trayIconService.ShowNotification("Startup failed", exception.Message, true);
                _ = _window.ShowSettingsWindowAsync();
            }
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error("Fatal exception during OnLaunched.", exception);
            NativeMethods.MessageBox(
                IntPtr.Zero,
                $"{exception}{Environment.NewLine}{Environment.NewLine}Log file: {DiagnosticsLogger.LogFilePath}",
                "Whisper Windows startup failed",
                NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);

            throw;
        }
    }

    private async void OnRecordingHotkeyStarted(object? sender, EventArgs e)
    {
        DiagnosticsLogger.Info("Recording hotkey started.");

        if (_dictationController is null)
        {
            return;
        }

        if (_currentSettings.PushToTalk)
        {
            await _dictationController.StartDictationAsync();
            return;
        }

        if (_toggleRecordingActive)
        {
            await _dictationController.StopDictationAsync();
            _toggleRecordingActive = false;
            DiagnosticsLogger.Info("Toggle recording stopped by hotkey press.");
        }
        else
        {
            if (await _dictationController.StartDictationAsync())
            {
                _toggleRecordingActive = true;
                DiagnosticsLogger.Info("Toggle recording started by hotkey press.");
            }
        }
    }

    private async void OnRecordingHotkeyEnded(object? sender, EventArgs e)
    {
        DiagnosticsLogger.Info("Recording hotkey ended.");

        if (!_currentSettings.PushToTalk || _dictationController is null)
        {
            return;
        }

        await _dictationController.StopDictationAsync();
    }

    private async void OnPasteLastTranscriptHotkeyPressed(object? sender, EventArgs e)
    {
        DiagnosticsLogger.Info("Paste last transcript hotkey pressed.");

        if (_dictationController is null)
        {
            return;
        }

        await _dictationController.PasteLastTranscriptAsync();
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        _currentSettings = settings;
        _keyboardHookService?.UpdateHotkeys(settings.Hotkey, settings.PasteLastTranscriptHotkey);
        ApplyTranscriptHistoryRetention(settings.TranscriptHistoryLimit);
        DiagnosticsLogger.Info($"Settings applied at runtime. Hotkey='{settings.Hotkey.ToDisplayString()}', PasteLastTranscriptHotkey='{settings.PasteLastTranscriptHotkey.ToDisplayString()}', TranscriptHistoryLimit={settings.TranscriptHistoryLimit}, PushToTalk={settings.PushToTalk}, GroqModel='{settings.GroqModel}'.");
    }

    private void ApplyTranscriptHistoryRetention(int transcriptHistoryLimit)
    {
        try
        {
            _transcriptHistoryStore.EnforceRetentionAsync(transcriptHistoryLimit).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error("Transcript history retention could not be applied.", exception);
        }
    }

    private void QuitApplication()
    {
        DiagnosticsLogger.Info("QuitApplication entered.");

        if (_isQuitting)
        {
            DiagnosticsLogger.Info("QuitApplication ignored because shutdown is already in progress.");
            return;
        }

        _isQuitting = true;

        if (_keyboardHookService is not null)
        {
            _keyboardHookService.RecordingHotkeyStarted -= OnRecordingHotkeyStarted;
            _keyboardHookService.RecordingHotkeyEnded -= OnRecordingHotkeyEnded;
            _keyboardHookService.PasteLastTranscriptHotkeyPressed -= OnPasteLastTranscriptHotkeyPressed;
            _keyboardHookService.Dispose();
            _keyboardHookService = null;
        }

        if (_window is not null)
        {
            _window.SettingsSaved -= OnSettingsSaved;
        }

        _dictationController?.Dispose();
        _dictationController = null;

        _trayIconService?.Dispose();
        _trayIconService = null;

        if (_window is not null)
        {
            _window.EnableClose();
            _window.Close();
            _window = null;
        }

        DiagnosticsLogger.Info("QuitApplication completed.");
    }
}
