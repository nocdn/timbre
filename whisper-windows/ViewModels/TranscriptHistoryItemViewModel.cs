using whisper_windows.Models;

namespace whisper_windows.ViewModels;

public sealed class TranscriptHistoryItemViewModel
{
    public TranscriptHistoryItemViewModel(TranscriptHistoryEntry entry)
    {
        EntryId = entry.Id;
        Text = entry.Text;
        CreatedAtDisplay = entry.CreatedAtUtc.ToLocalTime().ToString("g");
        PreviewText = entry.Text.Length <= 220 ? entry.Text : entry.Text[..220] + "...";
    }

    public string EntryId { get; }

    public string Text { get; }

    public string CreatedAtDisplay { get; }

    public string PreviewText { get; }
}
