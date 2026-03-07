using Microsoft.UI.Dispatching;

namespace whisper_windows.Interfaces;

public interface IUiDispatcherQueueAccessor
{
    DispatcherQueue? DispatcherQueue { get; set; }
}
