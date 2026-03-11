using System.Diagnostics;
using System.Threading.Channels;

namespace timbre.Services;

public static class DiagnosticsLogger
{
    private static readonly object SyncRoot = new();
    private const string CurrentAppDataFolderName = "Timbre";
    private const string LegacyAppDataFolderName = "WhisperWindows";
    private static string? _logFilePath;
    private static Channel<string>? _logChannel;
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

            _logChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
            StartBackgroundWriter(_logFilePath, _logChannel.Reader);

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

        if (!Debugger.IsAttached)
        {
            return;
        }

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

        Channel<string>? logChannel;
        lock (SyncRoot)
        {
            logChannel = _logChannel;
            Debug.WriteLine(line);
        }

        try
        {
            logChannel?.Writer.TryWrite(line);
        }
        catch
        {
        }
    }

    private static void StartBackgroundWriter(string logFilePath, ChannelReader<string> reader)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var stream = new FileStream(
                    logFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    4096,
                    FileOptions.Asynchronous);
                await using var writer = new StreamWriter(stream)
                {
                    AutoFlush = true,
                };

                await foreach (var line in reader.ReadAllAsync())
                {
                    await writer.WriteLineAsync(line);
                }
            }
            catch
            {
            }
        });
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
