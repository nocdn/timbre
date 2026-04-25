namespace timbre.Models;

public static class TranscriptionModelCatalog
{
    public static string DefaultGroqModel => TranscriptionProviderCatalog.DefaultGroqModel;
    public static string DefaultFireworksModel => TranscriptionProviderCatalog.DefaultFireworksModel;
    public static string DefaultDeepgramStreamingModel => TranscriptionProviderCatalog.DefaultDeepgramStreamingModel;
    public static string DefaultDeepgramNonStreamingModel => TranscriptionProviderCatalog.DefaultDeepgramNonStreamingModel;
    public static string DefaultMistralStreamingModel => TranscriptionProviderCatalog.DefaultMistralStreamingModel;
    public static string DefaultMistralNonStreamingModel => TranscriptionProviderCatalog.DefaultMistralNonStreamingModel;
    public static string DefaultCohereModel => TranscriptionProviderCatalog.DefaultCohereModel;
    public static string DefaultElevenLabsStreamingModel => TranscriptionProviderCatalog.DefaultElevenLabsStreamingModel;
    public static string DefaultElevenLabsNonStreamingModel => TranscriptionProviderCatalog.DefaultElevenLabsNonStreamingModel;

    public static IReadOnlyList<string> GroqModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Groq);

    public static IReadOnlyList<string> FireworksModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Fireworks);

    public static IReadOnlyList<string> DeepgramStreamingModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Deepgram, streamingEnabled: true);

    public static IReadOnlyList<string> DeepgramNonStreamingModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Deepgram, streamingEnabled: false);

    public static IReadOnlyList<string> MistralStreamingModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Mistral, streamingEnabled: true);

    public static IReadOnlyList<string> MistralNonStreamingModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Mistral, streamingEnabled: false);

    public static IReadOnlyList<string> CohereModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Cohere);

    public static IReadOnlyList<string> ElevenLabsStreamingModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.ElevenLabs, streamingEnabled: true);

    public static IReadOnlyList<string> ElevenLabsNonStreamingModels => TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.ElevenLabs, streamingEnabled: false);

    public static IReadOnlyList<string> GetDeepgramModels(bool streamingEnabled)
    {
        return TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Deepgram, streamingEnabled);
    }

    public static IReadOnlyList<string> GetMistralModels(bool streamingEnabled)
    {
        return TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.Mistral, streamingEnabled);
    }

    public static IReadOnlyList<string> GetElevenLabsModels(bool streamingEnabled)
    {
        return TranscriptionProviderCatalog.GetModelIds(TranscriptionProvider.ElevenLabs, streamingEnabled);
    }

    public static string GetDefaultDeepgramModel(bool streamingEnabled)
    {
        return TranscriptionProviderCatalog.GetDefaultModel(TranscriptionProvider.Deepgram, streamingEnabled);
    }

    public static string GetDefaultMistralModel(bool streamingEnabled)
    {
        return TranscriptionProviderCatalog.GetDefaultModel(TranscriptionProvider.Mistral, streamingEnabled);
    }

    public static string GetDefaultElevenLabsModel(bool streamingEnabled)
    {
        return TranscriptionProviderCatalog.GetDefaultModel(TranscriptionProvider.ElevenLabs, streamingEnabled);
    }

    public static bool InferDeepgramStreamingEnabled(string? model)
    {
        return TranscriptionProviderCatalog.InferStreamingEnabled(TranscriptionProvider.Deepgram, model);
    }

    public static bool InferMistralStreamingEnabled(string? model)
    {
        return TranscriptionProviderCatalog.InferStreamingEnabled(TranscriptionProvider.Mistral, model);
    }

    public static bool InferElevenLabsStreamingEnabled(string? model)
    {
        return TranscriptionProviderCatalog.InferStreamingEnabled(TranscriptionProvider.ElevenLabs, model);
    }

    public static string NormalizeDeepgramModel(string? model, bool streamingEnabled)
    {
        return TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Deepgram, model, streamingEnabled);
    }

    public static string NormalizeMistralModel(string? model, bool streamingEnabled)
    {
        return TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Mistral, model, streamingEnabled);
    }

    public static string NormalizeElevenLabsModel(string? model, bool streamingEnabled)
    {
        return TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.ElevenLabs, model, streamingEnabled);
    }
}
