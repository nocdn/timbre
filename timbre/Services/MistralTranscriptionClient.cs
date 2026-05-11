using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class MistralTranscriptionClient : ITranscriptionClient
{
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
        return _httpExecutor.TranscribeAsync(
            audioBytes,
            TranscriptionHttpRequestSpecs.Create(TranscriptionProvider.Mistral, apiKey, model, language),
            cancellationToken);
    }
}
