using timbre.Models;

namespace timbre.Interfaces;

public interface IClipboardPasteService
{
    Task CopyTextAsync(string text, CancellationToken cancellationToken = default);

    Task PasteTextAsync(string text, HotkeyBinding? triggeringHotkey = null, CancellationToken cancellationToken = default);

    Task BackupClipboardAsync(CancellationToken cancellationToken = default);

    Task RestoreClipboardAsync(CancellationToken cancellationToken = default);
}
