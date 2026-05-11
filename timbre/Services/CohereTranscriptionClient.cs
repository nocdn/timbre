using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class CohereTranscriptionClient : ITranscriptionClient
{
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
        return _httpExecutor.TranscribeAsync(
            audioBytes,
            TranscriptionHttpRequestSpecs.Create(TranscriptionProvider.Cohere, apiKey, model, language),
            cancellationToken);
    }
}
