using System.Collections.Specialized;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using timbre.Interop;
using timbre.ViewModels;
using Windows.Graphics;
using WinRT.Interop;

namespace timbre;

public sealed partial class TranscriptionHistoryWindow : Window
{
    private readonly TranscriptionHistoryViewModel _viewModel;
    private readonly IntPtr _windowHandle;
    private bool _isApplyingViewModel;

    public TranscriptionHistoryWindow(TranscriptionHistoryViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        _windowHandle = WindowNative.GetWindowHandle(this);
        ConfigureWindowAppearance();

        HistoryItemsRepeater.ItemsSource = _viewModel.VisibleEntries;
        _viewModel.VisibleEntries.CollectionChanged += OnVisibleEntriesChanged;
        Closed += OnClosed;
    }

    public async Task ShowHistoryWindowAsync()
    {
        await _viewModel.InitializeAsync();
        ApplyViewModelToControls();

        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_RESTORE);
        Activate();
        NativeMethods.SetForegroundWindow(_windowHandle);
    }

    private void ConfigureWindowAppearance()
    {
        Title = "Transcription History";

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(900, 700));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }
    }

    private void ApplyViewModelToControls()
    {
        _isApplyingViewModel = true;

        try
        {
            SearchTextBox.Text = _viewModel.SearchText;
            UpdateEmptyState();
        }
        finally
        {
            _isApplyingViewModel = false;
        }
    }

    private void UpdateEmptyState()
    {
        var hasVisibleEntries = _viewModel.HasVisibleEntries;
        EmptyStateTextBlock.Text = _viewModel.EmptyStateMessage;
        EmptyStateTextBlock.Visibility = hasVisibleEntries ? Visibility.Collapsed : Visibility.Visible;
        HistoryScrollViewer.Visibility = hasVisibleEntries ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.IsEnabled = _viewModel.HasAnyEntries;
    }

    private void OnVisibleEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isApplyingViewModel)
        {
            return;
        }

        _viewModel.SearchText = SearchTextBox.Text;
        UpdateEmptyState();
    }

    private async void CopyHistoryItemButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.CopyEntryAsync((sender as FrameworkElement)?.DataContext as TranscriptHistoryItemViewModel);
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("Transcript could not be copied", exception.Message);
        }
    }

    private async void DeleteHistoryItemButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.DeleteEntryAsync((sender as FrameworkElement)?.DataContext as TranscriptHistoryItemViewModel);
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("Transcript could not be deleted", exception.Message);
        }
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Clear transcription history?",
            Content = "This removes all saved transcripts from local history.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            XamlRoot = RootGrid.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _viewModel.ClearHistoryAsync();
        }
        catch (Exception exception)
        {
            await ShowDialogAsync("History could not be cleared", exception.Message);
        }
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

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _viewModel.VisibleEntries.CollectionChanged -= OnVisibleEntriesChanged;
        _viewModel.Dispose();
    }
}
