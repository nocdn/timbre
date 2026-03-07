using timbre.Interfaces;

namespace timbre.Services;

public sealed class NotificationService : INotificationService
{
    private TrayIconService? _trayIconService;

    public void AttachTrayIconService(TrayIconService trayIconService)
    {
        _trayIconService = trayIconService;
    }

    public void ShowNotification(string title, string message, bool isError)
    {
        _trayIconService?.ShowNotification(title, message, isError);
    }
}
