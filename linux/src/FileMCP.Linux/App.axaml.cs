using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace FileMCP.Linux;

public partial class App : Application
{
    internal static SingleInstance? SingleInstance { get; set; }
    internal static bool StartHidden { get; set; }

    private readonly List<PosixSignalRegistration> _signals = [];
    private MainWindow? _window;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayConnectItem;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(() => desktop.Shutdown());
            _window = window;
            desktop.MainWindow = window;
            CreateTrayIcon(window);
            window.RuntimeStatusChanged += UpdateTray;

            // Session end (XSMP) and service-manager signals: stop the tunnel before the process goes away.
            desktop.ShutdownRequested += (_, _) => window.ShutdownForSystemSession();
            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
            {
                _signals.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    Dispatcher.UIThread.Post(() => _ = window.QuitAsync());
                }));
            }
            desktop.Exit += (_, _) =>
            {
                foreach (var registration in _signals) registration.Dispose();
                _trayIcon?.Dispose();
            };

            SingleInstance?.OnActivationRequested(() => Dispatcher.UIThread.Post(window.ShowFromTray));
            if (!StartHidden) window.Show();
            _ = DetectTrayAsync(window);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task DetectTrayAsync(MainWindow window)
    {
        var available = await DesktopIntegration.HasStatusNotifierHostAsync();
        Dispatcher.UIThread.Post(() =>
        {
            window.TrayAvailable = available;
            // --hidden without a tray would be invisible and unreachable except via relaunch; show the window instead.
            if (StartHidden && !available) window.ShowFromTray();
        });
    }

    private void CreateTrayIcon(MainWindow window)
    {
        var open = new NativeMenuItem("Open FileMCP");
        open.Click += (_, _) => window.ShowFromTray();
        _trayConnectItem = new NativeMenuItem("Connect");
        _trayConnectItem.Click += (_, _) => _ = window.ToggleConnectionAsync();
        var about = new NativeMenuItem("About FileMCP");
        about.Click += (_, _) => _ = window.ShowAboutAsync();
        var quit = new NativeMenuItem("Quit FileMCP");
        quit.Click += (_, _) => _ = window.QuitAsync();

        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(_trayConnectItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(about);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://FileMCP/Assets/filemcp-64.png"))),
            ToolTipText = "FileMCP — Disconnected",
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => window.ShowFromTray();
        TrayIcon.SetIcons(this, [_trayIcon]);
    }

    private void UpdateTray(string statusText, string connectLabel, bool connectEnabled)
    {
        if (_trayIcon is not null) _trayIcon.ToolTipText = "FileMCP — " + statusText;
        if (_trayConnectItem is not null)
        {
            _trayConnectItem.Header = connectLabel;
            _trayConnectItem.IsEnabled = connectEnabled;
        }
    }
}
