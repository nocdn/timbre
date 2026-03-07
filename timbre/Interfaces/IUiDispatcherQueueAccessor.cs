using Microsoft.UI.Dispatching;

namespace timbre.Interfaces;

public interface IUiDispatcherQueueAccessor
{
    DispatcherQueue? DispatcherQueue { get; set; }
}
