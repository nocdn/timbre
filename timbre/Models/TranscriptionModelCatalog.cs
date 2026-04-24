namespace timbre.Models;

public static class TranscriptionModelCatalog
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

    public static IReadOnlyList<string> GroqModels { get; } =
    [
        DefaultGroqModel,
        "whisper-large-v3",
    ];

    public static IReadOnlyList<string> FireworksModels { get; } =
    [
        DefaultFireworksModel,
        "whisper-v3",
    ];

    public static IReadOnlyList<string> DeepgramStreamingModels { get; } =
    [
        DefaultDeepgramStreamingModel,
    ];

    public static IReadOnlyList<string> DeepgramNonStreamingModels { get; } =
    [
        DefaultDeepgramNonStreamingModel,
    ];

    public static IReadOnlyList<string> MistralStreamingModels { get; } =
    [
        DefaultMistralStreamingModel,
    ];

    public static IReadOnlyList<string> MistralNonStreamingModels { get; } =
    [
        DefaultMistralNonStreamingModel,
    ];

    public static IReadOnlyList<string> CohereModels { get; } =
    [
        DefaultCohereModel,
    ];

    public static IReadOnlyList<string> ElevenLabsStreamingModels { get; } =
    [
        DefaultElevenLabsStreamingModel,
    ];

    public static IReadOnlyList<string> ElevenLabsNonStreamingModels { get; } =
    [
        DefaultElevenLabsNonStreamingModel,
    ];

    public static IReadOnlyList<string> GetDeepgramModels(bool streamingEnabled)
    {
        return streamingEnabled ? DeepgramStreamingModels : DeepgramNonStreamingModels;
    }

    public static IReadOnlyList<string> GetMistralModels(bool streamingEnabled)
    {
        return streamingEnabled ? MistralStreamingModels : MistralNonStreamingModels;
    }

    public static IReadOnlyList<string> GetElevenLabsModels(bool streamingEnabled)
    {
        return streamingEnabled ? ElevenLabsStreamingModels : ElevenLabsNonStreamingModels;
    }

    public static string GetDefaultDeepgramModel(bool streamingEnabled)
    {
        return streamingEnabled ? DefaultDeepgramStreamingModel : DefaultDeepgramNonStreamingModel;
    }

    public static string GetDefaultMistralModel(bool streamingEnabled)
    {
        return streamingEnabled ? DefaultMistralStreamingModel : DefaultMistralNonStreamingModel;
    }

    public static string GetDefaultElevenLabsModel(bool streamingEnabled)
    {
        return streamingEnabled ? DefaultElevenLabsStreamingModel : DefaultElevenLabsNonStreamingModel;
    }

    public static bool InferDeepgramStreamingEnabled(string? model)
    {
        var normalized = model?.Trim().ToLowerInvariant();

        return normalized switch
        {
            DefaultDeepgramNonStreamingModel or "nova-3-general" => false,
            _ => true,
        };
    }

    public static bool InferMistralStreamingEnabled(string? model)
    {
        return string.Equals(model?.Trim(), DefaultMistralStreamingModel, StringComparison.OrdinalIgnoreCase);
    }

    public static bool InferElevenLabsStreamingEnabled(string? model)
    {
        return string.Equals(model?.Trim(), DefaultElevenLabsStreamingModel, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeDeepgramModel(string? model, bool streamingEnabled)
    {
        return SelectSupportedModel(GetDeepgramModels(streamingEnabled), model, GetDefaultDeepgramModel(streamingEnabled));
    }

    public static string NormalizeMistralModel(string? model, bool streamingEnabled)
    {
        return SelectSupportedModel(GetMistralModels(streamingEnabled), model, GetDefaultMistralModel(streamingEnabled));
    }

    public static string NormalizeElevenLabsModel(string? model, bool streamingEnabled)
    {
        return SelectSupportedModel(GetElevenLabsModels(streamingEnabled), model, GetDefaultElevenLabsModel(streamingEnabled));
    }

    private static string SelectSupportedModel(IReadOnlyList<string> models, string? selectedModel, string fallbackModel)
    {
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            return fallbackModel;
        }

        return models.FirstOrDefault(model => string.Equals(model, selectedModel.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? fallbackModel;
    }
}
