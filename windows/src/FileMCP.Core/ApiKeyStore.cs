using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace FileMCP.Core;

/// <summary>Stores the tunnel runtime API key in the operating system credential store, never in settings.json.</summary>
public interface IApiKeyStore
{
    /// <summary>Human-readable store name, e.g. "Windows Credential Manager".</summary>
    string StoreName { get; }
    bool HasSavedApiKey { get; }
    string ReadApiKey();
    void SaveApiKey(string value);
    void DeleteApiKey();
}

public static class ApiKeyStores
{
    /// <summary>Returns the credential store for the current platform.</summary>
    public static IApiKeyStore CreateDefault() => FileMcpPlatform.IsWindows
        ? new WindowsCredentialStore()
        : new SecretServiceCredentialStore();
}

/// <summary>
/// Linux credential store backed by the freedesktop.org Secret Service (GNOME Keyring, KWallet,
/// KeePassXC, ...) through libsecret's <c>secret-tool</c>. The secret is passed through stdin/stdout
/// pipes only and is never written to disk by FileMCP. There is deliberately no plaintext fallback.
/// </summary>
public sealed class SecretServiceCredentialStore : IApiKeyStore
{
    public const string DefaultService = "FileMCP";
    public const string DefaultAccount = "runtime-api-key";
    public const string ToolOverrideKey = "FILEMCP_SECRET_TOOL";
    private const int TimeoutMilliseconds = 120_000;
    private readonly string _service;
    private readonly string _account;
    private readonly string? _toolOverride;

    public SecretServiceCredentialStore(string? service = null, string? account = null, string? secretToolPath = null)
    {
        _service = string.IsNullOrWhiteSpace(service) ? DefaultService : service;
        _account = string.IsNullOrWhiteSpace(account) ? DefaultAccount : account;
        _toolOverride = secretToolPath;
    }

    public string StoreName => "the Secret Service keyring";

    public bool HasSavedApiKey
    {
        get
        {
            try { return !string.IsNullOrEmpty(ReadApiKeyOrNull()); }
            catch { return false; }
        }
    }

    public string ReadApiKey() => ReadApiKeyOrNull() ?? throw new FileMcpException("No API key is saved.");

    public void SaveApiKey(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (value.Contains('\0') || value.Contains('\n') || value.Contains('\r'))
            throw new FileMcpException("The API key contains characters that cannot be stored.");
        var result = Run(["store", "--label=FileMCP runtime API key", "service", _service, "account", _account], value);
        if (result.ExitCode != 0) throw StoreError("save the API key", result.Stderr);
    }

    public void DeleteApiKey()
    {
        var result = Run(["clear", "service", _service, "account", _account], null);
        // secret-tool clear exits non-zero when nothing matched; only report real failures.
        if (result.ExitCode != 0 && result.Stderr.Trim().Length > 0) throw StoreError("delete the API key", result.Stderr);
    }

    /// <summary>Returns null when no key is stored; throws when the Secret Service cannot be reached.</summary>
    public string? ReadApiKeyOrNull()
    {
        var result = Run(["lookup", "service", _service, "account", _account], null);
        if (result.ExitCode != 0)
        {
            if (result.Stderr.Trim().Length == 0) return null; // No matching item.
            throw StoreError("read the API key", result.Stderr);
        }
        var value = result.Stdout.TrimEnd('\r', '\n');
        return value.Length == 0 ? null : value;
    }

    private (int ExitCode, string Stdout, string Stderr) Run(IReadOnlyList<string> arguments, string? stdin)
    {
        if (FileMcpPlatform.IsWindows) throw new PlatformNotSupportedException("The Secret Service store is only available on Linux.");
        var tool = LocateSecretTool();
        var startInfo = new ProcessStartInfo
        {
            FileName = tool,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new FileMcpException("Could not start secret-tool.");
        }
        catch (Win32Exception ex)
        {
            throw new FileMcpException("Could not start secret-tool: " + ex.Message);
        }
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            if (stdin is not null) process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }
        catch (IOException) { }
        if (!process.WaitForExit(TimeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new FileMcpException("Timed out waiting for the Secret Service. Unlock your keyring and try again.");
        }
        process.WaitForExit();
        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private string LocateSecretTool()
    {
        var configured = _toolOverride ?? Environment.GetEnvironmentVariable(ToolOverrideKey);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (FileMcpPlatform.IsExecutableFile(configured)) return configured;
            throw new FileMcpException($"secret-tool override is not executable: {configured}");
        }
        return FileMcpPlatform.FindSystemTool("secret-tool")
            ?? throw new FileMcpException(
                "secret-tool was not found. Install libsecret-tools (Debian/Ubuntu) or libsecret (Fedora/RHEL/openSUSE) " +
                "and make sure a Secret Service keyring such as GNOME Keyring or KWallet is running.");
    }

    private FileMcpException StoreError(string operation, string stderr)
    {
        var detail = stderr.Trim();
        if (detail.Length > 500) detail = detail[..500];
        var hint = detail.Contains("org.freedesktop.secrets", StringComparison.Ordinal) || detail.Contains("not provided by any .service", StringComparison.Ordinal)
            ? " No Secret Service provider is running; start GNOME Keyring, KWallet or another Secret Service provider."
            : "";
        return new FileMcpException($"Could not {operation} in {StoreName}: {(detail.Length == 0 ? "secret-tool failed" : detail)}.{hint}");
    }
}
