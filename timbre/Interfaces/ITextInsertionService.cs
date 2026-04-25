using timbre.Models;

namespace timbre.Interfaces;

public enum TextInsertionMode
{
    Auto,
    PreferUnicodeTyping,
}

public interface ITextInsertionService
{
    Task InsertTextAsync(
        string text,
        HotkeyBinding? triggeringHotkey = null,
        TextInsertionMode insertionMode = TextInsertionMode.Auto,
        CancellationToken cancellationToken = default);
}
