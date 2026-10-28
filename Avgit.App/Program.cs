using System;
using Avalonia;
using Velopack;

namespace Avgit.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update hooks and exits early when invoked for one.
        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Avalonia.Win32PlatformOptions { })
            .LogToTrace();
}
