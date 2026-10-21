using Avalonia.Controls;
using Avalonia.Interactivity;
using ArtistFeed.App.ViewModels;

namespace ArtistFeed.App.Views;

public partial class TrackedArtistsWindow : Window
{
    bool _closing;

    public TrackedArtistsWindow()
    {
        InitializeComponent();
        Services.Display.Attach(this);
        DataContextChanged += (_, _) => ShowColumns();
        Activated += (_, _) => ShowColumns();
    }

    TrackedArtistsViewModel? Vm => DataContext as TrackedArtistsViewModel;

    /// <summary>A column for each site switched on.</summary>
    void ShowColumns()
    {
        if (Vm == null) return;
        foreach (var col in Table.Columns)
        {
            col.IsVisible = col.Tag switch
            {
                "bandcamp" => Vm.BandcampOn,
                "soundcloud" => Vm.SoundCloudOn,
                "youtube" or "youtube_lookup" => Vm.YouTubeOn,
                _ => true,
            };
        }
    }

    void CloseClicked(object? sender, RoutedEventArgs e) => Close();

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closing || Vm is not { Dirty: true }) return;
        e.Cancel = true;
        if (await ConfirmDialog.AskAsync(this, "Unsaved changes", "Close without saving? Your changes to Tracked Artists will be lost.", "Close without saving"))
        {
            _closing = true;
            Close();
        }
    }
}
