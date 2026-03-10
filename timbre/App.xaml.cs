using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using timbre.Services;

namespace timbre;

public partial class App : Application
{
    public App(IServiceProvider services)
    {
        Services = services;
        UnhandledException += OnUnhandledException;
        InitializeComponent();
    }

    public IServiceProvider Services { get; }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (e.Exception is Exception exception)
        {
            DiagnosticsLogger.Error($"WinUI unhandled exception. Message='{e.Message}'", exception);
            return;
        }

        DiagnosticsLogger.Info($"WinUI unhandled exception without managed exception. Message='{e.Message}'");
    }
}
