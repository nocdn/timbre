using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class ElevenLabsTranscriptionClient : ITranscriptionClient
{
    private static readonly Uri Endpoint = new("https://api.elevenlabs.io/v1/speech-to-text");
    private const string DefaultModel = TranscriptionProviderCatalog.DefaultElevenLabsNonStreamingModel;
    private const string RealtimeModel = TranscriptionProviderCatalog.DefaultElevenLabsStreamingModel;

    private readonly TranscriptionHttpExecutor _httpExecutor;

    public ElevenLabsTranscriptionClient(HttpClient httpClient)
    {
        _httpExecutor = new TranscriptionHttpExecutor(httpClient);
    }

    public Task<string> TranscribeAsync(
        byte[] audioBytes,
        string apiKey,
        string model,
        string? language,
        CancellationToken cancellationToken = default)
    {
        var requestedModel = ResolveModel(model);
        if (string.Equals(requestedModel, RealtimeModel, StringComparison.OrdinalIgnoreCase))
        {
            throw new TranscriptionException("ElevenLabs Scribe v2 Realtime requires streaming mode. Select Scribe v2 for non-streaming transcription.", false);
        }

        var resolvedModel = TranscriptionProviderCatalog.NormalizeModel(
            TranscriptionProvider.ElevenLabs,
            requestedModel,
            streamingEnabled: false);
        var requestLanguage = TranscriptionProviderCatalog.NormalizeRequestLanguage(TranscriptionProvider.ElevenLabs, language);

        return _httpExecutor.TranscribeAsync(
            audioBytes,
            new TranscriptionHttpRequestSpec
            {
                ProviderName = "ElevenLabs",
                Endpoint = Endpoint,
                ApiKey = apiKey,
                Model = resolvedModel,
                Language = requestLanguage,
                Authorization = TranscriptionHttpAuthorization.RawHeader("xi-api-key", apiKey),
                ModelFormFieldName = "model_id",
                LanguageFormFieldName = "language_code",
            },
            cancellationToken);
    }

    private static string ResolveModel(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DefaultModel : value.Trim();
    }
}
