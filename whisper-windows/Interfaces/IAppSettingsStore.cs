using whisper_windows.Models;

namespace whisper_windows.Interfaces;

public interface IAppSettingsStore
{
    AppSettings CurrentSettings { get; }

    Task<AppSettings> LoadAsync(bool forceReload = false, CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
