namespace timbre.Interfaces;

public interface IRealtimeTranscriptionSession : IAsyncDisposable
{
    Task SendAudioAsync(byte[] audioBytes, CancellationToken cancellationToken = default);

    Task<string> CompleteAsync(CancellationToken cancellationToken = default);
}
