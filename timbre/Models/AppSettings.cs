namespace timbre.Models;

public sealed class AppSettings
{
    public string? SelectedInputDeviceId { get; init; }

    public TranscriptionProvider Provider { get; init; } = TranscriptionProvider.Groq;

    public bool LlmPostProcessingEnabled { get; init; }

    public LlmPostProcessingProvider LlmPostProcessingProvider { get; init; } = LlmPostProcessingCatalog.DefaultProvider;

    public string? GroqApiKey { get; init; }

    public string? CerebrasApiKey { get; init; }

    public string? LlmGroqApiKey { get; init; }

    public string? FireworksApiKey { get; init; }

    public string? DeepgramApiKey { get; init; }

    public string? MistralApiKey { get; init; }

    public string? CohereApiKey { get; init; }

    public string? ElevenLabsApiKey { get; init; }

    public HotkeyBinding Hotkey { get; init; } = HotkeyBinding.Default;

    public HotkeyBinding PasteLastTranscriptHotkey { get; init; } = HotkeyBinding.PasteLastTranscriptDefault;

    public HotkeyBinding OpenHistoryHotkey { get; init; } = HotkeyBinding.OpenHistoryDefault;

    public int TranscriptHistoryLimit { get; init; } = 200;

    public bool PushToTalk { get; init; } = true;

    public bool LaunchAtStartup { get; init; }

    public bool SoundFeedbackEnabled { get; init; } = true;

    public string LlmPostProcessingPrompt { get; init; } = LlmPostProcessingCatalog.DefaultPrompt;

    public IReadOnlyList<string>? FetchedCerebrasModels { get; init; }

    public IReadOnlyList<string>? FetchedLlmGroqModels { get; init; }

    public string CerebrasModel { get; init; } = LlmPostProcessingCatalog.DefaultCerebrasModel;

    public string LlmGroqModel { get; init; } = LlmPostProcessingCatalog.DefaultGroqModel;

    public string GroqModel { get; init; } = TranscriptionModelCatalog.DefaultGroqModel;

    public string GroqLanguage { get; init; } = "auto";

    public string FireworksModel { get; init; } = TranscriptionModelCatalog.DefaultFireworksModel;

    public string FireworksLanguage { get; init; } = "auto";

    public string DeepgramModel { get; init; } = TranscriptionModelCatalog.DefaultDeepgramStreamingModel;

    public string DeepgramLanguage { get; init; } = "en";

    public bool DeepgramStreamingEnabled { get; init; } = true;

    public string MistralModel { get; init; } = TranscriptionModelCatalog.DefaultMistralNonStreamingModel;

    public bool MistralStreamingEnabled { get; init; }

    public MistralRealtimeMode MistralRealtimeMode { get; init; } = MistralRealtimeMode.Fast;

    public string CohereModel { get; init; } = TranscriptionModelCatalog.DefaultCohereModel;

    public string CohereLanguage { get; init; } = "en";

    public string ElevenLabsModel { get; init; } = TranscriptionModelCatalog.DefaultElevenLabsNonStreamingModel;

    public bool ElevenLabsStreamingEnabled { get; init; }

    public string ElevenLabsLanguage { get; init; } = "auto";

    public bool HasCompletedInitialSetup { get; init; }
}
