namespace whisper_windows.Models;

public sealed class AppSettings
{
    public string? SelectedInputDeviceId { get; init; }

    public string? GroqApiKey { get; init; }

    public HotkeyBinding Hotkey { get; init; } = HotkeyBinding.Default;

    public HotkeyBinding PasteLastTranscriptHotkey { get; init; } = HotkeyBinding.PasteLastTranscriptDefault;

    public int TranscriptHistoryLimit { get; init; } = 20;

    public bool PushToTalk { get; init; } = true;

    public string GroqModel { get; init; } = "whisper-large-v3-turbo";
}
