namespace timbre.Interfaces;

public interface ILaunchAtStartupService
{
    bool IsEnabled();

    void SetEnabled(bool isEnabled);
}
