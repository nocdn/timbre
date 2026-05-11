using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class ElevenLabsTranscriptionClient : ITranscriptionClient
{
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
        return _httpExecutor.TranscribeAsync(
            audioBytes,
            TranscriptionHttpRequestSpecs.Create(TranscriptionProvider.ElevenLabs, apiKey, model, language),
            cancellationToken);
    }
}
