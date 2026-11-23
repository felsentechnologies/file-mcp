using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FileMCP.Core;

namespace FileMCP.Linux;

public partial class MainWindow : Window
{
    private const int MaxLogCharacters = 500_000;
    private readonly SettingsStore _settingsStore = new();
    private readonly SecretServiceCredentialStore _credentialStore = new();
    private readonly LocalMcpRuntime _runtime = new();
    private readonly Action _shutdown;
    private readonly string? _settingsLoadError;
    private FileMcpSettings _settings;
    private string _logBuffer = "";
    private string? _sessionApiKey;
    private bool _hasSavedApiKey;
    private bool _busy;
    private bool _quitting;
    private bool _backgroundNoticeShown;
    private bool _trayAvailable;

    /// <summary>Raised with (status text, tray connect label, connect enabled) whenever the runtime state changes.</summary>
    internal event Action<string, string, bool>? RuntimeStatusChanged;

    // Used by the XAML previewer only.
    public MainWindow() : this(() => { }) { }

    public MainWindow(Action shutdown)
    {
        InitializeComponent();
        _shutdown = shutdown;
        _settings = LoadSettingsSafely(out _settingsLoadError);
        ApplySettings(_settings);

        _runtime.Log += text => Dispatcher.UIThread.Post(() => AppendLog(text));
        _runtime.StateChanged += state => Dispatcher.UIThread.Post(() => UpdateRuntimeState(state));
        Closing += OnClosing;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            if (_settingsLoadError is not null) await ShowErrorAsync(_settingsLoadError);
        };
        UpdateRuntimeState(_runtime.State);
        TrayAvailable = false;
        _ = RefreshApiKeyStatusAsync();
    }

    /// <summary>Set once the StatusNotifier host check completes.</summary>
    internal bool TrayAvailable
    {
        get => _trayAvailable;
        set
        {
            _trayAvailable = value;
            CloseHintText.Text = value
                ? "Closing the window keeps FileMCP running in the system tray."
                : "Closing the window while connected keeps FileMCP running in the background; launch FileMCP again to reopen it.";
        }
    }

    private FileMcpSettings LoadSettingsSafely(out string? error)
    {
        error = null;
        try { return _settingsStore.Load(); }
        catch (Exception ex)
        {
            error = ex.Message;
            return new FileMcpSettings();
        }
    }

    private void ApplySettings(FileMcpSettings settings)
    {
        TunnelIdBox.Text = settings.TunnelId;
        ProfileBox.Text = settings.Profile;
        PortBox.Text = settings.Port.ToString();
        DirectoryBox.Text = settings.AllowedDirectory;
        HealthAddressBox.Text = settings.HealthAddress;
        GitNameBox.Text = settings.GitUserName;
        GitEmailBox.Text = settings.GitUserEmail;
        EnableCommandsCheckBox.IsChecked = settings.EnableCommands;
    }

    private static string Text(TextBox box) => (box.Text ?? "").Trim();

    private async Task RefreshApiKeyStatusAsync()
    {
        string? problem = null;
        try { _hasSavedApiKey = await Task.Run(() => _credentialStore.ReadApiKeyOrNull() is not null); }
        catch (Exception ex) { _hasSavedApiKey = false; problem = ex.Message; }

        ApiKeyStatusText.Text = _hasSavedApiKey
            ? "API key is saved in the Secret Service keyring"
            : _sessionApiKey is not null
                ? "API key is kept in memory for this session only"
                : problem is not null ? "Keyring unavailable — see Logs" : "No API key is saved";
        DeleteApiKeyButton.IsEnabled = _hasSavedApiKey || _sessionApiKey is not null;
        if (problem is not null) AppendLog("Secret Service: " + problem + "\n");
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e) => await ToggleConnectionAsync();

    internal async Task ToggleConnectionAsync()
    {
        var status = _runtime.State.Status;
        if (status is LocalMcpRuntimeStatus.Running or LocalMcpRuntimeStatus.Starting)
        {
            await Task.Run(_runtime.StopAsync);
            return;
        }
        if (status == LocalMcpRuntimeStatus.Stopping || _busy) return;

        _busy = true;
        ConnectButton.IsEnabled = false;
        try
        {
            if (!await ValidateConnectionAsync() || !await ValidateSettingsAsync()) return;
            var apiKey = await ResolveApiKeyAsync();
            if (apiKey is null) return;
            SaveAllSettings();
            var configuration = new LocalMcpConfiguration(
                Text(TunnelIdBox), apiKey, Text(ProfileBox), checked((ushort)_settings.Port),
                FileMcpPlatform.ExpandUserPath(Text(DirectoryBox)), Text(HealthAddressBox), Text(GitNameBox), Text(GitEmailBox),
                EnableCommandsCheckBox.IsChecked == true);
            await Task.Run(() => _runtime.StartAsync(configuration));
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
        finally
        {
            _busy = false;
            UpdateRuntimeState(_runtime.State, reportFailure: false);
        }
    }

    /// <summary>
    /// Saves a newly typed key in the keyring. If no Secret Service is usable, the key is kept in memory for this
    /// session only — it is never written to settings.json or any other file.
    /// </summary>
    private async Task<string?> ResolveApiKeyAsync()
    {
        var typed = Text(ApiKeyBox);
        if (typed.Length > 0)
        {
            await StoreTypedApiKeyAsync(typed);
            return typed;
        }
        if (_sessionApiKey is not null) return _sessionApiKey;
        try
        {
            return await Task.Run(_credentialStore.ReadApiKey);
        }
        catch (Exception ex)
        {
            MainTabs.SelectedItem = ConnectionTab;
            await ShowErrorAsync(ex.Message);
            return null;
        }
    }

    private async Task StoreTypedApiKeyAsync(string typed)
    {
        ApiKeyBox.Text = "";
        try
        {
            await Task.Run(() => _credentialStore.SaveApiKey(typed));
            _sessionApiKey = null;
            AppendLog("Runtime API key saved in the Secret Service keyring.\n");
        }
        catch (Exception ex)
        {
            _sessionApiKey = typed;
            AppendLog("WARNING: could not save the API key in the keyring; it is kept in memory for this session only.\n");
            await ShowErrorAsync("The API key could not be saved in the Secret Service keyring:\n\n" + ex.Message +
                "\n\nFileMCP will keep it in memory for this session only. It is never written to disk.");
        }
        await RefreshApiKeyStatusAsync();
    }

    private async void SaveConnection_Click(object? sender, RoutedEventArgs e)
    {
        if (!await ValidateConnectionAsync()) return;
        try
        {
            var typed = Text(ApiKeyBox);
            if (typed.Length > 0) await StoreTypedApiKeyAsync(typed);
            _settings.TunnelId = Text(TunnelIdBox);
            _settingsStore.Save(_settings);
            AppendLog("Connection settings saved.\n");
            if (_runtime.State.Status != LocalMcpRuntimeStatus.Stopped) AppendLog("Changes will take effect the next time the tunnel starts.\n");
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private async void SaveSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (!await ValidateSettingsAsync()) return;
        try
        {
            SaveSettingsFields();
            _settingsStore.Save(_settings);
            AppendLog($"Settings saved to {_settingsStore.FilePath}.\n");
            if (_runtime.State.Status != LocalMcpRuntimeStatus.Stopped) AppendLog("Changes will take effect the next time the tunnel starts.\n");
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private async void DeleteApiKey_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _sessionApiKey = null;
            ApiKeyBox.Text = "";
            await Task.Run(_credentialStore.DeleteApiKey);
            AppendLog("Runtime API key deleted.\n");
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
        await RefreshApiKeyStatusAsync();
    }

    private async void BrowseDirectory_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var current = FileMcpPlatform.ExpandUserPath(Text(DirectoryBox));
            var start = Directory.Exists(current) ? current : FileMcpPlatform.DocumentsDirectory;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose shared directory",
                AllowMultiple = false,
                SuggestedStartLocation = Directory.Exists(start) ? await StorageProvider.TryGetFolderFromPathAsync(start) : null,
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) DirectoryBox.Text = path;
        }
        catch (Exception ex) { await ShowErrorAsync("Could not open the folder picker: " + ex.Message); }
    }

    private async void Quit_Click(object? sender, RoutedEventArgs e) => await QuitAsync();

    private async Task<bool> ValidateConnectionAsync()
    {
        var tunnelId = Text(TunnelIdBox);
        string? error = null;
        if (tunnelId.Length == 0) error = "Enter a Tunnel ID in the Connection tab.";
        else if (!LocalMcpRuntime.IsValidTunnelId(tunnelId)) error = "Tunnel ID must match tunnel_<32 lowercase letters or digits>.";
        else if (Text(ApiKeyBox).Length == 0 && _sessionApiKey is null && !_hasSavedApiKey)
        {
            await RefreshApiKeyStatusAsync();
            if (!_hasSavedApiKey) error = "Enter a Runtime API key in the Connection tab.";
        }
        if (error is null) return true;
        MainTabs.SelectedItem = ConnectionTab;
        await ShowErrorAsync(error);
        return false;
    }

    private async Task<bool> ValidateSettingsAsync()
    {
        string? error = null;
        var advanced = true;
        if (Text(DirectoryBox).Length == 0) { error = "Choose a shared directory."; advanced = false; }
        else if (!LocalMcpRuntime.IsValidProfileName(Text(ProfileBox))) error = "Profile must start with a letter or number and contain only letters, numbers, '.', '_' or '-' (maximum 128 characters).";
        else if (!int.TryParse(Text(PortBox), out var port) || port is < 1 or > 65535) error = "MCP port must be between 1 and 65535.";
        else if (LocalMcpRuntime.NormalizeHealthAddress(Text(HealthAddressBox)) is null) error = "Health listener must use localhost, 127.0.0.1, or [::1] with a port from 0 to 65535.";
        if (error is null) return true;
        MainTabs.SelectedItem = SettingsTab;
        if (advanced) AdvancedExpander.IsExpanded = true;
        await ShowErrorAsync(error);
        return false;
    }

    private void SaveAllSettings()
    {
        _settings.TunnelId = Text(TunnelIdBox);
        SaveSettingsFields();
        _settingsStore.Save(_settings);
    }

    private void SaveSettingsFields()
    {
        _settings.Profile = Text(ProfileBox);
        _settings.Port = int.Parse(Text(PortBox));
        _settings.AllowedDirectory = Text(DirectoryBox);
        _settings.HealthAddress = Text(HealthAddressBox);
        _settings.GitUserName = Text(GitNameBox);
        _settings.GitUserEmail = Text(GitEmailBox);
        _settings.EnableCommands = EnableCommandsCheckBox.IsChecked == true;
    }

    private void UpdateRuntimeState(LocalMcpRuntimeState state) => UpdateRuntimeState(state, reportFailure: true);

    private void UpdateRuntimeState(LocalMcpRuntimeState state, bool reportFailure)
    {
        (string label, bool enabled, string status, IBrush color) = state.Status switch
        {
            LocalMcpRuntimeStatus.Starting => ("Connecting…", false, "Connecting…", Brushes.Orange),
            LocalMcpRuntimeStatus.Running => ("Disconnect", true, "Connected", Brushes.LimeGreen),
            LocalMcpRuntimeStatus.Stopping => ("Disconnecting…", false, "Disconnecting…", Brushes.Orange),
            LocalMcpRuntimeStatus.Failed => ("Connect", true, "Failed", Brushes.IndianRed),
            _ => ("Connect", true, "Disconnected", Brushes.Gray),
        };
        if (_busy) { label = "Connecting…"; enabled = false; }
        ConnectButton.Content = label;
        ConnectButton.IsEnabled = enabled;
        StatusText.Text = status;
        StatusDot.Fill = color;
        RuntimeStatusChanged?.Invoke(status, label, enabled);
        if (reportFailure && state.Status == LocalMcpRuntimeStatus.Failed && !string.IsNullOrEmpty(state.Error)) _ = ShowErrorAsync(state.Error);
    }

    private void AppendLog(string text)
    {
        _logBuffer += text;
        if (_logBuffer.Length > MaxLogCharacters)
            _logBuffer = "[...older log truncated...]\n" + _logBuffer[^MaxLogCharacters..];
        LogBox.Text = _logBuffer;
        LogBox.CaretIndex = _logBuffer.Length;
    }

    private Task ShowErrorAsync(string message) => MessageDialog.ShowAsync(this, "FileMCP", message);

    internal async Task ShowAboutAsync()
    {
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        await MessageDialog.ShowAsync(IsVisible ? this : null, "About FileMCP",
            $"FileMCP {FileMcpConstants.Version} (Linux {architecture})\n\n" +
            "Native Linux MCP bridge for controlled local file, Git, and optional command access.\n\n" +
            $"Settings: {_settingsStore.FilePath}\n" +
            $"Data: {FileMcpPlatform.DataDirectory}\n" +
            "Runtime API key: Secret Service keyring");
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting) return;
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            ShutdownForSystemSession();
            return;
        }
        e.Cancel = true;
        var active = _runtime.State.Status is not (LocalMcpRuntimeStatus.Stopped or LocalMcpRuntimeStatus.Failed);
        if (!TrayAvailable && !active)
        {
            // Nothing is shared and there is no tray to come back through: closing means quitting.
            _ = QuitAsync();
            return;
        }
        Hide();
        if (!_backgroundNoticeShown)
        {
            _backgroundNoticeShown = true;
            AppendLog(TrayAvailable
                ? "FileMCP keeps running in the system tray. Use the tray menu to reopen or quit.\n"
                : "FileMCP keeps running in the background. Launch FileMCP again to reopen this window.\n");
        }
    }

    internal void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    internal void ShutdownForSystemSession()
    {
        if (_quitting) return;
        _quitting = true;
        try { _runtime.ShutdownAsync().GetAwaiter().GetResult(); } catch { }
    }

    internal async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        try { await Task.Run(_runtime.ShutdownAsync); }
        catch { }
        finally
        {
            try { await _runtime.DisposeAsync(); } catch { }
            _shutdown();
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        if (e.Key == Key.W) { Close(); e.Handled = true; }
        else if (e.Key == Key.OemComma) { MainTabs.SelectedItem = SettingsTab; ShowFromTray(); e.Handled = true; }
        else if (e.Key == Key.Q) { _ = QuitAsync(); e.Handled = true; }
    }
}
