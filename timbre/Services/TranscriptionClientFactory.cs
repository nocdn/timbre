using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class TranscriptionClientFactory : ITranscriptionClientFactory
{
    private readonly GroqTranscriptionClient _groqClient;
    private readonly FireworksTranscriptionClient _fireworksClient;
    private readonly DeepgramTranscriptionClient _deepgramClient;
    private readonly MistralTranscriptionClient _mistralClient;
    private readonly CohereTranscriptionClient _cohereClient;
    private readonly AquaVoiceTranscriptionClient _aquaVoiceClient;

    public TranscriptionClientFactory(
        GroqTranscriptionClient groqClient,
        FireworksTranscriptionClient fireworksClient,
        DeepgramTranscriptionClient deepgramClient,
        MistralTranscriptionClient mistralClient,
        CohereTranscriptionClient cohereClient,
        AquaVoiceTranscriptionClient aquaVoiceClient)
    {
        _groqClient = groqClient;
        _fireworksClient = fireworksClient;
        _deepgramClient = deepgramClient;
        _mistralClient = mistralClient;
        _cohereClient = cohereClient;
        _aquaVoiceClient = aquaVoiceClient;
    }

    public ITranscriptionClient GetClient(TranscriptionProvider provider)
    {
        return provider switch
        {
            TranscriptionProvider.Fireworks => _fireworksClient,
            TranscriptionProvider.Deepgram => _deepgramClient,
            TranscriptionProvider.Mistral => _mistralClient,
            TranscriptionProvider.Cohere => _cohereClient,
            TranscriptionProvider.AquaVoice => _aquaVoiceClient,
            _ => _groqClient,
        };
    }
}
