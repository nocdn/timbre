using timbre.Services;

namespace timbre.Interfaces;

public interface INotificationService
{
    void AttachTrayIconService(TrayIconService trayIconService);

    void ShowNotification(string title, string message, bool isError);
}
