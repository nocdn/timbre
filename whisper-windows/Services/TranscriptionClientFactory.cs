using whisper_windows.Interfaces;
using whisper_windows.Models;

namespace whisper_windows.Services;

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
