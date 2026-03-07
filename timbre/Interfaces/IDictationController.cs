using timbre.Models;

namespace timbre.Interfaces;

public interface IDictationController : IDisposable
{
    event EventHandler<DictationStatusChangedEventArgs>? StatusChanged;

    Task<bool> StartDictationAsync();

    Task<bool> StopDictationAsync();

    Task<bool> CancelTranscriptionAsync();

    Task<bool> PasteLastTranscriptAsync(HotkeyBinding? triggeringHotkey = null);
}
