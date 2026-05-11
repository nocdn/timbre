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
    private static readonly IReadOnlyDictionary<TranscriptionProvider, AppSettingsProviderFieldAccessors> ProviderFields =
        new AppSettingsProviderFieldAccessors[]
        {
            new(
                TranscriptionProvider.Groq,
                settings => settings.GroqApiKey,
                settings => settings.GroqModel,
                settings => settings.GroqLanguage,
                _ => false,
                _ => null),
            new(
                TranscriptionProvider.Deepgram,
                settings => settings.DeepgramApiKey,
                settings => settings.DeepgramModel,
                settings => settings.DeepgramLanguage,
                settings => settings.DeepgramStreamingEnabled,
                settings => settings.DeepgramVadSilenceThresholdSeconds),
            new(
                TranscriptionProvider.Mistral,
                settings => settings.MistralApiKey,
                settings => settings.MistralModel,
                _ => null,
                settings => settings.MistralStreamingEnabled,
                _ => null),
            new(
                TranscriptionProvider.Cohere,
                settings => settings.CohereApiKey,
                settings => settings.CohereModel,
                settings => settings.CohereLanguage,
                _ => false,
                _ => null),
            new(
                TranscriptionProvider.ElevenLabs,
                settings => settings.ElevenLabsApiKey,
                settings => settings.ElevenLabsModel,
                settings => settings.ElevenLabsLanguage,
                settings => settings.ElevenLabsStreamingEnabled,
                settings => settings.ElevenLabsVadSilenceThresholdSeconds),
        }.ToDictionary(fields => fields.Provider);

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
        return GetFields(provider).GetApiKey(settings);
    }

    private static string? GetModel(AppSettings settings, TranscriptionProvider provider)
    {
        return GetFields(provider).GetModel(settings);
    }

    private static string? GetLanguage(AppSettings settings, TranscriptionProvider provider)
    {
        return GetFields(provider).GetLanguage(settings);
    }

    private static bool GetStreamingEnabled(AppSettings settings, TranscriptionProvider provider)
    {
        return GetFields(provider).GetStreamingEnabled(settings);
    }

    private static double? GetVadSilenceThresholdSeconds(AppSettings settings, TranscriptionProvider provider)
    {
        return GetFields(provider).GetVadSilenceThresholdSeconds(settings);
    }

    private static AppSettingsProviderFieldAccessors GetFields(TranscriptionProvider provider)
    {
        return ProviderFields.TryGetValue(provider, out var fields)
            ? fields
            : ProviderFields[TranscriptionProvider.Groq];
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

    private sealed record AppSettingsProviderFieldAccessors(
        TranscriptionProvider Provider,
        Func<AppSettings, string?> GetApiKey,
        Func<AppSettings, string?> GetModel,
        Func<AppSettings, string?> GetLanguage,
        Func<AppSettings, bool> GetStreamingEnabled,
        Func<AppSettings, double?> GetVadSilenceThresholdSeconds);
}
