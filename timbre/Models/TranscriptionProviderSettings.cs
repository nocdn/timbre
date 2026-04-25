namespace timbre.Models;

public sealed record TranscriptionProviderSettings(
    TranscriptionProvider Provider,
    string ApiKey,
    string Model,
    string Language,
    bool StreamingEnabled,
    double VadSilenceThresholdSeconds)
{
    public TranscriptionProviderDefinition Definition => TranscriptionProviderCatalog.Get(Provider);

    public string DisplayName => Definition.DisplayName;

    public string? RequestLanguage => Definition.NormalizeRequestLanguage(Language);

    public bool UsesRealtimeStreaming => Definition.SupportsStreaming && StreamingEnabled;
}

public static class TranscriptionProviderSettingsAccessor
{
    public static TranscriptionProviderSettings GetTranscriptionProviderSettings(this AppSettings settings)
    {
        return settings.GetTranscriptionProviderSettings(settings.Provider);
    }

    public static TranscriptionProviderSettings GetTranscriptionProviderSettings(
        this AppSettings settings,
        TranscriptionProvider provider)
    {
        var definition = TranscriptionProviderCatalog.Get(provider);
        var streamingEnabled = definition.SupportsStreaming && GetStreamingEnabled(settings, provider);
        var model = definition.NormalizeModel(GetModel(settings, provider), streamingEnabled);
        var language = definition.NormalizeLanguage(GetLanguage(settings, provider));
        var vadSilenceThresholdSeconds = NormalizeVadSilenceThresholdSeconds(
            definition,
            GetVadSilenceThresholdSeconds(settings, provider),
            streamingEnabled);

        return new TranscriptionProviderSettings(
            provider,
            NormalizeApiKey(GetApiKey(settings, provider)),
            model,
            language,
            streamingEnabled,
            vadSilenceThresholdSeconds);
    }

    public static IReadOnlyList<TranscriptionProviderSettings> GetAllTranscriptionProviderSettings(this AppSettings settings)
    {
        return TranscriptionProviderCatalog.Providers
            .Select(definition => settings.GetTranscriptionProviderSettings(definition.Provider))
            .ToArray();
    }

    private static string? GetApiKey(AppSettings settings, TranscriptionProvider provider)
    {
        return provider switch
        {
            TranscriptionProvider.Fireworks => settings.FireworksApiKey,
            TranscriptionProvider.Deepgram => settings.DeepgramApiKey,
            TranscriptionProvider.Mistral => settings.MistralApiKey,
            TranscriptionProvider.Cohere => settings.CohereApiKey,
            TranscriptionProvider.ElevenLabs => settings.ElevenLabsApiKey,
            _ => settings.GroqApiKey,
        };
    }

    private static string? GetModel(AppSettings settings, TranscriptionProvider provider)
    {
        return provider switch
        {
            TranscriptionProvider.Fireworks => settings.FireworksModel,
            TranscriptionProvider.Deepgram => settings.DeepgramModel,
            TranscriptionProvider.Mistral => settings.MistralModel,
            TranscriptionProvider.Cohere => settings.CohereModel,
            TranscriptionProvider.ElevenLabs => settings.ElevenLabsModel,
            _ => settings.GroqModel,
        };
    }

    private static string? GetLanguage(AppSettings settings, TranscriptionProvider provider)
    {
        return provider switch
        {
            TranscriptionProvider.Fireworks => settings.FireworksLanguage,
            TranscriptionProvider.Deepgram => settings.DeepgramLanguage,
            TranscriptionProvider.Mistral => null,
            TranscriptionProvider.Cohere => settings.CohereLanguage,
            TranscriptionProvider.ElevenLabs => settings.ElevenLabsLanguage,
            _ => settings.GroqLanguage,
        };
    }

    private static bool GetStreamingEnabled(AppSettings settings, TranscriptionProvider provider)
    {
        return provider switch
        {
            TranscriptionProvider.Deepgram => settings.DeepgramStreamingEnabled,
            TranscriptionProvider.Mistral => settings.MistralStreamingEnabled,
            TranscriptionProvider.ElevenLabs => settings.ElevenLabsStreamingEnabled,
            _ => false,
        };
    }

    private static double? GetVadSilenceThresholdSeconds(AppSettings settings, TranscriptionProvider provider)
    {
        return provider switch
        {
            TranscriptionProvider.Deepgram => settings.DeepgramVadSilenceThresholdSeconds,
            TranscriptionProvider.ElevenLabs => settings.ElevenLabsVadSilenceThresholdSeconds,
            _ => null,
        };
    }

    private static double NormalizeVadSilenceThresholdSeconds(
        TranscriptionProviderDefinition definition,
        double? value,
        bool streamingEnabled)
    {
        var vadDefinition = definition.GetVadSilenceThreshold(streamingEnabled)
            ?? (definition.SupportsStreaming ? definition.GetVadSilenceThreshold(streamingEnabled: true) : null);

        return vadDefinition?.NormalizeSeconds(value) ?? 0;
    }

    private static string NormalizeApiKey(string? apiKey)
    {
        return string.IsNullOrWhiteSpace(apiKey) ? string.Empty : apiKey.Trim();
    }
}
