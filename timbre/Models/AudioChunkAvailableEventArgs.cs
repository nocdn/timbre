namespace timbre.Models;

public sealed class AudioChunkAvailableEventArgs : EventArgs
{
    public AudioChunkAvailableEventArgs(byte[] audioBytes)
    {
        AudioBytes = audioBytes;
    }

    public byte[] AudioBytes { get; }
}
