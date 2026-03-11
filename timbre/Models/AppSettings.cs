namespace timbre.Models;

public sealed class AppSettings
{
    public string? SelectedInputDeviceId { get; init; }

    public TranscriptionProvider Provider { get; init; } = TranscriptionProvider.Groq;

    public string? GroqApiKey { get; init; }

    public string? FireworksApiKey { get; init; }

    public string? DeepgramApiKey { get; init; }

    public HotkeyBinding Hotkey { get; init; } = HotkeyBinding.Default;

    public HotkeyBinding PasteLastTranscriptHotkey { get; init; } = HotkeyBinding.PasteLastTranscriptDefault;

    public HotkeyBinding OpenHistoryHotkey { get; init; } = HotkeyBinding.OpenHistoryDefault;

    public int TranscriptHistoryLimit { get; init; } = 200;

    public bool PushToTalk { get; init; } = true;

    public bool LaunchAtStartup { get; init; }

    public bool SoundFeedbackEnabled { get; init; } = true;

    public string GroqModel { get; init; } = "whisper-large-v3-turbo";

    public string GroqLanguage { get; init; } = "en";

    public string FireworksModel { get; init; } = "whisper-v3-turbo";

    public string FireworksLanguage { get; init; } = "en";

    public string DeepgramModel { get; init; } = "flux";

    public string DeepgramLanguage { get; init; } = "en";

    public bool DeepgramStreamingEnabled { get; init; } = true;

    public bool HasCompletedInitialSetup { get; init; }
}
