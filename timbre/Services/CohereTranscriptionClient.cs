using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class CohereTranscriptionClient : ITranscriptionClient
{
    private static readonly Uri Endpoint = new("https://api.cohere.com/v2/audio/transcriptions");

    private readonly TranscriptionHttpExecutor _httpExecutor;

    public CohereTranscriptionClient(HttpClient httpClient)
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
        var resolvedModel = TranscriptionProviderCatalog.NormalizeModel(TranscriptionProvider.Cohere, model);
        var requestLanguage = TranscriptionProviderCatalog.NormalizeRequestLanguage(TranscriptionProvider.Cohere, language);

        return _httpExecutor.TranscribeAsync(
            audioBytes,
            new TranscriptionHttpRequestSpec
            {
                ProviderName = "Cohere",
                Endpoint = Endpoint,
                ApiKey = apiKey,
                Model = resolvedModel,
                Language = requestLanguage,
                Authorization = TranscriptionHttpAuthorization.Bearer(apiKey),
            },
            cancellationToken);
    }
}
