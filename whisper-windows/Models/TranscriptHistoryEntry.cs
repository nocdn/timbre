namespace whisper_windows.Models;

public sealed class TranscriptHistoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAtUtc { get; init; }

    public string Text { get; init; } = string.Empty;
}
