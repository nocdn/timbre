using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class MistralTranscriptionClient : ITranscriptionClient
{
    private static readonly Uri Endpoint = new("https://api.mistral.ai/v1/audio/transcriptions");

    private readonly TranscriptionHttpExecutor _httpExecutor;

    public MistralTranscriptionClient(HttpClient httpClient)
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
        var resolvedModel = TranscriptionProviderCatalog.NormalizeModel(
            TranscriptionProvider.Mistral,
            model,
            streamingEnabled: false);
        var requestLanguage = TranscriptionProviderCatalog.NormalizeRequestLanguage(TranscriptionProvider.Mistral, language);

        return _httpExecutor.TranscribeAsync(
            audioBytes,
            new TranscriptionHttpRequestSpec
            {
                ProviderName = "Mistral",
                Endpoint = Endpoint,
                ApiKey = apiKey,
                Model = resolvedModel,
                Language = requestLanguage,
                Authorization = TranscriptionHttpAuthorization.Bearer(apiKey),
            },
            cancellationToken);
    }
}
