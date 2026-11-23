using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FileMCP.Core;

/// <summary>
/// Platform conventions shared by the Windows and Linux front ends. Windows keeps the historical
/// %LOCALAPPDATA%\FileMCP layout and case-insensitive path rules; Linux follows the XDG base
/// directory specification and compares paths case-sensitively.
/// </summary>
public static class FileMcpPlatform
{
    public const string LinuxApplicationId = "filemcp";

    [SupportedOSPlatformGuard("windows")]
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>Short platform tag used in version strings and diagnostics.</summary>
    public static string Name => IsWindows ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unix";

    /// <summary>Path comparison matching the platform's default filesystem semantics.</summary>
    public static StringComparison PathComparison => IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer PathComparer => IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Environment variable names are case-insensitive only on Windows.</summary>
    public static StringComparer EnvironmentVariableComparer => IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Returns a snapshot of the current process environment using the platform's key comparer.</summary>
    public static Dictionary<string, string> CurrentEnvironment()
    {
        var result = new Dictionary<string, string>(EnvironmentVariableComparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.Length > 0) result[key] = entry.Value as string ?? "";
        }
        return result;
    }

    /// <summary>Appends <c>.exe</c> on Windows.</summary>
    public static string ExecutableFileName(string baseName) => IsWindows ? baseName + ".exe" : baseName;

    /// <summary>Per-user application state: tunnel profiles and Git safe-mode resources.</summary>
    public static string DataDirectory => IsWindows
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileMCP")
        : Path.Combine(XdgDirectory("XDG_DATA_HOME", ".local/share"), LinuxApplicationId);

    /// <summary>Per-user settings (settings.json).</summary>
    public static string ConfigDirectory => IsWindows
        ? DataDirectory
        : Path.Combine(XdgDirectory("XDG_CONFIG_HOME", ".config"), LinuxApplicationId);

    /// <summary>Per-user, per-boot runtime state such as profile locks and the single-instance socket.</summary>
    public static string RuntimeDirectory
    {
        get
        {
            if (IsWindows) return Path.Combine(Path.GetTempPath(), "FileMCP");
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return !string.IsNullOrWhiteSpace(runtime) && Path.IsPathFullyQualified(runtime) && Directory.Exists(runtime)
                ? Path.Combine(runtime, LinuxApplicationId)
                : Path.Combine(DataDirectory, "run");
        }
    }

    /// <summary>Default shared workspace: Documents/FileMCP (localized XDG documents directory on Linux).</summary>
    public static string DefaultSharedDirectory => Path.Combine(DocumentsDirectory, "FileMCP");

    public static string DocumentsDirectory
    {
        get
        {
            if (IsWindows) return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var home = HomeDirectory;
            return XdgUserDirectory("XDG_DOCUMENTS_DIR", home) ?? Path.Combine(home, "Documents");
        }
    }

    public static string HomeDirectory
    {
        get
        {
            var home = IsWindows ? null : Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home;
        }
    }

    /// <summary>Expands a leading <c>~</c> on Unix and <c>%VAR%</c> references on every platform.</summary>
    public static string ExpandUserPath(string path)
    {
        var value = path.Trim();
        if (!IsWindows && (value == "~" || value.StartsWith("~/", StringComparison.Ordinal)))
            value = HomeDirectory + value[1..];
        return Environment.ExpandEnvironmentVariables(value);
    }

    /// <summary>Creates a directory readable only by the current user (0700 on Unix).</summary>
    public static void EnsurePrivateDirectory(string path)
    {
        if (IsWindows)
        {
            Directory.CreateDirectory(path);
            return;
        }
        const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(path, privateMode);
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null) throw new FileMcpException($"Refusing to use symbolic link as a private directory: {path}");
        if (info.UnixFileMode != privateMode) File.SetUnixFileMode(path, privateMode);
    }

    /// <summary>
    /// Replaces <paramref name="destination"/> with <paramref name="source"/> atomically. On Unix the
    /// destination's permission bits are carried over so edits do not silently drop e.g. the executable bit.
    /// </summary>
    internal static void ReplaceFile(string source, string destination, bool destinationMustExist)
    {
        if (!IsWindows)
        {
            try { File.SetUnixFileMode(source, File.GetUnixFileMode(destination)); }
            catch (FileNotFoundException) { if (destinationMustExist) throw; }
            catch (DirectoryNotFoundException) { if (destinationMustExist) throw; }
        }
        if (destinationMustExist) File.Replace(source, destination, null);
        else File.Move(source, destination, true);
    }

    /// <summary>Resolves an executable name the same way <c>execvp</c> would, using the current PATH.</summary>
    internal static string ResolveExecutable(string executable)
    {
        if (executable.Length == 0 || Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) ||
            (IsWindows && executable.Contains(Path.AltDirectorySeparatorChar)))
            return executable;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, executable);
            if (IsExecutableFile(candidate)) return candidate;
        }
        return executable;
    }

    internal static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (IsWindows) return true;
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Locates a well-known system tool in the standard Unix binary directories.</summary>
    internal static string? FindSystemTool(string name)
    {
        foreach (var directory in new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin", "/usr/local/bin" })
        {
            var candidate = Path.Combine(directory, name);
            if (IsExecutableFile(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Canonical path with every symbolic link resolved (Unix realpath(3)). The path must exist.</summary>
    internal static string RealPath(string path)
    {
        if (IsWindows) throw new PlatformNotSupportedException();
        var resolved = UnixNative.RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"Could not resolve path: {path}: {new Win32Exception(error).Message}");
        }
        try { return Marshal.PtrToStringUTF8(resolved) ?? throw new IOException($"Could not resolve path: {path}"); }
        finally { UnixNative.Free(resolved); }
    }

    private static string XdgDirectory(string variable, string homeRelativeFallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
            ? value
            : Path.Combine(HomeDirectory, homeRelativeFallback);
    }

    /// <summary>Reads a localized user directory (for example "~/Documentos") from user-dirs.dirs.</summary>
    private static string? XdgUserDirectory(string key, string home)
    {
        try
        {
            var file = Path.Combine(XdgDirectory("XDG_CONFIG_HOME", ".config"), "user-dirs.dirs");
            if (!File.Exists(file)) return null;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (!line.StartsWith(key + "=", StringComparison.Ordinal)) continue;
                var value = line[(key.Length + 1)..].Trim().Trim('"');
                if (value.StartsWith("$HOME", StringComparison.Ordinal)) value = home + value["$HOME".Length..];
                if (!Path.IsPathFullyQualified(value)) return null;
                var full = Path.GetFullPath(value);
                // xdg-user-dirs points disabled entries at $HOME itself; never share the whole home directory.
                return string.Equals(full.TrimEnd('/'), home.TrimEnd('/'), StringComparison.Ordinal) ? null : full;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}

internal static class UnixNative
{
    internal const int SIGTERM = 15;
    internal const int SIGKILL = 9;

    // CoreCLR maps "libc" to the platform C library (libc.so.6 on glibc Linux).
    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    internal static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "free")]
    internal static extern void Free(IntPtr pointer);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    internal static extern int Kill(int pid, int signal);
}
