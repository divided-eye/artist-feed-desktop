using Avalonia.Controls;
using Avalonia.Interactivity;
using ArtistFeed.App.ViewModels;

namespace ArtistFeed.App.Views;

public partial class SettingsWindow : Window
{
    bool _closing;

    public SettingsWindow() => InitializeComponent();

    void CloseClicked(object? sender, RoutedEventArgs e) => Close();

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closing || DataContext is not SettingsViewModel { Dirty: true }) return;
        e.Cancel = true;
        if (await ConfirmDialog.AskAsync(this, "Unsaved changes", "Close without saving? Your changes in Settings will be lost.", "Close without saving"))
        {
            _closing = true;
            Close();
        }
    }
}
