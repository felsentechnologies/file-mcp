using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using FileMCP.Core;

[assembly: SupportedOSPlatform("linux")]

namespace FileMCP.Linux;

internal static class Program
{
    private const string Usage = """
        Usage: filemcp [OPTION]

        Native Linux desktop client that shares a local workspace with ChatGPT through
        OpenAI Secure MCP Tunnel. Running it again while it is open shows the existing window.

          --hidden     start in the background (system tray) without showing the window
          --version    print the version and exit
          --help       show this help and exit
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        var hidden = false;
        foreach (var argument in args)
        {
            switch (argument)
            {
                case "--version" or "-V":
                    Console.WriteLine($"FileMCP {FileMcpConstants.Version} ({FileMcpPlatform.Name}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})");
                    return 0;
                case "--help" or "-h":
                    Console.WriteLine(Usage);
                    return 0;
                case "--hidden":
                    hidden = true;
                    break;
                default:
                    Console.Error.WriteLine($"filemcp: unknown option '{argument}'\n\n{Usage}");
                    return 2;
            }
        }

        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("This FileMCP build supports Linux only.");
            return 1;
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Console.Error.WriteLine("FileMCP needs a graphical session with an X11 display (Xorg or XWayland); DISPLAY is not set.");
            return 1;
        }

        SingleInstance? instance;
        try { instance = SingleInstance.TryAcquire(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FileMCP could not create its runtime directory: " + ex.Message);
            return 1;
        }
        if (instance is null)
        {
            if (hidden) return 0;
            if (SingleInstance.SignalExisting()) return 0;
            Console.Error.WriteLine("Another FileMCP instance is starting or not responding.");
            return 1;
        }

        using (instance)
        {
            App.SingleInstance = instance;
            App.StartHidden = hidden;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime([], Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // WM_CLASS must match StartupWMClass in the .desktop file so docks group the window with its launcher.
            .With(new X11PlatformOptions { WmClass = "filemcp" })
            .LogToTrace();
}
