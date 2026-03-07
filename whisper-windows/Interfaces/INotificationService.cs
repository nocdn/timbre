using whisper_windows.Services;

namespace whisper_windows.Interfaces;

public interface INotificationService
{
    void AttachTrayIconService(TrayIconService trayIconService);

    void ShowNotification(string title, string message, bool isError);
}
