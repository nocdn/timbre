using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

public sealed class TranscriptionClientFactory : ITranscriptionClientFactory
{
    private readonly GroqTranscriptionClient _groqClient;
    private readonly DeepgramTranscriptionClient _deepgramClient;
    private readonly MistralTranscriptionClient _mistralClient;
    private readonly CohereTranscriptionClient _cohereClient;
    private readonly ElevenLabsTranscriptionClient _elevenLabsClient;

    public TranscriptionClientFactory(
        GroqTranscriptionClient groqClient,
        DeepgramTranscriptionClient deepgramClient,
        MistralTranscriptionClient mistralClient,
        CohereTranscriptionClient cohereClient,
        ElevenLabsTranscriptionClient elevenLabsClient)
    {
        _groqClient = groqClient;
        _deepgramClient = deepgramClient;
        _mistralClient = mistralClient;
        _cohereClient = cohereClient;
        _elevenLabsClient = elevenLabsClient;
    }

    public ITranscriptionClient GetClient(TranscriptionProvider provider)
    {
        return provider switch
        {
            TranscriptionProvider.Deepgram => _deepgramClient,
            TranscriptionProvider.Mistral => _mistralClient,
            TranscriptionProvider.Cohere => _cohereClient,
            TranscriptionProvider.ElevenLabs => _elevenLabsClient,
            _ => _groqClient,
        };
    }
}
