namespace whisper_windows.Models;

public sealed class HotkeyValidationResult
{
    public List<string> Errors { get; } = [];

    public List<string> Warnings { get; } = [];

    public bool HasErrors => Errors.Count > 0;

    public bool HasWarnings => Warnings.Count > 0;
}
