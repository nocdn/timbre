using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        _windowHandle = WindowNative.GetWindowHandle(this);
        _windowProc = HandleWindowMessage;
        _previousWindowProc = NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_WNDPROC, _windowProc);

        ConfigureWindowAppearance();
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
    }

    private void ApplyViewModelToControls()
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
        StatusTextBlock.Text = _viewModel.StatusMessage;
        HotkeyWarningTextBlock.Text = _viewModel.HotkeyWarningMessage;
        HotkeyWarningTextBlock.Visibility = _viewModel.HotkeyWarningVisibility;
        CancelTranscriptionButton.Visibility = _viewModel.CancelTranscriptionVisibility;
        TranscriptHistoryRepeater.ItemsSource = _viewModel.TranscriptHistoryEntries;
        HistoryEmptyTextBlock.Visibility = _viewModel.HistoryEmptyVisibility;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;

        try
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

            await _viewModel.SaveSettingsAsync();
            ApplyViewModelToControls();
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("Settings could not be saved", exception.Message);
        }
        finally
        {
            SaveButton.IsEnabled = true;
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
    }

    private async void CancelTranscriptionButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.CancelTranscriptionAsync();
        ApplyViewModelToControls();
    }

    private async void CopyHistoryItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TranscriptHistoryItemViewModel item)
        {
            await _viewModel.CopyHistoryEntryAsync(item);
            ApplyViewModelToControls();
        }
    }

    private async void DeleteHistoryItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TranscriptHistoryItemViewModel item)
        {
            await _viewModel.DeleteHistoryEntryAsync(item);
            ApplyViewModelToControls();
        }
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Clear transcript history?",
            Content = "This removes all saved transcripts from local history.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            XamlRoot = RootGrid.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await _viewModel.ClearHistoryAsync();
            ApplyViewModelToControls();
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
        ApplyViewModelToControls();
    }

    private void SetHotkeyCaptureButtonsEnabled(bool isEnabled)
    {
        HotkeyCaptureButton.IsEnabled = isEnabled;
        PasteLastTranscriptHotkeyCaptureButton.IsEnabled = isEnabled;
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
}
