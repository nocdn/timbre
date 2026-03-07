using System.Text.Json;

namespace whisper_windows.Services;

public sealed class TranscriptHistoryStore
{
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly string _historyPath;

    public TranscriptHistoryStore()
    {
        var settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperWindows");

        Directory.CreateDirectory(settingsDirectory);
        _historyPath = Path.Combine(settingsDirectory, "transcript-history.json");
    }

    public async Task AppendAsync(string transcript, int maxEntries)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return;
        }

        await _stateLock.WaitAsync();

        try
        {
            var entries = LoadEntries();

            if (maxEntries <= 0)
            {
                entries.Clear();
            }
            else
            {
                entries.Add(new TranscriptHistoryEntry
                {
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Text = transcript,
                });

                entries = TrimEntries(entries, maxEntries);
            }

            SaveEntries(entries);
            DiagnosticsLogger.Info($"Transcript history updated. EntryCount={entries.Count}, MaxEntries={Math.Max(maxEntries, 0)}.");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<string?> GetLatestTranscriptAsync()
    {
        await _stateLock.WaitAsync();

        try
        {
            return LoadEntries().LastOrDefault()?.Text;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task EnforceRetentionAsync(int maxEntries)
    {
        await _stateLock.WaitAsync();

        try
        {
            var entries = LoadEntries();
            var trimmedEntries = maxEntries <= 0 ? [] : TrimEntries(entries, maxEntries);

            if (trimmedEntries.Count != entries.Count)
            {
                SaveEntries(trimmedEntries);
                DiagnosticsLogger.Info($"Transcript history retention enforced. EntryCount={trimmedEntries.Count}, MaxEntries={Math.Max(maxEntries, 0)}.");
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private List<TranscriptHistoryEntry> LoadEntries()
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        var json = File.ReadAllText(_historyPath);

        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<TranscriptHistoryEntry>>(json, _serializerOptions) ?? [];
    }

    private void SaveEntries(List<TranscriptHistoryEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries, _serializerOptions);
        File.WriteAllText(_historyPath, json);
    }

    private static List<TranscriptHistoryEntry> TrimEntries(List<TranscriptHistoryEntry> entries, int maxEntries)
    {
        if (entries.Count <= maxEntries)
        {
            return entries;
        }

        return entries.Skip(entries.Count - maxEntries).ToList();
    }

    private sealed class TranscriptHistoryEntry
    {
        public DateTimeOffset CreatedAtUtc { get; set; }

        public string Text { get; set; } = string.Empty;
    }
}
