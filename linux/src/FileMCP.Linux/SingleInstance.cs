using System.Net.Sockets;
using System.Text;
using FileMCP.Core;

namespace FileMCP.Linux;

/// <summary>
/// One FileMCP window per user session. The first instance holds an flock on
/// $XDG_RUNTIME_DIR/filemcp/app.lock and listens on a Unix socket in the same private (0700)
/// directory; later launches (for example from the application menu when no tray is available)
/// ask it to show its window and exit.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private static readonly byte[] ShowCommand = "show\n"u8.ToArray();
    private readonly FileStream _lock;
    private readonly Socket _listener;
    private readonly string _socketPath;
    private readonly CancellationTokenSource _stop = new();
    private Action? _activationRequested;
    private bool _pendingActivation;
    private readonly object _gate = new();

    private SingleInstance(FileStream lockFile, Socket listener, string socketPath)
    {
        _lock = lockFile;
        _listener = listener;
        _socketPath = socketPath;
        _ = AcceptLoopAsync();
    }

    private static string Directory => FileMcpPlatform.RuntimeDirectory;
    private static string SocketPath => Path.Combine(Directory, "app.sock");

    /// <summary>Returns null when another instance already owns the session.</summary>
    public static SingleInstance? TryAcquire()
    {
        FileMcpPlatform.EnsurePrivateDirectory(Directory);
        FileStream lockFile;
        try
        {
            lockFile = new FileStream(Path.Combine(Directory, "app.lock"), new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None, // flock(LOCK_EX | LOCK_NB) on Unix.
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }
        catch (IOException)
        {
            return null;
        }

        var socketPath = SocketPath;
        try { File.Delete(socketPath); } catch (IOException) { }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(4);
        }
        catch
        {
            listener.Dispose();
            lockFile.Dispose();
            throw;
        }
        return new SingleInstance(lockFile, listener, socketPath);
    }

    /// <summary>Asks the running instance to show its window.</summary>
    public static bool SignalExisting()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
                socket.Send(ShowCommand);
                return true;
            }
            catch (SocketException)
            {
                // The first instance may still be starting up.
                Thread.Sleep(100);
            }
        }
        return false;
    }

    /// <summary>Registers the UI callback; an activation that arrived before the window existed is replayed.</summary>
    public void OnActivationRequested(Action callback)
    {
        bool replay;
        lock (_gate)
        {
            _activationRequested = callback;
            replay = _pendingActivation;
            _pendingActivation = false;
        }
        if (replay) callback();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener.AcceptAsync(_stop.Token).ConfigureAwait(false); }
            catch { return; }
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(Socket client)
    {
        using (client)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var buffer = new byte[16];
                var read = await client.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
                if (!Encoding.ASCII.GetString(buffer, 0, read).StartsWith("show", StringComparison.Ordinal)) return;
                Action? callback;
                lock (_gate)
                {
                    callback = _activationRequested;
                    if (callback is null) _pendingActivation = true;
                }
                callback?.Invoke();
            }
            catch
            {
                // Ignore malformed or slow clients.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Dispose(); } catch { }
        try { File.Delete(_socketPath); } catch { }
        _lock.Dispose();
        _stop.Dispose();
    }
}
