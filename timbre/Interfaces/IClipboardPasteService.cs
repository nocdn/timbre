namespace timbre.Interfaces;

public interface IClipboardPasteService
{
    Task CopyTextAsync(string text, CancellationToken cancellationToken = default);
}
