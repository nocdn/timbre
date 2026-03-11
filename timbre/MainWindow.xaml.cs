using System.Globalization;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using timbre.Interop;
using timbre.Models;
using timbre.Services;
using timbre.ViewModels;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace timbre;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan DefaultAutoSaveDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TextInputAutoSaveDelay = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan NumberInputAutoSaveDelay = TimeSpan.FromMilliseconds(600);

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
    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();
        TranscriptHistoryLimitNumberBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(TranscriptHistoryLimitNumberBox_KeyDown), true);

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
        AppIcon.ApplyTo(appWindow);
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
            DeepgramProviderRadioButton.IsChecked = _viewModel.IsDeepgramSelected;
            HotkeyCaptureButton.Content = _viewModel.RecordingHotkeyDisplay;
            PasteLastTranscriptHotkeyCaptureButton.Content = _viewModel.PasteLastTranscriptHotkeyDisplay;
            OpenHistoryHotkeyCaptureButton.Content = _viewModel.OpenHistoryHotkeyDisplay;
            TranscriptHistoryLimitNumberBox.Value = _viewModel.TranscriptHistoryLimitValue;
            PushToTalkToggle.IsOn = _viewModel.PushToTalk;
            LaunchAtStartupToggle.IsOn = _viewModel.LaunchAtStartup;
            SoundFeedbackToggle.IsOn = _viewModel.SoundFeedbackEnabled;
            GroqApiKeyBox.Password = _viewModel.GroqApiKey;
            GroqModelComboBox.ItemsSource = _viewModel.AvailableGroqModels;
            GroqModelComboBox.SelectedItem = _viewModel.SelectedGroqModel;
            GroqLanguageTextBox.Text = _viewModel.GroqLanguage;
            FireworksApiKeyBox.Password = _viewModel.FireworksApiKey;
            FireworksModelComboBox.ItemsSource = _viewModel.AvailableFireworksModels;
            FireworksModelComboBox.SelectedItem = _viewModel.SelectedFireworksModel;
            FireworksLanguageTextBox.Text = _viewModel.FireworksLanguage;
            DeepgramApiKeyBox.Password = _viewModel.DeepgramApiKey;
            DeepgramStreamingToggle.IsOn = _viewModel.DeepgramStreamingEnabled;
            DeepgramModelComboBox.ItemsSource = _viewModel.AvailableDeepgramModels;
            DeepgramModelComboBox.SelectedItem = _viewModel.SelectedDeepgramModel;
            GroqSettingsPanel.Visibility = _viewModel.GroqSettingsVisibility;
            FireworksSettingsPanel.Visibility = _viewModel.FireworksSettingsVisibility;
            DeepgramSettingsPanel.Visibility = _viewModel.DeepgramSettingsVisibility;
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

    private void OpenHistoryHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        StartHotkeyCapture(OpenHistoryHotkeyCaptureButton, _viewModel.ApplyOpenHistoryHotkey);
    }

    private async void ResetOpenHistoryHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ApplyOpenHistoryHotkey(HotkeyBinding.OpenHistoryDefault);
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
            : DeepgramProviderRadioButton.IsChecked == true
                ? TranscriptionProvider.Deepgram
                : TranscriptionProvider.Groq;
        ApplyViewModelToControls();
        QueueAutoSave();
    }

    private async void CancelTranscriptionButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.CancelTranscriptionAsync();
        ApplyViewModelToControls();
    }

    private async void OpenHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowHistoryWindowAsync();
    }

    public async Task ShowHistoryWindowAsync()
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
        QueueAutoSave(TextInputAutoSaveDelay);
    }

    private void SettingsPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        QueueAutoSave(TextInputAutoSaveDelay);
    }

    private void DeepgramStreamingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingViewModel)
        {
            return;
        }

        _viewModel.DeepgramStreamingEnabled = DeepgramStreamingToggle.IsOn;
        ApplyViewModelToControls();
        QueueAutoSave();
    }

    private void PushToTalkToggle_Toggled(object sender, RoutedEventArgs e)
    {
        QueueAutoSave();
    }

    private void LaunchAtStartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        QueueAutoSave();
    }

    private void SoundFeedbackToggle_Toggled(object sender, RoutedEventArgs e)
    {
        QueueAutoSave();
    }

    private void TranscriptHistoryLimitNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        QueueAutoSave(NumberInputAutoSaveDelay);
    }

    private async void TranscriptHistoryLimitNumberBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not VirtualKey.Enter and not VirtualKey.Escape)
        {
            return;
        }

        try
        {
            DiagnosticsLogger.Info(
                $"Transcript history limit keydown received. Key={e.Key}, Text='{TranscriptHistoryLimitNumberBox.Text}', Value={TranscriptHistoryLimitNumberBox.Value}.");

            e.Handled = true;
            CommitTranscriptHistoryLimitInput();

            var focusMoved = OpenHistoryButton.Focus(FocusState.Programmatic);
            DiagnosticsLogger.Info(
                $"Transcript history limit focus move requested. Key={e.Key}, FocusMoved={focusMoved}, Text='{TranscriptHistoryLimitNumberBox.Text}', Value={TranscriptHistoryLimitNumberBox.Value}.");

            _autoSaveCancellationTokenSource?.Cancel();
            await SaveSettingsAsync(immediate: true);
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error(
                $"Transcript history limit key handling failed. Key={e.Key}, Text='{TranscriptHistoryLimitNumberBox.Text}', Value={TranscriptHistoryLimitNumberBox.Value}.",
                exception);
            ApplyViewModelToControls();
            await ShowDialogAsync("Transcript history limit failed", exception.Message);
        }
    }

    private void CommitTranscriptHistoryLimitInput()
    {
        var text = TranscriptHistoryLimitNumberBox.Text?.Trim();
        DiagnosticsLogger.Info($"Committing transcript history limit input. RawText='{text}', CurrentValue={TranscriptHistoryLimitNumberBox.Value}.");

        if (string.IsNullOrWhiteSpace(text))
        {
            TranscriptHistoryLimitNumberBox.Value = _viewModel.TranscriptHistoryLimitValue;
            DiagnosticsLogger.Info($"Transcript history limit input was blank. RestoredValue={TranscriptHistoryLimitNumberBox.Value}.");
            return;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            TranscriptHistoryLimitNumberBox.Value = _viewModel.TranscriptHistoryLimitValue;
            DiagnosticsLogger.Info($"Transcript history limit input could not be parsed. RestoredValue={TranscriptHistoryLimitNumberBox.Value}.");
            return;
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            TranscriptHistoryLimitNumberBox.Value = _viewModel.TranscriptHistoryLimitValue;
            DiagnosticsLogger.Info($"Transcript history limit input was non-finite. RestoredValue={TranscriptHistoryLimitNumberBox.Value}.");
            return;
        }

        TranscriptHistoryLimitNumberBox.Value = Math.Clamp(
            value,
            TranscriptHistoryLimitNumberBox.Minimum,
            TranscriptHistoryLimitNumberBox.Maximum);
        DiagnosticsLogger.Info($"Transcript history limit input committed. CommittedValue={TranscriptHistoryLimitNumberBox.Value}.");
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
        OpenHistoryHotkeyCaptureButton.IsEnabled = isEnabled;
        ResetHotkeyButton.IsEnabled = isEnabled;
        ResetPasteLastTranscriptHotkeyButton.IsEnabled = isEnabled;
        ResetOpenHistoryHotkeyButton.IsEnabled = isEnabled;
    }

    private void QueueAutoSave(TimeSpan? delay = null)
    {
        if (_isApplyingViewModel)
        {
            return;
        }

        _autoSaveCancellationTokenSource?.Cancel();
        _autoSaveCancellationTokenSource?.Dispose();
        _autoSaveCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _autoSaveCancellationTokenSource.Token;
        _ = SaveSettingsAsync(immediate: false, cancellationToken, delay ?? DefaultAutoSaveDelay);
    }

    private async Task SaveSettingsAsync(bool immediate, CancellationToken cancellationToken = default, TimeSpan? autoSaveDelay = null)
    {
        try
        {
            if (!immediate)
            {
                await Task.Delay(autoSaveDelay ?? DefaultAutoSaveDelay, cancellationToken);
            }

            await _saveLock.WaitAsync(cancellationToken);

            try
            {
                ApplyControlsToViewModel();
                await _viewModel.SaveSettingsAsync();
                ApplyViewModelToControls();
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
            DiagnosticsLogger.Error("Settings save failed.", exception);
            ApplyViewModelToControls();
            await ShowDialogAsync("Settings could not be saved", exception.Message);
        }
    }

    private void ApplyControlsToViewModel()
    {
        _viewModel.SelectedInputDevice = InputDeviceComboBox.SelectedItem as AudioInputDevice;
        _viewModel.SelectedProvider = FireworksProviderRadioButton.IsChecked == true
            ? TranscriptionProvider.Fireworks
            : DeepgramProviderRadioButton.IsChecked == true
                ? TranscriptionProvider.Deepgram
                : TranscriptionProvider.Groq;
        _viewModel.GroqApiKey = GroqApiKeyBox.Password;
        _viewModel.FireworksApiKey = FireworksApiKeyBox.Password;
        _viewModel.DeepgramApiKey = DeepgramApiKeyBox.Password;
        _viewModel.DeepgramStreamingEnabled = DeepgramStreamingToggle.IsOn;
        _viewModel.PushToTalk = PushToTalkToggle.IsOn;
        _viewModel.LaunchAtStartup = LaunchAtStartupToggle.IsOn;
        _viewModel.SoundFeedbackEnabled = SoundFeedbackToggle.IsOn;
        _viewModel.TranscriptHistoryLimitValue = TranscriptHistoryLimitNumberBox.Value;
        _viewModel.SelectedGroqModel = GroqModelComboBox.SelectedItem as string ?? _viewModel.AvailableGroqModels[0];
        _viewModel.GroqLanguage = GroqLanguageTextBox.Text;
        _viewModel.SelectedFireworksModel = FireworksModelComboBox.SelectedItem as string ?? _viewModel.AvailableFireworksModels[0];
        _viewModel.FireworksLanguage = FireworksLanguageTextBox.Text;
        _viewModel.SelectedDeepgramModel = DeepgramModelComboBox.SelectedItem as string ?? _viewModel.AvailableDeepgramModels[0];
    }

    private void RestoreStatusText()
    {
        StatusTextBlock.Text = _viewModel.StatusMessage;
        StatusTextBlock.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        StatusTextBlock.FontWeight = new Windows.UI.Text.FontWeight { Weight = 400 };
        StatusTextBlock.Opacity = 1;
        StatusTextBlock.Visibility = string.IsNullOrWhiteSpace(StatusTextBlock.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SetStatusText(string message)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        StatusTextBlock.FontWeight = new Windows.UI.Text.FontWeight { Weight = 400 };
        StatusTextBlock.Opacity = 1;
        StatusTextBlock.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
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
