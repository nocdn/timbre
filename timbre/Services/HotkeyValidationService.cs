using timbre.Models;

namespace timbre.Services;

public static class HotkeyValidationService
{
    public static HotkeyValidationResult Validate(
        HotkeyBinding recordingHotkey,
        HotkeyBinding pasteHotkey,
        HotkeyBinding openHistoryHotkey)
    {
        var result = new HotkeyValidationResult();

        ValidateSingle("Recording", recordingHotkey, result);
        ValidateSingle("Paste last transcript", pasteHotkey, result);
        ValidateSingle("Open history", openHistoryHotkey, result);

        ValidateDistinct("Recording", recordingHotkey, "Paste last transcript", pasteHotkey, result);
        ValidateDistinct("Recording", recordingHotkey, "Open history", openHistoryHotkey, result);
        ValidateDistinct("Paste last transcript", pasteHotkey, "Open history", openHistoryHotkey, result);

        return result;
    }

    private static void ValidateDistinct(
        string firstLabel,
        HotkeyBinding firstHotkey,
        string secondLabel,
        HotkeyBinding secondHotkey,
        HotkeyValidationResult result)
    {
        if (firstHotkey.Equals(secondHotkey))
        {
            result.Errors.Add($"{firstLabel} and {secondLabel.ToLowerInvariant()} hotkeys must be different.");
        }
    }

    private static void ValidateSingle(string label, HotkeyBinding hotkey, HotkeyValidationResult result)
    {
        if (hotkey.KeyCode == 0)
        {
            result.Errors.Add($"{label} hotkey is missing a main key.");
            return;
        }

        if (hotkey.Windows)
        {
            result.Warnings.Add($"{label} hotkey uses the Windows key, which can conflict with system shortcuts.");
        }

        if (hotkey.KeyCode == 0x7B)
        {
            result.Warnings.Add($"{label} hotkey uses F12, which Windows reserves for debugging in some scenarios.");
        }

        if (hotkey.Alt && hotkey.KeyCode == 0x09)
        {
            result.Warnings.Add($"{label} hotkey matches Alt+Tab, which is a common Windows shortcut.");
        }

        if (hotkey.Alt && hotkey.KeyCode == 0x73)
        {
            result.Warnings.Add($"{label} hotkey matches Alt+F4, which closes windows.");
        }

        if (hotkey.Control && hotkey.KeyCode == 0x1B)
        {
            result.Warnings.Add($"{label} hotkey matches Ctrl+Esc, which opens the Start menu.");
        }

        if (hotkey.Control && hotkey.Shift && hotkey.KeyCode == 0x1B)
        {
            result.Warnings.Add($"{label} hotkey matches Ctrl+Shift+Esc, which opens Task Manager.");
        }
    }
}
