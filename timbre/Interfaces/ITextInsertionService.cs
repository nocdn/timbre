using timbre.Models;

namespace timbre.Interfaces;

public interface ITextInsertionService
{
    Task InsertTextAsync(string text, HotkeyBinding? triggeringHotkey = null, CancellationToken cancellationToken = default);
}
