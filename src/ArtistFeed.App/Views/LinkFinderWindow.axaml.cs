using Avalonia.Controls;
using Avalonia.Interactivity;
using ArtistFeed.App.Services;
using ArtistFeed.App.ViewModels;

namespace ArtistFeed.App.Views;

public partial class LinkFinderWindow : Window
{
    static readonly Dictionary<string, string> SavedHeaders = new()
    {
        ["artist"] = "Artist (tidied)", ["title"] = "Title (tidied)", ["list_title"] = "Your spelling", ["link"] = "Link",
        ["year"] = "Year", ["result"] = "Result", ["saved"] = "Saved",
    };

    public LinkFinderWindow()
    {
        InitializeComponent();
        Display.Attach(this);
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    LinkFinderViewModel? Vm => DataContext as LinkFinderViewModel;

    void CloseClicked(object? sender, RoutedEventArgs e) => Close();

    void OpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string url } && url.StartsWith("http")) Desktop.Open(url);
    }

    void ResultsLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not LookupRowViewModel row) return;
        e.Row.Classes.Set("review", row.HasReview);
        row.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(LookupRowViewModel.Review) && e.Row.DataContext == row) e.Row.Classes.Set("review", row.HasReview);
        };
    }

    void SavedLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is SavedRowViewModel row) e.Row.Classes.Set("review", row.HasReview);
    }

    /// <summary>Sorted by Link Finder across every saved row, not just the page shown.</summary>
    void SavedSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (Vm == null || e.Column.Tag is not string key) return;
        Vm.SortSaved(key);
        foreach (var col in Saved.Columns)
            if (col.Tag is string k && SavedHeaders.TryGetValue(k, out var text))
                col.Header = k == Vm.SavedSort ? text + (Vm.SavedDesc ? " ▼" : " ▲") : text;
    }
}
