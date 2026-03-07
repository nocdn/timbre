using System.Diagnostics;
using System.Text;
using timbre.Interop;

namespace timbre.Services;

public static class DiagnosticsLogger
{
    private static readonly object SyncRoot = new();
    private const string CurrentAppDataFolderName = "Timbre";
    private const string LegacyAppDataFolderName = "WhisperWindows";
    private static string? _logFilePath;
    private static bool _initialized;

    public static string? LogFilePath => _logFilePath;

    public static void Initialize()
    {
        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                CurrentAppDataFolderName,
                "logs");

            Directory.CreateDirectory(logDirectory);
            MigrateLegacyLogs(logDirectory);

            _logFilePath = Path.Combine(
                logDirectory,
                $"startup-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            try
            {
                NativeMethods.AllocConsole();
                Console.OutputEncoding = Encoding.UTF8;
                Console.InputEncoding = Encoding.UTF8;
            }
            catch
            {
            }

            _initialized = true;
            WriteInternal("INFO", "Diagnostics initialized.");
            WriteInternal("INFO", $"Log file: {_logFilePath}");
        }
    }

    public static string GetAppDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var timbreDirectory = Path.Combine(localAppData, CurrentAppDataFolderName);
        Directory.CreateDirectory(timbreDirectory);
        return timbreDirectory;
    }

    public static void HookGlobalExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                Error("AppDomain unhandled exception", exception);
            }
            else
            {
                Info($"AppDomain unhandled non-exception object: {args.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Error("TaskScheduler unobserved task exception", args.Exception);
        };

        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
        {
            if (args.Exception is null)
            {
                return;
            }

            var typeName = args.Exception.GetType().FullName ?? string.Empty;

            if (typeName.StartsWith("System.Runtime.InteropServices.COMException", StringComparison.Ordinal) ||
                typeName.StartsWith("Microsoft.UI", StringComparison.Ordinal) ||
                typeName.StartsWith("WinRT", StringComparison.Ordinal) ||
                typeName.StartsWith("System.InvalidOperationException", StringComparison.Ordinal))
            {
                Error("First-chance exception", args.Exception);
            }
        };
    }

    public static void Info(string message)
    {
        WriteInternal("INFO", message);
    }

    public static void Error(string message, Exception exception)
    {
        WriteInternal("ERROR", $"{message}{Environment.NewLine}{exception}");
    }

    private static void WriteInternal(string level, string message)
    {
        var line = $"{DateTime.Now:O} [{level}] {message}";

        lock (SyncRoot)
        {
            try
            {
                Console.WriteLine(line);
            }
            catch
            {
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(_logFilePath))
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
            }

            Debug.WriteLine(line);
        }
    }

    private static void MigrateLegacyLogs(string newLogDirectory)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacyLogDirectory = Path.Combine(localAppData, LegacyAppDataFolderName, "logs");

        if (!Directory.Exists(legacyLogDirectory))
        {
            return;
        }

        foreach (var legacyLogPath in Directory.EnumerateFiles(legacyLogDirectory, "*.log"))
        {
            var destinationPath = Path.Combine(newLogDirectory, Path.GetFileName(legacyLogPath));
            if (File.Exists(destinationPath))
            {
                continue;
            }

            File.Copy(legacyLogPath, destinationPath);
        }
    }
}
