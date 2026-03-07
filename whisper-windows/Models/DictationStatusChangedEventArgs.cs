namespace whisper_windows.Models;

public enum DictationState
{
    Idle,
    Recording,
    Transcribing,
    Cancelling,
    Error,
}

public sealed class DictationStatusChangedEventArgs : EventArgs
{
    public DictationStatusChangedEventArgs(DictationState state, string message, bool canCancel)
    {
        State = state;
        Message = message;
        CanCancel = canCancel;
    }

    public DictationState State { get; }

    public string Message { get; }

    public bool CanCancel { get; }
}
