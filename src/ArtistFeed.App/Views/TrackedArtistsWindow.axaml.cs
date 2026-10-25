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

    static readonly Dictionary<string, string> Headers = new()
    {
        ["name"] = "Artist", ["bandcamp"] = "Bandcamp", ["soundcloud"] = "SoundCloud", ["youtube"] = "YouTube", ["state"] = "Status",
    };

    /// <summary>Click a heading to sort by it; again to reverse; a third time to go back to name order.</summary>
    void TableSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (Vm == null || e.Column.Tag is not string key || !Headers.ContainsKey(key)) return;
        Vm.SortBy(key);
        foreach (var col in Table.Columns)
            if (col.Tag is string k && Headers.TryGetValue(k, out var text))
                col.Header = k == Vm.SortKey ? text + (Vm.SortDesc ? " ▼" : " ▲") : text;
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
