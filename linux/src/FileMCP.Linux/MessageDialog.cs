using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace FileMCP.Linux;

/// <summary>Minimal modal message box (Avalonia has no built-in one).</summary>
internal sealed class MessageDialog : Window
{
    private MessageDialog(string title, string message)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://FileMCP/Assets/filemcp-64.png")));

        var ok = new Button { Content = "OK", MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                ok,
            },
        };
        Opened += (_, _) => ok.Focus();
    }

    public static async Task ShowAsync(Window? owner, string title, string message)
    {
        var dialog = new MessageDialog(title, message);
        if (owner is { IsVisible: true })
        {
            await dialog.ShowDialog(owner);
            return;
        }
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        dialog.Show();
        await closed.Task;
    }
}
