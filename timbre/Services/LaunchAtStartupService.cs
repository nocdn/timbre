using Microsoft.Win32;
using timbre.Interfaces;

namespace timbre.Services;

public sealed class LaunchAtStartupService : ILaunchAtStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Timbre";

    public bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = runKey?.GetValue(RunValueName) as string;
        return string.Equals(value, BuildCommandLine(), StringComparison.Ordinal);
    }

    public void SetEnabled(bool isEnabled)
    {
        var expectedValue = BuildCommandLine();
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows could not open the current user's startup registry key.");

        var currentValue = runKey.GetValue(RunValueName) as string;

        if (isEnabled)
        {
            if (string.Equals(currentValue, expectedValue, StringComparison.Ordinal))
            {
                return;
            }

            runKey.SetValue(RunValueName, expectedValue, RegistryValueKind.String);
            return;
        }

        if (currentValue is not null)
        {
            runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    private static string BuildCommandLine()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Windows could not determine the Timbre executable path.");
        }

        return $"\"{processPath}\" {LaunchArguments.Background}";
    }
}
