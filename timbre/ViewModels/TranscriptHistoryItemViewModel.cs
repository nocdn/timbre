using timbre.Models;

namespace timbre.ViewModels;

public sealed class TranscriptHistoryItemViewModel
{
    public TranscriptHistoryItemViewModel(TranscriptHistoryEntry entry)
    {
        EntryId = entry.Id;
        Text = entry.Text;
        CreatedAtDisplay = entry.CreatedAtUtc.ToLocalTime().ToString("g");
    }

    public string EntryId { get; }

    public string Text { get; }

    public string CreatedAtDisplay { get; }

    public bool MatchesSearch(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return Text.Contains(searchText.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }
}
