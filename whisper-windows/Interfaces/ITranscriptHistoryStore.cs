using whisper_windows.Models;

namespace whisper_windows.Interfaces;

public interface ITranscriptHistoryStore
{
    event EventHandler? HistoryChanged;

    Task AppendAsync(string transcript, int maxEntries, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TranscriptHistoryEntry>> GetEntriesAsync(CancellationToken cancellationToken = default);

    Task<string?> GetLatestTranscriptAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(string entryId, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);

    Task EnforceRetentionAsync(int maxEntries, CancellationToken cancellationToken = default);
}
