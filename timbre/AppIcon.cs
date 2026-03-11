using System.IO;
using Microsoft.UI.Windowing;

namespace timbre;

internal static class AppIcon
{
    private const string RelativePath = @"Assets\AppIcon.ico";

    public static void ApplyTo(AppWindow appWindow)
    {
        if (TryGetPath(out var iconPath))
        {
            appWindow.SetIcon(iconPath);
        }
    }

    public static bool TryGetPath(out string iconPath)
    {
        iconPath = Path.Combine(AppContext.BaseDirectory, RelativePath);
        return File.Exists(iconPath);
    }
}
