namespace timbre.Models;

public enum TranscriptionLanguageMode
{
    NotConfigurable,
    ExplicitCode,
    AutoDetectCode,
}

public sealed class TranscriptionVadSilenceThresholdDefinition
{
    private const double FloatingPointNoiseTolerance = 0.000001;

    public TranscriptionVadSilenceThresholdDefinition(
        double defaultSeconds,
        double minimumSeconds,
        double maximumSeconds,
        double stepSeconds)
    {
        DefaultSeconds = defaultSeconds;
        MinimumSeconds = minimumSeconds;
        MaximumSeconds = maximumSeconds;
        StepSeconds = stepSeconds;
    }

    public double DefaultSeconds { get; }

    public double MinimumSeconds { get; }

    public double MaximumSeconds { get; }

    public double StepSeconds { get; }

    public double NormalizeSeconds(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return DefaultSeconds;
        }

        var clampedValue = Math.Clamp(value.Value, MinimumSeconds, MaximumSeconds);
        var cleanedValue = Math.Round(clampedValue, 3, MidpointRounding.AwayFromZero);
        return Math.Abs(clampedValue - cleanedValue) <= FloatingPointNoiseTolerance
            ? cleanedValue
            : clampedValue;
    }

    public int NormalizeMilliseconds(double? value)
    {
        return (int)Math.Round(NormalizeSeconds(value) * 1000, MidpointRounding.AwayFromZero);
    }
}

public sealed class TranscriptionModelDefinition
{
    public TranscriptionModelDefinition(
        string id,
        bool supportsStreaming,
        bool isDefault = false,
        TranscriptionVadSilenceThresholdDefinition? vadSilenceThreshold = null,
        params string[] aliases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        Id = id.Trim();
        SupportsStreaming = supportsStreaming;
        IsDefault = isDefault;
        VadSilenceThreshold = vadSilenceThreshold;
        Aliases = aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string Id { get; }

    public bool SupportsStreaming { get; }

    public bool IsDefault { get; }

    public TranscriptionVadSilenceThresholdDefinition? VadSilenceThreshold { get; }

    public IReadOnlyList<string> Aliases { get; }

    public bool Matches(string model)
    {
        return string.Equals(Id, model, StringComparison.OrdinalIgnoreCase) ||
               Aliases.Any(alias => string.Equals(alias, model, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class TranscriptionProviderDefinition
{
    private readonly IReadOnlyList<TranscriptionModelDefinition> _streamingModels;
    private readonly IReadOnlyList<TranscriptionModelDefinition> _nonStreamingModels;

    public TranscriptionProviderDefinition(
        TranscriptionProvider provider,
        string displayName,
        IReadOnlyList<TranscriptionModelDefinition> models,
        TranscriptionLanguageMode languageMode,
        string defaultLanguage,
        long? uploadLimitBytes = null,
        bool defaultStreamingEnabled = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultLanguage);

        if (models.Count == 0)
        {
            throw new ArgumentException("A transcription provider must define at least one model.", nameof(models));
        }

        var modelArray = models.ToArray();
        Provider = provider;
        DisplayName = displayName.Trim();
        Models = modelArray;
        LanguageMode = languageMode;
        DefaultLanguage = defaultLanguage.Trim().ToLowerInvariant();
        UploadLimitBytes = uploadLimitBytes;
        DefaultStreamingEnabled = defaultStreamingEnabled;
        _streamingModels = modelArray.Where(model => model.SupportsStreaming).ToArray();
        _nonStreamingModels = modelArray.Where(model => !model.SupportsStreaming).ToArray();
        ModelIds = modelArray.Select(model => model.Id).ToArray();
        StreamingModelIds = _streamingModels.Select(model => model.Id).ToArray();
        NonStreamingModelIds = _nonStreamingModels.Select(model => model.Id).ToArray();
    }

    public TranscriptionProvider Provider { get; }

    public string DisplayName { get; }

    public IReadOnlyList<TranscriptionModelDefinition> Models { get; }

    public IReadOnlyList<string> ModelIds { get; }

    public IReadOnlyList<string> StreamingModelIds { get; }

    public IReadOnlyList<string> NonStreamingModelIds { get; }

    public TranscriptionLanguageMode LanguageMode { get; }

    public string DefaultLanguage { get; }

    public long? UploadLimitBytes { get; }

    public bool DefaultStreamingEnabled { get; }

    public bool SupportsStreaming => _streamingModels.Count > 0;

    public bool SupportsLanguageInput => LanguageMode is TranscriptionLanguageMode.ExplicitCode or TranscriptionLanguageMode.AutoDetectCode;

    public bool SupportsAutoDetectLanguage => LanguageMode == TranscriptionLanguageMode.AutoDetectCode;

    public IReadOnlyList<TranscriptionModelDefinition> GetModels(bool streamingEnabled)
    {
        if (!SupportsStreaming)
        {
            return Models;
        }

        return streamingEnabled ? _streamingModels : _nonStreamingModels;
    }

    public IReadOnlyList<string> GetModelIds(bool streamingEnabled)
    {
        if (!SupportsStreaming)
        {
            return ModelIds;
        }

        return streamingEnabled ? StreamingModelIds : NonStreamingModelIds;
    }

    public string GetDefaultModel(bool streamingEnabled)
    {
        var models = GetModels(streamingEnabled);
        return models.FirstOrDefault(model => model.IsDefault)?.Id
            ?? models.FirstOrDefault()?.Id
            ?? Models.FirstOrDefault(model => model.IsDefault)?.Id
            ?? Models[0].Id;
    }

    public bool InferStreamingEnabled(string? model)
    {
        if (!SupportsStreaming)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return DefaultStreamingEnabled;
        }

        var normalizedModel = model.Trim();
        var match = Models.FirstOrDefault(candidate => candidate.Matches(normalizedModel));
        return match?.SupportsStreaming ?? DefaultStreamingEnabled;
    }

    public string NormalizeModel(string? model, bool streamingEnabled)
    {
        var models = GetModels(streamingEnabled);
        if (!string.IsNullOrWhiteSpace(model))
        {
            var normalizedModel = model.Trim();
            var match = models.FirstOrDefault(candidate => candidate.Matches(normalizedModel));
            if (match is not null)
            {
                return match.Id;
            }
        }

        return GetDefaultModel(streamingEnabled);
    }

    public TranscriptionVadSilenceThresholdDefinition? GetVadSilenceThreshold(bool streamingEnabled)
    {
        return GetModels(streamingEnabled).FirstOrDefault(model => model.VadSilenceThreshold is not null)?.VadSilenceThreshold;
    }

    public bool SupportsVadSilenceThreshold(bool streamingEnabled)
    {
        return GetVadSilenceThreshold(streamingEnabled) is not null;
    }

    public double NormalizeVadSilenceThresholdSeconds(double? value, bool streamingEnabled)
    {
        return GetVadSilenceThreshold(streamingEnabled)?.NormalizeSeconds(value) ?? 0;
    }

    public string NormalizeLanguage(string? language)
    {
        if (LanguageMode == TranscriptionLanguageMode.NotConfigurable)
        {
            return DefaultLanguage;
        }

        if (string.IsNullOrWhiteSpace(language))
        {
            return DefaultLanguage;
        }

        var normalizedLanguage = language.Trim().ToLowerInvariant();
        if (string.Equals(normalizedLanguage, "auto", StringComparison.OrdinalIgnoreCase) && !SupportsAutoDetectLanguage)
        {
            return DefaultLanguage;
        }

        return normalizedLanguage;
    }

    public string? NormalizeRequestLanguage(string? language)
    {
        var normalizedLanguage = NormalizeLanguage(language);
        return SupportsAutoDetectLanguage && string.Equals(normalizedLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalizedLanguage;
    }
}

public static class TranscriptionProviderCatalog
{
    public const string DefaultGroqModel = "whisper-large-v3-turbo";
    public const string DefaultFireworksModel = "whisper-v3-turbo";
    public const string DefaultDeepgramStreamingModel = "flux";
    public const string DefaultDeepgramNonStreamingModel = "nova-3";
    public const string DefaultMistralStreamingModel = "voxtral-mini-transcribe-realtime-2602";
    public const string DefaultMistralNonStreamingModel = "voxtral-mini-latest";
    public const string DefaultCohereModel = "cohere-transcribe-03-2026";
    public const string DefaultElevenLabsStreamingModel = "scribe_v2_realtime";
    public const string DefaultElevenLabsNonStreamingModel = "scribe_v2";
    public const double DefaultDeepgramVadSilenceThresholdSeconds = 5.0;
    public const double DefaultElevenLabsVadSilenceThresholdSeconds = 0.6;

    private static readonly TranscriptionVadSilenceThresholdDefinition DeepgramVadSilenceThreshold = new(
        defaultSeconds: DefaultDeepgramVadSilenceThresholdSeconds,
        minimumSeconds: 0.5,
        maximumSeconds: 10.0,
        stepSeconds: 0.1);

    private static readonly TranscriptionVadSilenceThresholdDefinition ElevenLabsVadSilenceThreshold = new(
        defaultSeconds: DefaultElevenLabsVadSilenceThresholdSeconds,
        minimumSeconds: 0.3,
        maximumSeconds: 3.0,
        stepSeconds: 0.1);

    private static readonly TranscriptionProviderDefinition[] Definitions =
    [
        new(
            TranscriptionProvider.Groq,
            "Groq",
            [
                new(DefaultGroqModel, supportsStreaming: false, isDefault: true),
                new("whisper-large-v3", supportsStreaming: false),
            ],
            TranscriptionLanguageMode.AutoDetectCode,
            defaultLanguage: "auto",
            uploadLimitBytes: 25L * 1024 * 1024),
        new(
            TranscriptionProvider.Fireworks,
            "Fireworks",
            [
                new(DefaultFireworksModel, supportsStreaming: false, isDefault: true),
                new("whisper-v3", supportsStreaming: false),
            ],
            TranscriptionLanguageMode.AutoDetectCode,
            defaultLanguage: "auto",
            uploadLimitBytes: 1024L * 1024 * 1024),
        new(
            TranscriptionProvider.Deepgram,
            "Deepgram",
            [
                new(
                    DefaultDeepgramStreamingModel,
                    supportsStreaming: true,
                    isDefault: true,
                    vadSilenceThreshold: DeepgramVadSilenceThreshold),
                new(
                    DefaultDeepgramNonStreamingModel,
                    supportsStreaming: false,
                    isDefault: true,
                    aliases: new[] { "nova-3-general" }),
            ],
            TranscriptionLanguageMode.NotConfigurable,
            defaultLanguage: "en",
            uploadLimitBytes: 2L * 1024 * 1024 * 1024,
            defaultStreamingEnabled: true),
        new(
            TranscriptionProvider.Mistral,
            "Mistral",
            [
                new(DefaultMistralStreamingModel, supportsStreaming: true, isDefault: true),
                new(DefaultMistralNonStreamingModel, supportsStreaming: false, isDefault: true),
            ],
            TranscriptionLanguageMode.NotConfigurable,
            defaultLanguage: "en"),
        new(
            TranscriptionProvider.Cohere,
            "Cohere",
            [
                new(DefaultCohereModel, supportsStreaming: false, isDefault: true),
            ],
            TranscriptionLanguageMode.ExplicitCode,
            defaultLanguage: "en"),
        new(
            TranscriptionProvider.ElevenLabs,
            "ElevenLabs",
            [
                new(
                    DefaultElevenLabsStreamingModel,
                    supportsStreaming: true,
                    isDefault: true,
                    vadSilenceThreshold: ElevenLabsVadSilenceThreshold),
                new(DefaultElevenLabsNonStreamingModel, supportsStreaming: false, isDefault: true),
            ],
            TranscriptionLanguageMode.AutoDetectCode,
            defaultLanguage: "auto",
            uploadLimitBytes: 3L * 1024 * 1024 * 1024),
    ];

    private static readonly IReadOnlyDictionary<TranscriptionProvider, TranscriptionProviderDefinition> DefinitionsByProvider =
        Definitions.ToDictionary(definition => definition.Provider);

    public static IReadOnlyList<TranscriptionProviderDefinition> Providers => Definitions;

    public static TranscriptionProviderDefinition Get(TranscriptionProvider provider)
    {
        return DefinitionsByProvider.TryGetValue(provider, out var definition)
            ? definition
            : DefinitionsByProvider[TranscriptionProvider.Groq];
    }

    public static IReadOnlyList<string> GetModelIds(TranscriptionProvider provider)
    {
        return Get(provider).ModelIds;
    }

    public static IReadOnlyList<string> GetModelIds(TranscriptionProvider provider, bool streamingEnabled)
    {
        return Get(provider).GetModelIds(streamingEnabled);
    }

    public static string GetDefaultModel(TranscriptionProvider provider, bool streamingEnabled = false)
    {
        return Get(provider).GetDefaultModel(streamingEnabled);
    }

    public static bool InferStreamingEnabled(TranscriptionProvider provider, string? model)
    {
        return Get(provider).InferStreamingEnabled(model);
    }

    public static string NormalizeModel(TranscriptionProvider provider, string? model, bool streamingEnabled = false)
    {
        return Get(provider).NormalizeModel(model, streamingEnabled);
    }

    public static string NormalizeLanguage(TranscriptionProvider provider, string? language)
    {
        return Get(provider).NormalizeLanguage(language);
    }

    public static string? NormalizeRequestLanguage(TranscriptionProvider provider, string? language)
    {
        return Get(provider).NormalizeRequestLanguage(language);
    }

    public static TranscriptionVadSilenceThresholdDefinition? GetVadSilenceThreshold(
        TranscriptionProvider provider,
        bool streamingEnabled)
    {
        return Get(provider).GetVadSilenceThreshold(streamingEnabled);
    }

    public static bool SupportsVadSilenceThreshold(TranscriptionProvider provider, bool streamingEnabled)
    {
        return Get(provider).SupportsVadSilenceThreshold(streamingEnabled);
    }

    public static double NormalizeVadSilenceThresholdSeconds(
        TranscriptionProvider provider,
        double? value,
        bool streamingEnabled)
    {
        return Get(provider).NormalizeVadSilenceThresholdSeconds(value, streamingEnabled);
    }

    public static int NormalizeVadSilenceThresholdMilliseconds(
        TranscriptionProvider provider,
        double? value,
        bool streamingEnabled)
    {
        var definition = GetVadSilenceThreshold(provider, streamingEnabled);
        return definition?.NormalizeMilliseconds(value) ?? 0;
    }

    public static double NormalizeDeepgramVadSilenceThresholdSeconds(double? value)
    {
        return NormalizeVadSilenceThresholdSeconds(TranscriptionProvider.Deepgram, value, streamingEnabled: true);
    }

    public static double NormalizeElevenLabsVadSilenceThresholdSeconds(double? value)
    {
        return NormalizeVadSilenceThresholdSeconds(TranscriptionProvider.ElevenLabs, value, streamingEnabled: true);
    }

    public static bool TryGetUploadLimitBytes(TranscriptionProvider provider, out long uploadLimitBytes)
    {
        var uploadLimit = Get(provider).UploadLimitBytes;
        if (uploadLimit is null)
        {
            uploadLimitBytes = 0;
            return false;
        }

        uploadLimitBytes = uploadLimit.Value;
        return true;
    }
}
