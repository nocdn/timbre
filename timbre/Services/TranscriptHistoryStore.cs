using System.Text.Json;
using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

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
    private List<TranscriptHistoryEntry> _entries = [];
    private bool _hasLoadedEntries;

    public event EventHandler? HistoryChanged;

    public TranscriptHistoryStore()
    {
        var settingsDirectory = DiagnosticsLogger.GetAppDataDirectory();
        MigrateLegacyHistoryFile(settingsDirectory);

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
            var entries = await GetMutableEntriesAsync(cancellationToken);

            if (maxEntries <= 0)
            {
                if (entries.Count > 0)
                {
                    entries = [];
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
                _entries = entries;
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
            var entries = await GetMutableEntriesAsync(cancellationToken);
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
            var entries = await GetMutableEntriesAsync(cancellationToken);
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
            var entries = await GetMutableEntriesAsync(cancellationToken);
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
            var entries = await GetMutableEntriesAsync(cancellationToken);
            hasChanged = entries.Count > 0;

            if (hasChanged)
            {
                _entries = [];
                await SaveEntriesAsync(_entries, cancellationToken);
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
            var entries = await GetMutableEntriesAsync(cancellationToken);
            var trimmedEntries = maxEntries <= 0 ? [] : TrimEntries(entries, maxEntries);

            if (trimmedEntries.Count != entries.Count)
            {
                _entries = trimmedEntries;
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

    private async Task<List<TranscriptHistoryEntry>> GetMutableEntriesAsync(CancellationToken cancellationToken)
    {
        if (_hasLoadedEntries)
        {
            return _entries;
        }

        if (!File.Exists(_historyPath))
        {
            _entries = [];
            _hasLoadedEntries = true;
            return _entries;
        }

        var json = await File.ReadAllTextAsync(_historyPath, cancellationToken);

        if (string.IsNullOrWhiteSpace(json))
        {
            _entries = [];
            _hasLoadedEntries = true;
            return _entries;
        }

        _entries = (JsonSerializer.Deserialize<List<TranscriptHistoryEntry>>(json, _serializerOptions) ?? [])
            .Select(entry => new TranscriptHistoryEntry
            {
                Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id,
                CreatedAtUtc = entry.CreatedAtUtc,
                Text = entry.Text,
            })
            .ToList();
        _hasLoadedEntries = true;
        return _entries;
    }

    private async Task SaveEntriesAsync(List<TranscriptHistoryEntry> entries, CancellationToken cancellationToken)
    {
        _entries = entries;
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

    private static void MigrateLegacyHistoryFile(string settingsDirectory)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacyHistoryPath = Path.Combine(localAppData, "WhisperWindows", "transcript-history.json");
        var newHistoryPath = Path.Combine(settingsDirectory, "transcript-history.json");

        if (!File.Exists(legacyHistoryPath) || File.Exists(newHistoryPath))
        {
            return;
        }

        File.Copy(legacyHistoryPath, newHistoryPath);
    }
}
