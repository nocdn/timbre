using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class TranscriptionClientFactory : ITranscriptionClientFactory
{
    private readonly GroqTranscriptionClient _groqClient;
    private readonly FireworksTranscriptionClient _fireworksClient;

    public TranscriptionClientFactory(GroqTranscriptionClient groqClient, FireworksTranscriptionClient fireworksClient)
    {
        _groqClient = groqClient;
        _fireworksClient = fireworksClient;
    }

    public ITranscriptionClient GetClient(TranscriptionProvider provider)
    {
        return provider == TranscriptionProvider.Fireworks
            ? _fireworksClient
            : _groqClient;
    }
}
