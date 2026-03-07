namespace timbre.Interfaces;

public interface ITranscriptionClient
{
    Task<string> TranscribeAsync(
        byte[] audioBytes,
        string apiKey,
        string model,
        string? language,
        CancellationToken cancellationToken = default);
}
