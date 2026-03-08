using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using timbre.Interop;
using timbre.Models;
using timbre.Services;
using timbre.ViewModels;
using Windows.Graphics;
using WinRT.Interop;

namespace timbre;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IntPtr _windowHandle;
    private readonly NativeMethods.WndProc _windowProc;

    private IntPtr _previousWindowProc;
    private TrayIconService? _trayIconService;
    private KeyboardHookService? _keyboardHookService;
    private TranscriptionHistoryWindow? _historyWindow;
    private CancellationTokenSource? _autoSaveCancellationTokenSource;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _allowClose;
    private bool _isApplyingViewModel;
    private int _statusMessageToken;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        _windowHandle = WindowNative.GetWindowHandle(this);
        _windowProc = HandleWindowMessage;
        _previousWindowProc = NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_WNDPROC, _windowProc);

        ConfigureWindowAppearance();
        RootGrid.ActualThemeChanged += OnRootGridActualThemeChanged;
        Closed += OnClosed;
        _viewModel.SettingsSaved += OnSettingsSaved;
    }

    public event Action<AppSettings>? SettingsSaved;

    public void AttachTrayIcon(TrayIconService trayIconService)
    {
        _trayIconService = trayIconService;
        _trayIconService.Initialize(_windowHandle);
    }

    public void AttachKeyboardHookService(KeyboardHookService keyboardHookService)
    {
        _keyboardHookService = keyboardHookService;
    }

    public async Task ShowSettingsWindowAsync()
    {
        await _viewModel.InitializeAsync();
        ApplyViewModelToControls();

        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_RESTORE);
        Activate();
        NativeMethods.SetForegroundWindow(_windowHandle);
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
        Title = "Timbre Settings";

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(760, 820));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        ApplyWindowTheme();
    }

    private void ApplyViewModelToControls()
    {
        _isApplyingViewModel = true;

        try
        {
            InputDeviceComboBox.ItemsSource = _viewModel.InputDevices;
            InputDeviceComboBox.SelectedItem = _viewModel.SelectedInputDevice;
            GroqProviderRadioButton.IsChecked = _viewModel.IsGroqSelected;
            FireworksProviderRadioButton.IsChecked = _viewModel.IsFireworksSelected;
            HotkeyCaptureButton.Content = _viewModel.RecordingHotkeyDisplay;
            PasteLastTranscriptHotkeyCaptureButton.Content = _viewModel.PasteLastTranscriptHotkeyDisplay;
            TranscriptHistoryLimitNumberBox.Value = _viewModel.TranscriptHistoryLimitValue;
            PushToTalkToggle.IsOn = _viewModel.PushToTalk;
            GroqApiKeyBox.Password = _viewModel.GroqApiKey;
            GroqModelComboBox.ItemsSource = _viewModel.AvailableGroqModels;
            GroqModelComboBox.SelectedItem = _viewModel.SelectedGroqModel;
            GroqLanguageTextBox.Text = _viewModel.GroqLanguage;
            FireworksApiKeyBox.Password = _viewModel.FireworksApiKey;
            FireworksModelComboBox.ItemsSource = _viewModel.AvailableFireworksModels;
            FireworksModelComboBox.SelectedItem = _viewModel.SelectedFireworksModel;
            FireworksLanguageTextBox.Text = _viewModel.FireworksLanguage;
            GroqSettingsPanel.Visibility = _viewModel.GroqSettingsVisibility;
            FireworksSettingsPanel.Visibility = _viewModel.FireworksSettingsVisibility;
            RestoreStatusText();
            HotkeyWarningTextBlock.Text = _viewModel.HotkeyWarningMessage;
            HotkeyWarningTextBlock.Visibility = _viewModel.HotkeyWarningVisibility;
            CancelTranscriptionButton.Visibility = _viewModel.CancelTranscriptionVisibility;
        }
        finally
        {
            _isApplyingViewModel = false;
        }
    }

    private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        StartHotkeyCapture(HotkeyCaptureButton, _viewModel.ApplyRecordingHotkey);
    }

    private void PasteLastTranscriptHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        StartHotkeyCapture(PasteLastTranscriptHotkeyCaptureButton, _viewModel.ApplyPasteLastTranscriptHotkey);
    }

    private async void ResetHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ApplyRecordingHotkey(HotkeyBinding.Default);
        ApplyViewModelToControls();
        await SaveSettingsAsync(immediate: true);
    }

    private async void ResetPasteLastTranscriptHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ApplyPasteLastTranscriptHotkey(HotkeyBinding.PasteLastTranscriptDefault);
        ApplyViewModelToControls();
        await SaveSettingsAsync(immediate: true);
    }

    private async void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ReloadDevicesAsync();
        ApplyViewModelToControls();
    }

    private void ProviderRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectedProvider = FireworksProviderRadioButton.IsChecked == true
            ? TranscriptionProvider.Fireworks
            : TranscriptionProvider.Groq;
        ApplyViewModelToControls();
        QueueAutoSave();
    }

    private async void CancelTranscriptionButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.CancelTranscriptionAsync();
        ApplyViewModelToControls();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _autoSaveCancellationTokenSource?.Cancel();
        await SaveSettingsAsync(immediate: true);
    }

    private async void OpenHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_historyWindow is null)
        {
            var app = (App)Application.Current;
            _historyWindow = app.Services.GetRequiredService<TranscriptionHistoryWindow>();
            _historyWindow.Closed += OnHistoryWindowClosed;
        }

        await _historyWindow.ShowHistoryWindowAsync();
    }

    private void SettingsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        QueueAutoSave();
    }

    private void SettingsTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        QueueAutoSave();
    }

    private void SettingsPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        QueueAutoSave();
    }

    private void PushToTalkToggle_Toggled(object sender, RoutedEventArgs e)
    {
        QueueAutoSave();
    }

    private void TranscriptHistoryLimitNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        QueueAutoSave();
    }

    private void StartHotkeyCapture(Button button, Action<HotkeyBinding> onHotkeyCaptured)
    {
        if (_keyboardHookService is null)
        {
            SetStatusText("The keyboard hook is not ready yet.");
            return;
        }

        SetHotkeyCaptureButtonsEnabled(false);
        button.Content = "Press hotkey...";
        SetStatusText("Press the new hotkey now.");
        _keyboardHookService.BeginHotkeyCapture(hotkey => HotkeyCaptured(button, hotkey, onHotkeyCaptured));
    }

    private async void HotkeyCaptured(Button button, HotkeyBinding hotkey, Action<HotkeyBinding> onHotkeyCaptured)
    {
        onHotkeyCaptured(hotkey);
        button.Content = hotkey.ToDisplayString();
        SetHotkeyCaptureButtonsEnabled(true);
        ApplyViewModelToControls();
        await SaveSettingsAsync(immediate: true);
    }

    private void SetHotkeyCaptureButtonsEnabled(bool isEnabled)
    {
        HotkeyCaptureButton.IsEnabled = isEnabled;
        PasteLastTranscriptHotkeyCaptureButton.IsEnabled = isEnabled;
        ResetHotkeyButton.IsEnabled = isEnabled;
        ResetPasteLastTranscriptHotkeyButton.IsEnabled = isEnabled;
    }

    private void QueueAutoSave()
    {
        if (_isApplyingViewModel)
        {
            return;
        }

        _autoSaveCancellationTokenSource?.Cancel();
        _autoSaveCancellationTokenSource?.Dispose();
        _autoSaveCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _autoSaveCancellationTokenSource.Token;
        _ = SaveSettingsAsync(immediate: false, cancellationToken);
    }

    private async Task SaveSettingsAsync(bool immediate, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!immediate)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
            }

            await _saveLock.WaitAsync(cancellationToken);

            try
            {
                ApplyControlsToViewModel();
                var saved = await _viewModel.SaveSettingsAsync();
                ApplyViewModelToControls();

                if (saved)
                {
                    await ShowTransientSavedStatusAsync();
                }
            }
            finally
            {
                _saveLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ApplyViewModelToControls();
            await ShowDialogAsync("Settings could not be saved", exception.Message);
        }
    }

    private void ApplyControlsToViewModel()
    {
        _viewModel.SelectedInputDevice = InputDeviceComboBox.SelectedItem as AudioInputDevice;
        _viewModel.SelectedProvider = FireworksProviderRadioButton.IsChecked == true
            ? TranscriptionProvider.Fireworks
            : TranscriptionProvider.Groq;
        _viewModel.GroqApiKey = GroqApiKeyBox.Password;
        _viewModel.FireworksApiKey = FireworksApiKeyBox.Password;
        _viewModel.PushToTalk = PushToTalkToggle.IsOn;
        _viewModel.TranscriptHistoryLimitValue = TranscriptHistoryLimitNumberBox.Value;
        _viewModel.SelectedGroqModel = GroqModelComboBox.SelectedItem as string ?? _viewModel.AvailableGroqModels[0];
        _viewModel.GroqLanguage = GroqLanguageTextBox.Text;
        _viewModel.SelectedFireworksModel = FireworksModelComboBox.SelectedItem as string ?? _viewModel.AvailableFireworksModels[0];
        _viewModel.FireworksLanguage = FireworksLanguageTextBox.Text;
    }

    private async Task ShowTransientSavedStatusAsync()
    {
        var statusToken = Interlocked.Increment(ref _statusMessageToken);
        StatusTextBlock.Text = "Changes saved.";
        StatusTextBlock.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 0, 92, 175));
        StatusTextBlock.FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 };
        StatusTextBlock.Opacity = 1;

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        if (statusToken != _statusMessageToken)
        {
            return;
        }

        RestoreStatusText();
    }

    private void RestoreStatusText()
    {
        Interlocked.Increment(ref _statusMessageToken);
        StatusTextBlock.Text = _viewModel.StatusMessage;
        StatusTextBlock.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        StatusTextBlock.FontWeight = new Windows.UI.Text.FontWeight { Weight = 400 };
        StatusTextBlock.Opacity = 1;
    }

    private void SetStatusText(string message)
    {
        Interlocked.Increment(ref _statusMessageToken);
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        StatusTextBlock.FontWeight = new Windows.UI.Text.FontWeight { Weight = 400 };
        StatusTextBlock.Opacity = 1;
    }

    private async Task ShowDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = RootGrid.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    private void OnRootGridActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyWindowTheme();
    }

    private void ApplyWindowTheme()
    {
        var isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        var darkMode = isDark ? 1 : 0;
        _ = NativeMethods.DwmSetWindowAttribute(
            _windowHandle,
            NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
            ref darkMode,
            sizeof(int));

        if (isDark)
        {
            var captionColor = GetColorRef("SolidBackgroundFillColorBaseBrush");
            var textColor = GetColorRef("TextFillColorPrimaryBrush");
            _ = NativeMethods.DwmSetWindowAttribute(
                _windowHandle,
                NativeMethods.DWMWA_CAPTION_COLOR,
                ref captionColor,
                sizeof(uint));
            _ = NativeMethods.DwmSetWindowAttribute(
                _windowHandle,
                NativeMethods.DWMWA_TEXT_COLOR,
                ref textColor,
                sizeof(uint));
        }
        else
        {
            var defaultColor = NativeMethods.DWMWA_COLOR_DEFAULT;
            _ = NativeMethods.DwmSetWindowAttribute(
                _windowHandle,
                NativeMethods.DWMWA_CAPTION_COLOR,
                ref defaultColor,
                sizeof(uint));
            _ = NativeMethods.DwmSetWindowAttribute(
                _windowHandle,
                NativeMethods.DWMWA_TEXT_COLOR,
                ref defaultColor,
                sizeof(uint));
            _ = NativeMethods.DwmSetWindowAttribute(
                _windowHandle,
                NativeMethods.DWMWA_BORDER_COLOR,
                ref defaultColor,
                sizeof(uint));
        }
    }

    private static uint GetColorRef(string resourceKey)
    {
        var brush = (SolidColorBrush)Application.Current.Resources[resourceKey];
        var color = brush.Color;
        return (uint)(color.R | (color.G << 8) | (color.B << 16));
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
        RootGrid.ActualThemeChanged -= OnRootGridActualThemeChanged;
        _autoSaveCancellationTokenSource?.Cancel();
        _autoSaveCancellationTokenSource?.Dispose();

        if (_historyWindow is not null)
        {
            _historyWindow.Closed -= OnHistoryWindowClosed;
            _historyWindow.Close();
            _historyWindow = null;
        }

        if (_previousWindowProc != IntPtr.Zero)
        {
            NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_WNDPROC, _previousWindowProc);
            _previousWindowProc = IntPtr.Zero;
        }

        _viewModel.Dispose();
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        SettingsSaved?.Invoke(settings);
    }

    private void OnHistoryWindowClosed(object sender, WindowEventArgs args)
    {
        if (_historyWindow is null)
        {
            return;
        }

        _historyWindow.Closed -= OnHistoryWindowClosed;
        _historyWindow = null;
    }
}
