using System.Globalization;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using timbre.Interop;
using timbre.Models;
using timbre.Services;
using timbre.ViewModels;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace timbre;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan DefaultAutoSaveDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TextInputAutoSaveDelay = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan TranscriptHistorySavedMessageDuration = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan TranscriptHistoryValidationMessageDuration = TimeSpan.FromMilliseconds(1750);
    private const string TranscriptHistoryLimitDescriptionText = "Maximum transcripts to keep on disk. The oldest entries are removed first.";
    private const string InvalidTranscriptHistoryLimitDescriptionText = "History amount must be a valid number";

    private readonly MainViewModel _viewModel;
    private readonly IntPtr _windowHandle;
    private readonly NativeMethods.WndProc _windowProc;

    private IntPtr _previousWindowProc;
    private TrayIconService? _trayIconService;
    private KeyboardHookService? _keyboardHookService;
    private TranscriptionHistoryWindow? _historyWindow;
    private CancellationTokenSource? _autoSaveCancellationTokenSource;
    private CancellationTokenSource? _transcriptHistorySavedMessageCancellationTokenSource;
    private CancellationTokenSource? _transcriptHistoryValidationMessageCancellationTokenSource;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _allowClose;
    private bool _isApplyingViewModel;
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
            MistralProviderRadioButton.IsChecked = _viewModel.IsMistralSelected;
            CohereProviderRadioButton.IsChecked = _viewModel.IsCohereSelected;
            AquaVoiceProviderRadioButton.IsChecked = _viewModel.IsAquaVoiceSelected;
            HotkeyCaptureButton.Content = _viewModel.RecordingHotkeyDisplay;
            PasteLastTranscriptHotkeyCaptureButton.Content = _viewModel.PasteLastTranscriptHotkeyDisplay;
            OpenHistoryHotkeyCaptureButton.Content = _viewModel.OpenHistoryHotkeyDisplay;
            TranscriptHistoryLimitTextBox.Text = _viewModel.TranscriptHistoryLimit.ToString(CultureInfo.CurrentCulture);
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
            MistralApiKeyBox.Password = _viewModel.MistralApiKey;
            MistralRealtimeToggle.IsOn = _viewModel.MistralRealtimeEnabled;
            MistralRealtimeModeComboBox.SelectedIndex = _viewModel.MistralRealtimeMode == MistralRealtimeMode.Slow ? 1 : 0;
            MistralRealtimeModeComboBox.IsEnabled = _viewModel.MistralRealtimeEnabled;
            CohereApiKeyBox.Password = _viewModel.CohereApiKey;
            CohereModelComboBox.ItemsSource = _viewModel.AvailableCohereModels;
            CohereModelComboBox.SelectedItem = _viewModel.SelectedCohereModel;
            CohereLanguageTextBox.Text = _viewModel.CohereLanguage;
            AquaVoiceApiKeyBox.Password = _viewModel.AquaVoiceApiKey;
            AquaVoiceModelComboBox.ItemsSource = _viewModel.AvailableAquaVoiceModels;
            AquaVoiceModelComboBox.SelectedItem = _viewModel.SelectedAquaVoiceModel;
            AquaVoiceLanguageTextBox.Text = _viewModel.AquaVoiceLanguage;
            GroqSettingsPanel.Visibility = _viewModel.GroqSettingsVisibility;
            FireworksSettingsPanel.Visibility = _viewModel.FireworksSettingsVisibility;
            DeepgramSettingsPanel.Visibility = _viewModel.DeepgramSettingsVisibility;
            MistralSettingsPanel.Visibility = _viewModel.MistralSettingsVisibility;
            CohereSettingsPanel.Visibility = _viewModel.CohereSettingsVisibility;
            AquaVoiceSettingsPanel.Visibility = _viewModel.AquaVoiceSettingsVisibility;
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
        StartHotkeyCapture(HotkeyCaptureButton, _viewModel.ApplyRecordingHotkey, IsHotkeyAllowedForCapture);
    }

    private void PasteLastTranscriptHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        StartHotkeyCapture(PasteLastTranscriptHotkeyCaptureButton, _viewModel.ApplyPasteLastTranscriptHotkey, IsHotkeyAllowedForCapture);
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
        StartHotkeyCapture(OpenHistoryHotkeyCaptureButton, _viewModel.ApplyOpenHistoryHotkey, IsHotkeyAllowedForCapture);
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
        _viewModel.SelectedProvider = GetSelectedProviderFromControls();
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

    private void MistralRealtimeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingViewModel)
        {
            return;
        }

        _viewModel.MistralRealtimeEnabled = MistralRealtimeToggle.IsOn;
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

    private async void SaveTranscriptHistoryLimitButton_Click(object sender, RoutedEventArgs e)
    {
        var text = TranscriptHistoryLimitTextBox.Text?.Trim();

        if (!TryParseTranscriptHistoryLimit(text, out var value))
        {
            HideTranscriptHistorySavedIndicator();
            await ShowInvalidTranscriptHistoryLimitDescriptionAsync();
            return;
        }

        RestoreTranscriptHistoryLimitDescriptionText();
        _viewModel.TranscriptHistoryLimitValue = value;
        TranscriptHistoryLimitTextBox.Text = _viewModel.TranscriptHistoryLimit.ToString(CultureInfo.CurrentCulture);
        _autoSaveCancellationTokenSource?.Cancel();

        if (await SaveSettingsAsync(immediate: true))
        {
            await ShowTranscriptHistorySavedIndicatorAsync();
        }
    }

    private static bool TryParseTranscriptHistoryLimit(string? text, out double value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = 0;
            return false;
        }

        var wasParsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        return wasParsed && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private async Task ShowInvalidTranscriptHistoryLimitDescriptionAsync()
    {
        _transcriptHistoryValidationMessageCancellationTokenSource?.Cancel();
        _transcriptHistoryValidationMessageCancellationTokenSource?.Dispose();
        _transcriptHistoryValidationMessageCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _transcriptHistoryValidationMessageCancellationTokenSource.Token;

        TranscriptHistoryLimitDescriptionTextBlock.Text = InvalidTranscriptHistoryLimitDescriptionText;
        TranscriptHistoryLimitDescriptionTextBlock.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 0, 0));

        try
        {
            await Task.Delay(TranscriptHistoryValidationMessageDuration, cancellationToken);
            RestoreTranscriptHistoryLimitDescriptionText();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RestoreTranscriptHistoryLimitDescriptionText()
    {
        TranscriptHistoryLimitDescriptionTextBlock.Text = TranscriptHistoryLimitDescriptionText;
        TranscriptHistoryLimitDescriptionTextBlock.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    private async Task ShowTranscriptHistorySavedIndicatorAsync()
    {
        _transcriptHistorySavedMessageCancellationTokenSource?.Cancel();
        _transcriptHistorySavedMessageCancellationTokenSource?.Dispose();
        _transcriptHistorySavedMessageCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _transcriptHistorySavedMessageCancellationTokenSource.Token;

        TranscriptHistoryLimitSavedTextBlock.Visibility = Visibility.Visible;

        try
        {
            await Task.Delay(TranscriptHistorySavedMessageDuration, cancellationToken);
            HideTranscriptHistorySavedIndicator();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void HideTranscriptHistorySavedIndicator()
    {
        TranscriptHistoryLimitSavedTextBlock.Visibility = Visibility.Collapsed;
    }

    private static bool IsHotkeyAllowedForCapture(HotkeyBinding hotkey)
    {
        return HotkeyValidationService.IsAllowedForCapture(hotkey);
    }

    private void StartHotkeyCapture(Button button, Action<HotkeyBinding> onHotkeyCaptured, Func<HotkeyBinding, bool>? hotkeyCaptureValidator = null)
    {
        if (_keyboardHookService is null)
        {
            SetStatusText("The keyboard hook is not ready yet.");
            return;
        }

        SetHotkeyCaptureButtonsEnabled(false);
        button.Content = "Press hotkey...";
        _keyboardHookService.BeginHotkeyCapture(
            hotkey => HotkeyCaptured(button, hotkey, onHotkeyCaptured),
            hotkeyCaptureValidator);
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

    private async Task<bool> SaveSettingsAsync(bool immediate, CancellationToken cancellationToken = default, TimeSpan? autoSaveDelay = null)
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
                var saved = await _viewModel.SaveSettingsAsync();
                ApplyViewModelToControls();
                return saved;
            }
            finally
            {
                _saveLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error("Settings save failed.", exception);
            ApplyViewModelToControls();
            await ShowDialogAsync("Settings could not be saved", exception.Message);
        }

        return false;
    }

    private void ApplyControlsToViewModel()
    {
        _viewModel.SelectedInputDevice = InputDeviceComboBox.SelectedItem as AudioInputDevice;
        _viewModel.SelectedProvider = GetSelectedProviderFromControls();
        _viewModel.GroqApiKey = GroqApiKeyBox.Password;
        _viewModel.FireworksApiKey = FireworksApiKeyBox.Password;
        _viewModel.DeepgramApiKey = DeepgramApiKeyBox.Password;
        _viewModel.MistralApiKey = MistralApiKeyBox.Password;
        _viewModel.CohereApiKey = CohereApiKeyBox.Password;
        _viewModel.AquaVoiceApiKey = AquaVoiceApiKeyBox.Password;
        _viewModel.DeepgramStreamingEnabled = DeepgramStreamingToggle.IsOn;
        _viewModel.MistralRealtimeEnabled = MistralRealtimeToggle.IsOn;
        _viewModel.MistralRealtimeMode = MistralRealtimeModeComboBox.SelectedIndex == 1
            ? MistralRealtimeMode.Slow
            : MistralRealtimeMode.Fast;
        _viewModel.PushToTalk = PushToTalkToggle.IsOn;
        _viewModel.LaunchAtStartup = LaunchAtStartupToggle.IsOn;
        _viewModel.SoundFeedbackEnabled = SoundFeedbackToggle.IsOn;
        _viewModel.SelectedGroqModel = GroqModelComboBox.SelectedItem as string ?? _viewModel.AvailableGroqModels[0];
        _viewModel.GroqLanguage = GroqLanguageTextBox.Text;
        _viewModel.SelectedFireworksModel = FireworksModelComboBox.SelectedItem as string ?? _viewModel.AvailableFireworksModels[0];
        _viewModel.FireworksLanguage = FireworksLanguageTextBox.Text;
        _viewModel.SelectedDeepgramModel = DeepgramModelComboBox.SelectedItem as string ?? _viewModel.AvailableDeepgramModels[0];
        _viewModel.SelectedCohereModel = CohereModelComboBox.SelectedItem as string ?? _viewModel.AvailableCohereModels[0];
        _viewModel.CohereLanguage = CohereLanguageTextBox.Text;
        _viewModel.SelectedAquaVoiceModel = AquaVoiceModelComboBox.SelectedItem as string ?? _viewModel.AvailableAquaVoiceModels[0];
        _viewModel.AquaVoiceLanguage = AquaVoiceLanguageTextBox.Text;
    }

    private TranscriptionProvider GetSelectedProviderFromControls()
    {
        if (MistralProviderRadioButton.IsChecked == true)
        {
            return TranscriptionProvider.Mistral;
        }

        if (AquaVoiceProviderRadioButton.IsChecked == true)
        {
            return TranscriptionProvider.AquaVoice;
        }

        if (CohereProviderRadioButton.IsChecked == true)
        {
            return TranscriptionProvider.Cohere;
        }

        if (DeepgramProviderRadioButton.IsChecked == true)
        {
            return TranscriptionProvider.Deepgram;
        }

        if (FireworksProviderRadioButton.IsChecked == true)
        {
            return TranscriptionProvider.Fireworks;
        }

        return TranscriptionProvider.Groq;
    }

    private void RestoreStatusText()
    {
        StatusTextBlock.Visibility = Visibility.Collapsed;
    }

    private void SetStatusText(string _)
    {
        StatusTextBlock.Visibility = Visibility.Collapsed;
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
        _transcriptHistorySavedMessageCancellationTokenSource?.Cancel();
        _transcriptHistorySavedMessageCancellationTokenSource?.Dispose();
        _transcriptHistoryValidationMessageCancellationTokenSource?.Cancel();
        _transcriptHistoryValidationMessageCancellationTokenSource?.Dispose();

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
