namespace timbre.Models;

public sealed class AppSettings
{
    public string? SelectedInputDeviceId { get; init; }

    public TranscriptionProvider Provider { get; init; } = TranscriptionProvider.Groq;

    public string? GroqApiKey { get; init; }

    public string? FireworksApiKey { get; init; }

    public string? DeepgramApiKey { get; init; }

    public string? MistralApiKey { get; init; }

    public string? CohereApiKey { get; init; }

    public string? AquaVoiceApiKey { get; init; }

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

    public bool MistralRealtimeEnabled { get; init; }

    public MistralRealtimeMode MistralRealtimeMode { get; init; } = MistralRealtimeMode.Fast;

    public string CohereModel { get; init; } = "cohere-transcribe-03-2026";

    public string CohereLanguage { get; init; } = "en";

    public string AquaVoiceModel { get; init; } = "avalon-v1-en";

    public string AquaVoiceLanguage { get; init; } = "en";

    public bool HasCompletedInitialSetup { get; init; }
}
