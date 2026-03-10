using timbre.Interfaces;
using timbre.Models;
using timbre.ViewModels;

namespace timbre.Services;

public sealed class AppCoordinator
{
    private readonly MainWindow _mainWindow;
    private readonly MainViewModel _mainViewModel;
    private readonly IUiDispatcherQueueAccessor _uiDispatcherQueueAccessor;
    private readonly IAppSettingsStore _settingsStore;
    private readonly ITranscriptHistoryStore _transcriptHistoryStore;
    private readonly INotificationService _notificationService;
    private readonly IDictationController _dictationController;
    private readonly IAudioFeedbackService _audioFeedbackService;

    private KeyboardHookService? _keyboardHookService;
    private TrayIconService? _trayIconService;
    private bool _isQuitting;
    private bool _toggleRecordingActive;
    private AppSettings _currentSettings = new();

    public AppCoordinator(
        MainWindow mainWindow,
        MainViewModel mainViewModel,
        IUiDispatcherQueueAccessor uiDispatcherQueueAccessor,
        IAppSettingsStore settingsStore,
        ITranscriptHistoryStore transcriptHistoryStore,
        INotificationService notificationService,
        IDictationController dictationController,
        IAudioFeedbackService audioFeedbackService)
    {
        _mainWindow = mainWindow;
        _mainViewModel = mainViewModel;
        _uiDispatcherQueueAccessor = uiDispatcherQueueAccessor;
        _settingsStore = settingsStore;
        _transcriptHistoryStore = transcriptHistoryStore;
        _notificationService = notificationService;
        _dictationController = dictationController;
        _audioFeedbackService = audioFeedbackService;
        Current = this;
    }

    public static AppCoordinator? Current { get; private set; }

    public async Task InitializeAsync(bool startHidden = false)
    {
        _uiDispatcherQueueAccessor.DispatcherQueue = _mainWindow.DispatcherQueue;
        _currentSettings = await _settingsStore.LoadAsync();
        await _transcriptHistoryStore.EnforceRetentionAsync(_currentSettings.TranscriptHistoryLimit);

        _mainWindow.SettingsSaved += OnSettingsSaved;

        _trayIconService = new TrayIconService(() => _mainWindow.ShowSettingsWindowAsync(), QuitApplication);
        _mainWindow.AttachTrayIcon(_trayIconService);
        _notificationService.AttachTrayIconService(_trayIconService);

        _keyboardHookService = new KeyboardHookService(_mainWindow.DispatcherQueue);
        _keyboardHookService.UpdateHotkeys(_currentSettings.Hotkey, _currentSettings.PasteLastTranscriptHotkey, _currentSettings.OpenHistoryHotkey);
        _keyboardHookService.RecordingHotkeyStarted += OnRecordingHotkeyStarted;
        _keyboardHookService.RecordingHotkeyEnded += OnRecordingHotkeyEnded;
        _keyboardHookService.PasteLastTranscriptHotkeyPressed += OnPasteLastTranscriptHotkeyPressed;
        _keyboardHookService.OpenHistoryHotkeyPressed += OnOpenHistoryHotkeyPressed;
        _mainWindow.AttachKeyboardHookService(_keyboardHookService);

        await _mainViewModel.InitializeAsync();

        try
        {
            _keyboardHookService.Start();
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error("Keyboard hook startup failed.", exception);
            _notificationService.ShowNotification("Startup failed", exception.Message, true);
            await _mainWindow.ShowSettingsWindowAsync();
            return;
        }

        if (startHidden)
        {
            _mainWindow.HideToTray();
            return;
        }

        await _mainWindow.ShowSettingsWindowAsync();
    }

    public void ShowMainWindowFromActivation()
    {
        _ = _mainWindow.ShowSettingsWindowAsync();
    }

    private async void OnRecordingHotkeyStarted(object? sender, EventArgs e)
    {
        if (_currentSettings.PushToTalk)
        {
            if (await _dictationController.StartDictationAsync())
            {
                PlayRecordingStartedFeedback();
            }

            return;
        }

        if (_toggleRecordingActive)
        {
            await _dictationController.StopDictationAsync();
            _toggleRecordingActive = false;
        }
        else if (await _dictationController.StartDictationAsync())
        {
            PlayRecordingStartedFeedback();
            _toggleRecordingActive = true;
        }
    }

    private async void OnRecordingHotkeyEnded(object? sender, EventArgs e)
    {
        if (!_currentSettings.PushToTalk)
        {
            return;
        }

        await _dictationController.StopDictationAsync();
    }

    private async void OnPasteLastTranscriptHotkeyPressed(object? sender, EventArgs e)
    {
        await _dictationController.PasteLastTranscriptAsync(_currentSettings.PasteLastTranscriptHotkey);
    }

    private async void OnOpenHistoryHotkeyPressed(object? sender, EventArgs e)
    {
        await _mainWindow.ShowHistoryWindowAsync();
    }

    private async void OnSettingsSaved(AppSettings settings)
    {
        _currentSettings = settings;
        _keyboardHookService?.UpdateHotkeys(settings.Hotkey, settings.PasteLastTranscriptHotkey, settings.OpenHistoryHotkey);

        try
        {
            await _transcriptHistoryStore.EnforceRetentionAsync(settings.TranscriptHistoryLimit);
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error("Transcript history retention could not be applied.", exception);
        }
    }

    private void QuitApplication()
    {
        if (_isQuitting)
        {
            return;
        }

        _isQuitting = true;

        if (_keyboardHookService is not null)
        {
            _keyboardHookService.RecordingHotkeyStarted -= OnRecordingHotkeyStarted;
            _keyboardHookService.RecordingHotkeyEnded -= OnRecordingHotkeyEnded;
            _keyboardHookService.PasteLastTranscriptHotkeyPressed -= OnPasteLastTranscriptHotkeyPressed;
            _keyboardHookService.OpenHistoryHotkeyPressed -= OnOpenHistoryHotkeyPressed;
            _keyboardHookService.Dispose();
            _keyboardHookService = null;
        }

        _mainWindow.SettingsSaved -= OnSettingsSaved;
        _dictationController.Dispose();
        _audioFeedbackService.Dispose();
        _trayIconService?.Dispose();
        _mainWindow.EnableClose();
        _mainWindow.Close();
    }

    private void PlayRecordingStartedFeedback()
    {
        if (!_currentSettings.SoundFeedbackEnabled)
        {
            return;
        }

        try
        {
            _audioFeedbackService.PlayRecordingStarted();
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error("Recording feedback sound failed.", exception);
        }
    }
}
