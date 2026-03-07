using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using timbre.Interfaces;

namespace timbre.ViewModels;

public sealed partial class TranscriptionHistoryViewModel : ObservableObject, IDisposable
{
    private readonly ITranscriptHistoryStore _transcriptHistoryStore;
    private readonly IClipboardPasteService _clipboardPasteService;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly List<TranscriptHistoryItemViewModel> _allEntries = [];

    [ObservableProperty]
    private string searchText = string.Empty;

    public TranscriptionHistoryViewModel(
        ITranscriptHistoryStore transcriptHistoryStore,
        IClipboardPasteService clipboardPasteService,
        IUiDispatcherQueueAccessor uiDispatcherQueueAccessor)
    {
        _transcriptHistoryStore = transcriptHistoryStore;
        _clipboardPasteService = clipboardPasteService;
        _dispatcherQueue = uiDispatcherQueueAccessor.DispatcherQueue;

        _transcriptHistoryStore.HistoryChanged += OnHistoryChanged;
    }

    public ObservableCollection<TranscriptHistoryItemViewModel> VisibleEntries { get; } = [];

    public bool HasAnyEntries => _allEntries.Count > 0;

    public bool HasVisibleEntries => VisibleEntries.Count > 0;

    public string EmptyStateMessage => HasAnyEntries
        ? "No transcripts match your search."
        : "No transcripts have been saved yet.";

    public async Task InitializeAsync()
    {
        await ReloadEntriesAsync();
    }

    public async Task CopyEntryAsync(TranscriptHistoryItemViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        await _clipboardPasteService.CopyTextAsync(entry.Text);
    }

    public async Task DeleteEntryAsync(TranscriptHistoryItemViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        await _transcriptHistoryStore.DeleteAsync(entry.EntryId);
    }

    public async Task ClearHistoryAsync()
    {
        await _transcriptHistoryStore.ClearAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    public void Dispose()
    {
        _transcriptHistoryStore.HistoryChanged -= OnHistoryChanged;
    }

    private async void OnHistoryChanged(object? sender, EventArgs e)
    {
        await RunOnUiThreadAsync(ReloadEntriesAsync);
    }

    private async Task ReloadEntriesAsync()
    {
        var entries = await _transcriptHistoryStore.GetEntriesAsync();
        _allEntries.Clear();
        _allEntries.AddRange(entries.Select(entry => new TranscriptHistoryItemViewModel(entry)));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var matchingEntries = _allEntries
            .Where(entry => entry.MatchesSearch(SearchText))
            .ToList();

        VisibleEntries.Clear();
        foreach (var entry in matchingEntries)
        {
            VisibleEntries.Add(entry);
        }

        OnPropertyChanged(nameof(HasAnyEntries));
        OnPropertyChanged(nameof(HasVisibleEntries));
        OnPropertyChanged(nameof(EmptyStateMessage));
    }

    private Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (_dispatcherQueue is null)
        {
            return action();
        }

        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    completionSource.TrySetResult();
                }
                catch (Exception exception)
                {
                    completionSource.TrySetException(exception);
                }
            }))
        {
            return action();
        }

        return completionSource.Task;
    }
}
