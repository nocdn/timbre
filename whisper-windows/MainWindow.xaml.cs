using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using whisper_windows.Interop;
using whisper_windows.Models;
using whisper_windows.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace whisper_windows;

public sealed partial class MainWindow : Window
{
    private static readonly string[] GroqModels =
    [
        "whisper-large-v3-turbo",
        "whisper-large-v3",
    ];

    private readonly AppSettingsStore _settingsStore;
    private readonly AudioDeviceService _audioDeviceService;
    private readonly IntPtr _windowHandle;
    private readonly NativeMethods.WndProc _windowProc;

    private IntPtr _previousWindowProc;
    private TrayIconService? _trayIconService;
    private KeyboardHookService? _keyboardHookService;
    private bool _allowClose;
    private HotkeyBinding _pendingHotkey = HotkeyBinding.Default;
    private HotkeyBinding _pendingPasteLastTranscriptHotkey = HotkeyBinding.PasteLastTranscriptDefault;

    public MainWindow(AppSettingsStore settingsStore, AudioDeviceService audioDeviceService)
    {
        DiagnosticsLogger.Info("MainWindow constructor start.");
        _settingsStore = settingsStore;
        _audioDeviceService = audioDeviceService;

        InitializeComponent();
        DiagnosticsLogger.Info("MainWindow InitializeComponent completed.");

        GroqModelComboBox.ItemsSource = GroqModels;

        _windowHandle = WindowNative.GetWindowHandle(this);
        DiagnosticsLogger.Info($"MainWindow handle acquired: 0x{_windowHandle.ToInt64():X}.");
        _windowProc = HandleWindowMessage;
        _previousWindowProc = NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_WNDPROC, _windowProc);
        DiagnosticsLogger.Info($"MainWindow subclass installed. Previous proc: 0x{_previousWindowProc.ToInt64():X}.");

        ConfigureWindowAppearance();
        Closed += OnClosed;
        DiagnosticsLogger.Info("MainWindow constructor completed.");
    }

    public event Action<AppSettings>? SettingsSaved;

    public void AttachTrayIcon(TrayIconService trayIconService)
    {
        DiagnosticsLogger.Info("AttachTrayIcon entered.");
        _trayIconService = trayIconService;
        _trayIconService.Initialize(_windowHandle);
        DiagnosticsLogger.Info("AttachTrayIcon completed.");
    }

    public void AttachKeyboardHookService(KeyboardHookService keyboardHookService)
    {
        _keyboardHookService = keyboardHookService;
        DiagnosticsLogger.Info("AttachKeyboardHookService completed.");
    }

    public async Task ShowSettingsWindowAsync()
    {
        DiagnosticsLogger.Info("ShowSettingsWindowAsync entered.");
        await LoadSettingsIntoFormAsync();

        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_RESTORE);
        Activate();
        NativeMethods.SetForegroundWindow(_windowHandle);
        DiagnosticsLogger.Info("ShowSettingsWindowAsync completed.");
    }

    public void HideToTray()
    {
        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_HIDE);
    }

    public void EnableClose()
    {
        _allowClose = true;
    }

    private void ConfigureWindowAppearance()
    {
        DiagnosticsLogger.Info("ConfigureWindowAppearance entered.");
        Title = "Whisper Windows Settings";

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(520, 560));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }

        DiagnosticsLogger.Info("ConfigureWindowAppearance completed.");
    }

    private async Task LoadSettingsIntoFormAsync()
    {
        DiagnosticsLogger.Info("LoadSettingsIntoFormAsync entered.");
        try
        {
            var settings = await _settingsStore.LoadAsync();
            var devices = _audioDeviceService.GetInputDevices();

            InputDeviceComboBox.ItemsSource = devices;

            var selectedDeviceId = string.IsNullOrWhiteSpace(settings.SelectedInputDeviceId)
                ? devices.FirstOrDefault(device => device.IsDefault)?.Id
                : settings.SelectedInputDeviceId;

            InputDeviceComboBox.SelectedItem = devices.FirstOrDefault(device => device.Id == selectedDeviceId);

            if (InputDeviceComboBox.SelectedItem is null && devices.Count > 0)
            {
                InputDeviceComboBox.SelectedIndex = 0;
            }

            GroqApiKeyBox.Password = settings.GroqApiKey ?? string.Empty;
            PushToTalkToggle.IsOn = settings.PushToTalk;

            _pendingHotkey = settings.Hotkey;
            HotkeyCaptureButton.Content = _pendingHotkey.ToDisplayString();

            _pendingPasteLastTranscriptHotkey = settings.PasteLastTranscriptHotkey;
            PasteLastTranscriptHotkeyCaptureButton.Content = _pendingPasteLastTranscriptHotkey.ToDisplayString();
            TranscriptHistoryLimitNumberBox.Value = settings.TranscriptHistoryLimit;

            GroqModelComboBox.SelectedItem = GroqModels.FirstOrDefault(model => model == settings.GroqModel) ?? GroqModels[0];

            StatusTextBlock.Text = devices.Count == 0 ? "No input devices are currently available." : string.Empty;
            DiagnosticsLogger.Info($"LoadSettingsIntoFormAsync completed. Device count: {devices.Count}.");
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error("LoadSettingsIntoFormAsync failed.", exception);
            await ShowDialogAsync("Settings could not be loaded", exception.Message);
        }
    }

    private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        StartHotkeyCapture(
            HotkeyCaptureButton,
            hotkey =>
            {
                _pendingHotkey = hotkey;
                StatusTextBlock.Text = $"Recording hotkey set to {hotkey.ToDisplayString()}. Click Save to apply.";
            });
    }

    private void PasteLastTranscriptHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        StartHotkeyCapture(
            PasteLastTranscriptHotkeyCaptureButton,
            hotkey =>
            {
                _pendingPasteLastTranscriptHotkey = hotkey;
                StatusTextBlock.Text = $"Paste last transcript hotkey set to {hotkey.ToDisplayString()}. Click Save to apply.";
            });
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        StatusTextBlock.Text = string.Empty;

        try
        {
            if (_pendingHotkey.Equals(_pendingPasteLastTranscriptHotkey))
            {
                StatusTextBlock.Text = "Recording and paste last transcript hotkeys must be different.";
                return;
            }

            var selectedDevice = InputDeviceComboBox.SelectedItem as AudioInputDevice;
            var selectedModel = GroqModelComboBox.SelectedItem as string ?? GroqModels[0];
            var transcriptHistoryLimit = GetTranscriptHistoryLimit();

            var settings = new AppSettings
            {
                SelectedInputDeviceId = selectedDevice?.Id,
                GroqApiKey = GroqApiKeyBox.Password,
                Hotkey = _pendingHotkey,
                PasteLastTranscriptHotkey = _pendingPasteLastTranscriptHotkey,
                TranscriptHistoryLimit = transcriptHistoryLimit,
                PushToTalk = PushToTalkToggle.IsOn,
                GroqModel = selectedModel,
            };

            await _settingsStore.SaveAsync(settings);
            SettingsSaved?.Invoke(settings);

            StatusTextBlock.Text = "Settings saved.";
            DiagnosticsLogger.Info("Settings saved successfully.");
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error("SaveButton_Click failed.", exception);
            await ShowDialogAsync("Settings could not be saved", exception.Message);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void StartHotkeyCapture(Button button, Action<HotkeyBinding> onHotkeyCaptured)
    {
        if (_keyboardHookService is null)
        {
            StatusTextBlock.Text = "The keyboard hook is not ready yet.";
            return;
        }

        SetHotkeyCaptureButtonsEnabled(false);
        button.Content = "Press hotkey...";
        StatusTextBlock.Text = "Press the new hotkey now.";
        _keyboardHookService.BeginHotkeyCapture(hotkey => HotkeyCaptured(button, hotkey, onHotkeyCaptured));
    }

    private void HotkeyCaptured(Button button, HotkeyBinding hotkey, Action<HotkeyBinding> onHotkeyCaptured)
    {
        onHotkeyCaptured(hotkey);
        button.Content = hotkey.ToDisplayString();
        SetHotkeyCaptureButtonsEnabled(true);
    }

    private void SetHotkeyCaptureButtonsEnabled(bool isEnabled)
    {
        HotkeyCaptureButton.IsEnabled = isEnabled;
        PasteLastTranscriptHotkeyCaptureButton.IsEnabled = isEnabled;
    }

    private int GetTranscriptHistoryLimit()
    {
        var value = TranscriptHistoryLimitNumberBox.Value;

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 20;
        }

        return Math.Clamp((int)Math.Round(value), 0, 500);
    }

    private async Task ShowDialogAsync(string title, string message)
    {
        DiagnosticsLogger.Info($"ShowDialogAsync: {title}");
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = RootGrid.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    private IntPtr HandleWindowMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (_trayIconService is not null && _trayIconService.HandleWindowMessage(message, wParam, lParam))
        {
            return IntPtr.Zero;
        }

        if (!_allowClose)
        {
            if (message == NativeMethods.WM_CLOSE)
            {
                HideToTray();
                return IntPtr.Zero;
            }

            if (message == NativeMethods.WM_SYSCOMMAND &&
                ((long)wParam & 0xFFF0) == NativeMethods.SC_MINIMIZE)
            {
                HideToTray();
                return IntPtr.Zero;
            }

            if (message == NativeMethods.WM_SIZE && wParam.ToInt32() == NativeMethods.SIZE_MINIMIZED)
            {
                HideToTray();
                return IntPtr.Zero;
            }
        }

        return NativeMethods.CallWindowProc(_previousWindowProc, hWnd, message, wParam, lParam);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        DiagnosticsLogger.Info("MainWindow OnClosed entered.");
        if (_previousWindowProc != IntPtr.Zero)
        {
            NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_WNDPROC, _previousWindowProc);
            _previousWindowProc = IntPtr.Zero;
        }
        DiagnosticsLogger.Info("MainWindow OnClosed completed.");
    }
}
