using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using timbre.Interfaces;
using timbre.Services;
using timbre.ViewModels;

namespace timbre;

public static class Program
{
    private static IntPtr _redirectEventHandle = IntPtr.Zero;

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        DiagnosticsLogger.Initialize();
        DiagnosticsLogger.HookGlobalExceptionLogging();

        if (DecideRedirection())
        {
            return 0;
        }

        Application.Start(applicationInitializationCallbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);

            var startHidden = args.Any(argument => string.Equals(argument, LaunchArguments.Background, StringComparison.OrdinalIgnoreCase));
            var services = ConfigureServices();
            var app = new App(services);
            var coordinator = services.GetRequiredService<AppCoordinator>();
            _ = coordinator.InitializeAsync(startHidden);
        });

        return 0;
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IUiDispatcherQueueAccessor, UiDispatcherQueueAccessor>();
        services.AddSingleton<IAppSettingsStore, AppSettingsStore>();
        services.AddSingleton<IAudioDeviceService, AudioDeviceService>();
        services.AddSingleton<ITranscriptHistoryStore, TranscriptHistoryStore>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IClipboardPasteService, ClipboardPasteService>();
        services.AddSingleton<ILaunchAtStartupService, LaunchAtStartupService>();
        services.AddSingleton<IAudioFeedbackService, AudioFeedbackService>();
        services.AddSingleton<GroqTranscriptionClient>(_ =>
        {
            var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2),
            };
            return new GroqTranscriptionClient(httpClient);
        });
        services.AddSingleton<FireworksTranscriptionClient>(_ =>
        {
            var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2),
            };
            return new FireworksTranscriptionClient(httpClient);
        });
        services.AddSingleton<DeepgramTranscriptionClient>(_ =>
        {
            var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2),
            };
            return new DeepgramTranscriptionClient(httpClient);
        });
        services.AddSingleton<MistralTranscriptionClient>(_ =>
        {
            var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2),
            };
            return new MistralTranscriptionClient(httpClient);
        });
        services.AddSingleton<DeepgramStreamingTranscriptionClient>();
        services.AddSingleton<MistralRealtimeTranscriptionClient>();
        services.AddSingleton<ITranscriptionClientFactory, TranscriptionClientFactory>();
        services.AddSingleton<IDictationController, DictationController>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<TranscriptionHistoryViewModel>();
        services.AddTransient<TranscriptionHistoryWindow>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<AppCoordinator>();

        return services.BuildServiceProvider();
    }

    private static bool DecideRedirection()
    {
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey("TimbreSingleInstance");

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += OnActivated;
            return false;
        }

        RedirectActivationTo(args, keyInstance);
        return true;
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        AppCoordinator.Current?.ShowMainWindowFromActivation();
    }

    private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
    {
        _redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null);

        Task.Run(() =>
        {
            keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(_redirectEventHandle);
        });

        const uint infinite = 0xFFFFFFFF;
        _ = CoWaitForMultipleObjects(0, infinite, 1, [_redirectEventHandle], out _);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, ulong nHandles, IntPtr[] pHandles, out uint dwIndex);
}
