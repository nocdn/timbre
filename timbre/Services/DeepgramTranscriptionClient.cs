using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class DeepgramTranscriptionClient : ITranscriptionClient
{
    private readonly TranscriptionHttpExecutor _httpExecutor;

    public DeepgramTranscriptionClient(HttpClient httpClient)
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
            TranscriptionHttpRequestSpecs.Create(TranscriptionProvider.Deepgram, apiKey, model, language),
            cancellationToken);
    }
}
