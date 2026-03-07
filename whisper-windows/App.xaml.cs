using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace whisper_windows;

public partial class App : Application
{
    public App(IServiceProvider services)
    {
        Services = services;
        InitializeComponent();
    }

    public IServiceProvider Services { get; }
}
