namespace timbre.Interfaces;

public interface IAudioFeedbackService : IDisposable
{
    void WarmUp();

    void PlayRecordingStarted();
}
