using whisper_windows.Models;

namespace whisper_windows.Interfaces;

public interface IClipboardPasteService
{
    Task CopyTextAsync(string text, CancellationToken cancellationToken = default);

    Task PasteTextAsync(string text, HotkeyBinding? triggeringHotkey = null, CancellationToken cancellationToken = default);
}
