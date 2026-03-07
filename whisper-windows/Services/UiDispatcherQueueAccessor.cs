using Microsoft.UI.Dispatching;
using whisper_windows.Interfaces;

namespace whisper_windows.Services;

public sealed class UiDispatcherQueueAccessor : IUiDispatcherQueueAccessor
{
    public DispatcherQueue? DispatcherQueue { get; set; }
}
