using System.Text.Json;
using whisper_windows.Interfaces;
using whisper_windows.Models;

namespace whisper_windows.Services;

public sealed class TranscriptHistoryStore : ITranscriptHistoryStore
{
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly string _historyPath;

    public event EventHandler? HistoryChanged;

    public TranscriptHistoryStore()
    {
        var settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperWindows");

        Directory.CreateDirectory(settingsDirectory);
        _historyPath = Path.Combine(settingsDirectory, "transcript-history.json");
    }

    public async Task AppendAsync(string transcript, int maxEntries, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return;
        }

        await _stateLock.WaitAsync(cancellationToken);
        var hasChanged = false;

        try
        {
            var entries = await LoadEntriesAsync(cancellationToken);

            if (maxEntries <= 0)
            {
                if (entries.Count > 0)
                {
                    entries.Clear();
                    hasChanged = true;
                }
            }
            else
            {
                entries.Add(new TranscriptHistoryEntry
                {
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Text = transcript,
                });

                entries = TrimEntries(entries, maxEntries);
                hasChanged = true;
            }

            if (hasChanged)
            {
                await SaveEntriesAsync(entries, cancellationToken);
            }

            DiagnosticsLogger.Info($"Transcript history updated. EntryCount={entries.Count}, MaxEntries={Math.Max(maxEntries, 0)}.");
        }
        finally
        {
            _stateLock.Release();
        }

        if (hasChanged)
        {
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<IReadOnlyList<TranscriptHistoryEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);

        try
        {
            var entries = await LoadEntriesAsync(cancellationToken);
            return entries
                .OrderByDescending(entry => entry.CreatedAtUtc)
                .ToList();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<string?> GetLatestTranscriptAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);

        try
        {
            var entries = await LoadEntriesAsync(cancellationToken);
            return entries.LastOrDefault()?.Text;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task DeleteAsync(string entryId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return;
        }

        await _stateLock.WaitAsync(cancellationToken);
        var hasChanged = false;

        try
        {
            var entries = await LoadEntriesAsync(cancellationToken);
            hasChanged = entries.RemoveAll(entry => string.Equals(entry.Id, entryId, StringComparison.Ordinal)) > 0;

            if (hasChanged)
            {
                await SaveEntriesAsync(entries, cancellationToken);
                DiagnosticsLogger.Info($"Transcript history item deleted. EntryId='{entryId}'.");
            }
        }
        finally
        {
            _stateLock.Release();
        }

        if (hasChanged)
        {
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);
        var hasChanged = false;

        try
        {
            var entries = await LoadEntriesAsync(cancellationToken);
            hasChanged = entries.Count > 0;

            if (hasChanged)
            {
                await SaveEntriesAsync([], cancellationToken);
                DiagnosticsLogger.Info("Transcript history cleared.");
            }
        }
        finally
        {
            _stateLock.Release();
        }

        if (hasChanged)
        {
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task EnforceRetentionAsync(int maxEntries, CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);
        var hasChanged = false;

        try
        {
            var entries = await LoadEntriesAsync(cancellationToken);
            var trimmedEntries = maxEntries <= 0 ? [] : TrimEntries(entries, maxEntries);

            if (trimmedEntries.Count != entries.Count)
            {
                await SaveEntriesAsync(trimmedEntries, cancellationToken);
                hasChanged = true;
                DiagnosticsLogger.Info($"Transcript history retention enforced. EntryCount={trimmedEntries.Count}, MaxEntries={Math.Max(maxEntries, 0)}.");
            }
        }
        finally
        {
            _stateLock.Release();
        }

        if (hasChanged)
        {
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<List<TranscriptHistoryEntry>> LoadEntriesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        var json = await File.ReadAllTextAsync(_historyPath, cancellationToken);

        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return (JsonSerializer.Deserialize<List<TranscriptHistoryEntry>>(json, _serializerOptions) ?? [])
            .Select(entry => new TranscriptHistoryEntry
            {
                Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id,
                CreatedAtUtc = entry.CreatedAtUtc,
                Text = entry.Text,
            })
            .ToList();
    }

    private async Task SaveEntriesAsync(List<TranscriptHistoryEntry> entries, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(entries, _serializerOptions);
        await File.WriteAllTextAsync(_historyPath, json, cancellationToken);
    }

    private static List<TranscriptHistoryEntry> TrimEntries(List<TranscriptHistoryEntry> entries, int maxEntries)
    {
        if (entries.Count <= maxEntries)
        {
            return entries;
        }

        return entries.Skip(entries.Count - maxEntries).ToList();
    }
}
