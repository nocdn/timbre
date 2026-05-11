using timbre.Models;

namespace timbre.Services;

internal static class TranscriptionHttpRequestSpecs
{
    private static readonly Uri GroqEndpoint = new("https://api.groq.com/openai/v1/audio/transcriptions");
    private static readonly Uri DeepgramEndpoint = new("https://api.deepgram.com/v1/listen");
    private static readonly Uri MistralEndpoint = new("https://api.mistral.ai/v1/audio/transcriptions");
    private static readonly Uri CohereEndpoint = new("https://api.cohere.com/v2/audio/transcriptions");
    private static readonly Uri ElevenLabsEndpoint = new("https://api.elevenlabs.io/v1/speech-to-text");

    private static readonly IReadOnlyList<KeyValuePair<string, string>> GroqAdditionalFormFields =
    [
        new("response_format", "json"),
    ];

    public static TranscriptionHttpRequestSpec Create(
        TranscriptionProvider provider,
        string apiKey,
        string model,
        string? language)
    {
        return provider switch
        {
            TranscriptionProvider.Deepgram => CreateDeepgram(apiKey, model, language),
            TranscriptionProvider.Mistral => CreateMistral(apiKey, model, language),
            TranscriptionProvider.Cohere => CreateCohere(apiKey, model, language),
            TranscriptionProvider.ElevenLabs => CreateElevenLabs(apiKey, model, language),
            _ => CreateGroq(apiKey, model, language),
        };
    }

    private static TranscriptionHttpRequestSpec CreateGroq(string apiKey, string model, string? language)
    {
        var resolvedModel = string.IsNullOrWhiteSpace(model)
            ? string.Empty
            : TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Groq, model);

        return new TranscriptionHttpRequestSpec
        {
            ProviderName = TranscriptionProviderCatalog.Get(TranscriptionProvider.Groq).DisplayName,
            Endpoint = GroqEndpoint,
            ApiKey = apiKey,
            Model = resolvedModel,
            Language = TranscriptionProviderCatalog.NormalizeRequestLanguage(TranscriptionProvider.Groq, language),
            Authorization = TranscriptionHttpAuthorization.Bearer(apiKey),
            AdditionalFormFields = GroqAdditionalFormFields,
        };
    }

    private static TranscriptionHttpRequestSpec CreateDeepgram(string apiKey, string model, string? language)
    {
        var requestedModel = string.IsNullOrWhiteSpace(model)
            ? TranscriptionProviderCatalog.DefaultDeepgramNonStreamingModel
            : model.Trim().ToLowerInvariant();

        if (requestedModel.StartsWith("flux", StringComparison.OrdinalIgnoreCase))
        {
            throw new TranscriptionException("Deepgram Flux requires streaming mode. Turn on streaming in Deepgram settings to use Flux.", false);
        }

        var resolvedModel = TranscriptionProviderCatalog.NormalizeModel(
            TranscriptionProvider.Deepgram,
            requestedModel,
            streamingEnabled: false);
        var requestLanguage = TranscriptionProviderCatalog.NormalizeRequestLanguage(TranscriptionProvider.Deepgram, language)
            ?? TranscriptionProviderCatalog.Get(TranscriptionProvider.Deepgram).DefaultLanguage;

        return new TranscriptionHttpRequestSpec
        {
            ProviderName = TranscriptionProviderCatalog.Get(TranscriptionProvider.Deepgram).DisplayName,
            Endpoint = UriQuery.Build(
                DeepgramEndpoint,
                ("model", resolvedModel),
                ("language", requestLanguage),
                ("smart_format", "true")),
            ApiKey = apiKey,
            Model = resolvedModel,
            Language = requestLanguage,
            Authorization = TranscriptionHttpAuthorization.SchemeHeader("Token", apiKey),
            BodyKind = TranscriptionHttpBodyKind.RawAudio,
            TranscriptExtractor = TranscriptionHttpResponseParsers.ExtractDeepgramTranscript,
        };
    }

    private static TranscriptionHttpRequestSpec CreateMistral(string apiKey, string model, string? language)
    {
        var resolvedModel = TranscriptionProviderCatalog.NormalizeModel(
            TranscriptionProvider.Mistral,
            model,
            streamingEnabled: false);

        return new TranscriptionHttpRequestSpec
        {
            ProviderName = TranscriptionProviderCatalog.Get(TranscriptionProvider.Mistral).DisplayName,
            Endpoint = MistralEndpoint,
            ApiKey = apiKey,
            Model = resolvedModel,
            Language = TranscriptionProviderCatalog.NormalizeRequestLanguage(TranscriptionProvider.Mistral, language),
            Authorization = TranscriptionHttpAuthorization.Bearer(apiKey),
        };
    }

    private static TranscriptionHttpRequestSpec CreateCohere(string apiKey, string model, string? language)
    {
        return new TranscriptionHttpRequestSpec
        {
            ProviderName = TranscriptionProviderCatalog.Get(TranscriptionProvider.Cohere).DisplayName,
            Endpoint = CohereEndpoint,
            ApiKey = apiKey,
            Model = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Cohere, model),
            Language = TranscriptionProviderCatalog.NormalizeRequestLanguage(TranscriptionProvider.Cohere, language),
            Authorization = TranscriptionHttpAuthorization.Bearer(apiKey),
        };
    }

    private static TranscriptionHttpRequestSpec CreateElevenLabs(string apiKey, string model, string? language)
    {
        var requestedModel = string.IsNullOrWhiteSpace(model)
            ? TranscriptionProviderCatalog.DefaultElevenLabsNonStreamingModel
            : model.Trim();

        if (string.Equals(requestedModel, TranscriptionProviderCatalog.DefaultElevenLabsStreamingModel, StringComparison.OrdinalIgnoreCase))
        {
            throw new TranscriptionException("ElevenLabs Scribe v2 Realtime requires streaming mode. Select Scribe v2 for non-streaming transcription.", false);
        }

        return new TranscriptionHttpRequestSpec
        {
            ProviderName = TranscriptionProviderCatalog.Get(TranscriptionProvider.ElevenLabs).DisplayName,
            Endpoint = ElevenLabsEndpoint,
            ApiKey = apiKey,
            Model = TranscriptionProviderCatalog.NormalizeModel(
                TranscriptionProvider.ElevenLabs,
                requestedModel,
                streamingEnabled: false),
            Language = TranscriptionProviderCatalog.NormalizeRequestLanguage(TranscriptionProvider.ElevenLabs, language),
            Authorization = TranscriptionHttpAuthorization.RawHeader("xi-api-key", apiKey),
            ModelFormFieldName = "model_id",
            LanguageFormFieldName = "language_code",
        };
    }
}
