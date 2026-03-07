using Microsoft.UI.Dispatching;
using timbre.Interfaces;

namespace timbre.Services;

public sealed class UiDispatcherQueueAccessor : IUiDispatcherQueueAccessor
{
    public DispatcherQueue? DispatcherQueue { get; set; }
}
