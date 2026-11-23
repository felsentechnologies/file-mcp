using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileMCP.Core;

// The tunnel polls the control plane, so idle system sleep (e.g. a locked screen) takes it
// offline. A power request keeps the system awake while connected; the display may still sleep.
// Unlike SetThreadExecutionState it is tied to a handle, not to the calling thread.
// On Linux the equivalent is a systemd-logind "idle:sleep" block inhibitor held by a
// systemd-inhibit child. The child waits on a stdin pipe owned by FileMCP, so the inhibitor is
// released automatically if FileMCP exits or crashes. Lid-close suspend is unaffected by default
// (logind LidSwitchIgnoreInhibited=yes).
internal sealed class SystemSleepGuard : IDisposable
{
    private SafeFileHandle? _request;
    private Process? _inhibitor;

    public bool IsActive => _request is not null || _inhibitor is not null;

    public void Acquire()
    {
        if (IsActive) return;
        if (FileMcpPlatform.IsWindows) AcquireWindows();
        else AcquireLogind();
    }

    public void Release()
    {
        var request = _request;
        if (request is not null)
        {
            _request = null;
            NativeMethods.PowerClearRequest(request, NativeMethods.PowerRequestSystemRequired);
            request.Dispose();
        }

        var inhibitor = _inhibitor;
        if (inhibitor is not null)
        {
            _inhibitor = null;
            try { inhibitor.StandardInput.Close(); } catch { }
            try { if (!inhibitor.WaitForExit(2000)) inhibitor.Kill(entireProcessTree: true); } catch { }
            inhibitor.Dispose();
        }
    }

    public void Dispose() => Release();

    private void AcquireWindows()
    {
        var context = new NativeMethods.ReasonContext
        {
            Version = NativeMethods.PowerRequestContextVersion,
            Flags = NativeMethods.PowerRequestContextSimpleString,
            SimpleReasonString = "FileMCP tunnel is connected",
        };
        var request = NativeMethods.PowerCreateRequest(ref context);
        if (request.IsInvalid) { request.Dispose(); return; }
        if (!NativeMethods.PowerSetRequest(request, NativeMethods.PowerRequestSystemRequired)) { request.Dispose(); return; }
        _request = request;
    }

    private void AcquireLogind()
    {
        // Best effort, like the Windows power request: no logind (containers, non-systemd distros) means no guard.
        var inhibit = FileMcpPlatform.FindSystemTool("systemd-inhibit");
        var cat = FileMcpPlatform.FindSystemTool("cat");
        if (inhibit is null || cat is null) return;
        var startInfo = new ProcessStartInfo
        {
            FileName = inhibit,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "--what=idle:sleep", "--who=FileMCP", "--why=FileMCP tunnel is connected", "--mode=block", cat })
            startInfo.ArgumentList.Add(argument);
        try
        {
            var process = Process.Start(startInfo);
            if (process is null) return;
            if (process.WaitForExit(150))
            {
                // systemd-inhibit failed immediately (no logind, or inhibition denied by polkit).
                process.Dispose();
                return;
            }
            _inhibitor = process;
        }
        catch
        {
            // The guard is advisory; never fail the tunnel because sleep prevention is unavailable.
        }
    }

    private static class NativeMethods
    {
        internal const uint PowerRequestContextVersion = 0;
        internal const uint PowerRequestContextSimpleString = 0x1;
        internal const int PowerRequestSystemRequired = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ReasonContext
        {
            public uint Version;
            public uint Flags;
            [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PowerSetRequest(SafeFileHandle powerRequest, int requestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PowerClearRequest(SafeFileHandle powerRequest, int requestType);
    }
}
