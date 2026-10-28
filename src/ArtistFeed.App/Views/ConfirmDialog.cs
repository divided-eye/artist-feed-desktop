using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ArtistFeed.App.Views;

/// <summary>A small "are you sure?" window.</summary>
public sealed class ConfirmDialog : Window
{
    ConfirmDialog(string title, string message, string yes, string no, bool danger)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Something that can't be undone: the safe answer is the default, and "yes" is red.
        var yesButton = new Button { Content = yes, IsDefault = !danger, Classes = { danger ? "danger" : "primary" } };
        var noButton = new Button { Content = no, IsCancel = true, IsDefault = danger };
        yesButton.Click += (_, _) => Close(true);
        noButton.Click += (_, _) => Close(false);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { noButton, yesButton },
                },
            },
        };
        Services.Display.Attach(this);
    }

    public static async Task<bool> AskAsync(Window owner, string title, string message, string yes = "OK", string no = "Cancel", bool danger = false)
    {
        var dialog = new ConfirmDialog(title, message, yes, no, danger);
        return await dialog.ShowDialog<bool>(owner);
    }
}
