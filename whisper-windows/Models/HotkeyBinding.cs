namespace whisper_windows.Models;

public sealed class HotkeyBinding : IEquatable<HotkeyBinding>
{
    public bool Control { get; init; } = true;

    public bool Shift { get; init; }

    public bool Alt { get; init; }

    public bool Windows { get; init; }

    public uint KeyCode { get; init; } = 0x20;

    public string ToDisplayString()
    {
        var parts = new List<string>();

        if (Control)
        {
            parts.Add("Ctrl");
        }

        if (Shift)
        {
            parts.Add("Shift");
        }

        if (Alt)
        {
            parts.Add("Alt");
        }

        if (Windows)
        {
            parts.Add("Win");
        }

        parts.Add(GetKeyName(KeyCode));
        return string.Join("+", parts);
    }

    public static HotkeyBinding Default => new();

    public static HotkeyBinding PasteLastTranscriptDefault => new()
    {
        Alt = true,
    };

    public bool Equals(HotkeyBinding? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return Control == other.Control &&
               Shift == other.Shift &&
               Alt == other.Alt &&
               Windows == other.Windows &&
               KeyCode == other.KeyCode;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as HotkeyBinding);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Control, Shift, Alt, Windows, KeyCode);
    }

    private static string GetKeyName(uint keyCode)
    {
        if (keyCode >= 0x41 && keyCode <= 0x5A)
        {
            return ((char)keyCode).ToString();
        }

        if (keyCode >= 0x30 && keyCode <= 0x39)
        {
            return ((char)keyCode).ToString();
        }

        return keyCode switch
        {
            0x20 => "Space",
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            >= 0x70 and <= 0x7B => $"F{keyCode - 0x6F}",
            _ => $"Key 0x{keyCode:X2}",
        };
    }
}
