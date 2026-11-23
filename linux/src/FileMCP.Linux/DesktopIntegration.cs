using Tmds.DBus.Protocol;

namespace FileMCP.Linux;

/// <summary>Freedesktop session helpers.</summary>
internal static class DesktopIntegration
{
    /// <summary>
    /// Avalonia's Linux tray icon is a StatusNotifierItem. It is only visible when a StatusNotifierWatcher
    /// is running (KDE Plasma, Cinnamon, XFCE, MATE, LXQt, GNOME with the AppIndicator extension, ...).
    /// Without one, hiding the window would leave no visible way back except relaunching FileMCP.
    /// </summary>
    public static async Task<bool> HasStatusNotifierHostAsync()
    {
        try
        {
            if (Address.Session is not { Length: > 0 } address) return false;
            using var connection = new Connection(address);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await connection.ConnectAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
            return await connection.CallMethodAsync(
                CreateNameHasOwnerMessage(connection, "org.kde.StatusNotifierWatcher"),
                static (Message message, object? _) => message.GetBodyReader().ReadBool(),
                null).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static MessageBuffer CreateNameHasOwnerMessage(Connection connection, string name)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(
                destination: "org.freedesktop.DBus",
                path: "/org/freedesktop/DBus",
                @interface: "org.freedesktop.DBus",
                member: "NameHasOwner",
                signature: "s");
            writer.WriteString(name);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }
}
