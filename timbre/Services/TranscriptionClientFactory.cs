using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class TranscriptionClientFactory : ITranscriptionClientFactory
{
    private readonly GroqTranscriptionClient _groqClient;
    private readonly FireworksTranscriptionClient _fireworksClient;
    private readonly DeepgramTranscriptionClient _deepgramClient;

    public TranscriptionClientFactory(
        GroqTranscriptionClient groqClient,
        FireworksTranscriptionClient fireworksClient,
        DeepgramTranscriptionClient deepgramClient)
    {
        _groqClient = groqClient;
        _fireworksClient = fireworksClient;
        _deepgramClient = deepgramClient;
    }

    public ITranscriptionClient GetClient(TranscriptionProvider provider)
    {
        return provider switch
        {
            TranscriptionProvider.Fireworks => _fireworksClient,
            TranscriptionProvider.Deepgram => _deepgramClient,
            _ => _groqClient,
        };
    }
}
