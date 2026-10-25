using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ArtistFeed.App.Services;
using ArtistFeed.App.ViewModels;

namespace ArtistFeed.App.Views;

public partial class MainWindow : Window, IUi
{
    NativeWebView? _web;
    PlayItem? _playing;
    TrackedArtistsWindow? _artists;
    LinkFinderWindow? _finder;

    /// <summary>The window a dialog belongs to: whichever of this app's windows is in front.</summary>
    Window Front => OwnedWindows.FirstOrDefault(w => w.IsActive && w.IsVisible)
                    ?? (_artists is { IsVisible: true } ? _artists : _finder is { IsVisible: true } ? _finder : this);

    public MainWindow()
    {
        InitializeComponent();
        Display.Attach(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Ui = this;
                ShowSortMark(vm);
                ApplyColumns(vm);
                vm.ColumnsChanged += () => ApplyColumns(vm);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(MainViewModel.SortKey) or nameof(MainViewModel.SortAscending)) ShowSortMark(vm);
                };
            }
        };
        List.UnloadingRow += (_, e) =>
        {
            if (e.Row.Tag is TrackRowViewModel old) old.PropertyChanged -= RowChanged;
            e.Row.Tag = null;
        };
    }

    MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>The ⓘ beside the status line opens its details on hover as well as on click.</summary>
    void StatusInfoEntered(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (sender is Button { Flyout: { IsOpen: false } flyout } b) flyout.ShowAt(b);
    }

    void ClearSearch(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) Vm.Search = "";
    }

    // ---------- the list ----------

    void ListSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;                                    // sorted by the database, across every page
        if (Vm != null && e.Column.Tag is string key) Vm.SortBy(key);
    }

    static readonly Dictionary<string, string> Headers = new()
    {
        ["artist"] = "Artist", ["label"] = "Label", ["release"] = "Release", ["track"] = "Track",
        ["duration"] = "Length", ["date"] = "Released", ["posted"] = "Posted", ["year"] = "Year",
    };

    bool _placing;

    /// <summary>Show the list's columns in the chosen order, leaving out the ones switched off.
    /// The play and related buttons stay first and the hide button last.</summary>
    void ApplyColumns(MainViewModel vm)
    {
        _placing = true;
        try
        {
            var byKey = List.Columns.Where(c => c.Tag is string).ToDictionary(c => (string)c.Tag!);
            var fixedFirst = List.Columns.Where(c => c.Tag == null).Take(2).ToList();
            var last = List.Columns.Last(c => c.Tag == null);
            int i = 0;
            foreach (var c in fixedFirst) c.DisplayIndex = i++;
            foreach (var choice in vm.Columns)
                if (byKey.TryGetValue(choice.Key, out var col))
                {
                    col.IsVisible = choice.Shown;
                    col.DisplayIndex = i++;
                }
            last.DisplayIndex = i;
        }
        finally { _placing = false; }
    }

    /// <summary>A heading dragged to a new place: the order is kept (the same as using Edit Columns).</summary>
    void ListColumnReordered(object? sender, DataGridColumnEventArgs e)
    {
        if (_placing || Vm == null || e.Column.Tag is not string key) return;
        var order = List.Columns.Where(c => c.Tag is string).OrderBy(c => c.DisplayIndex).Select(c => (string)c.Tag!).ToList();
        var shownBefore = Vm.Columns.Select(c => c.Key).ToList();
        Vm.MoveColumn(key, order.IndexOf(key));
        if (Vm.Columns.Select(c => c.Key).SequenceEqual(shownBefore)) ApplyColumns(Vm);   // dropped beside the fixed buttons: put back
    }

    void ShowSortMark(MainViewModel vm)
    {
        foreach (var col in List.Columns)
            if (col.Tag is string key && Headers.TryGetValue(key, out var text))
                col.Header = key == vm.SortKey ? text + (vm.SortAscending ? " ▲" : " ▼") : text;
    }

    void ListLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.Tag is TrackRowViewModel old) old.PropertyChanged -= RowChanged;
        if (e.Row.DataContext is not TrackRowViewModel row) return;
        e.Row.Tag = row;
        row.PropertyChanged += RowChanged;
        Mark(e.Row, row);
    }

    void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TrackRowViewModel row) return;
        foreach (var r in List.GetVisualDescendants().OfType<DataGridRow>())
            if (r.Tag == row) Mark(r, row);
    }

    static void Mark(DataGridRow r, TrackRowViewModel row)
    {
        r.Classes.Set("played", row.IsPlayed && !row.IsPlaying);
        r.Classes.Set("playing", row.IsPlaying);
        r.Classes.Set("hidden", row.IsHidden);
        r.AreDetailsVisible = row.RelatedOpen;                // its related tracks, open under it
    }

    void ListDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (List.SelectedItem is TrackRowViewModel row) Vm?.PlayRowCommand.Execute(row);
    }

    // ---------- the player ----------

    public void ShowPlayer(PlayItem item, PlayerPage page)
    {
        _playing = item;
        DockTitle.Text = item.Title;
        DockHint.Text = item.NoStream ? "No preview for this track yet; the player shows what the release has available" : page.Hint;
        DockLink.Content = "Open on " + (Vm?.Engine.Sites.Get(item.Source)?.Label ?? item.Source);
        PlayerHost.Height = page.Height;
        Dock.IsVisible = true;
        if (_web == null)
        {
            try
            {
                _web = new NativeWebView();
                // The players' own sign-ins and settings (a SoundCloud like, say) are kept with your data.
                var store = Path.Combine(Vm?.DataFolder ?? Path.GetTempPath(), "webview");
                _web.EnvironmentRequested += (_, a) =>
                {
                    switch (a)
                    {
                        case Avalonia.Platform.WindowsWebView2EnvironmentRequestedEventArgs w: w.UserDataFolder = store; break;
                        case Avalonia.Platform.GtkWebViewEnvironmentRequestedEventArgs g:
                            g.BaseDataDirectory = store;
                            g.BaseCacheDirectory = Path.Combine(store, "cache");
                            break;
                    }
                };
                _web.NewWindowRequested += (_, a) =>
                {
                    // A link inside a player (Buy, the artist's page) opens in your browser.
                    if (a.Request is { } uri) Desktop.Open(uri.ToString());
                    a.Handled = true;
                };
                PlayerHost.Children.Add(_web);
            }
            catch (Exception)
            {
                _web = null;
            }
        }
        if (_web == null)
        {
            DockHint.Text = "This computer can't show the site's player here; use Open on the site.";
            return;
        }
        try
        {
            // An https address for the page, so the players see an ordinary embedding site.
            _web.NavigateToString(Players.Html(page), new Uri("https://artist-feed.app/player"));
        }
        catch (Exception)
        {
            try { _web.Navigate(new Uri(page.Src)); }          // else the player's own page
            catch (Exception) { DockHint.Text = "The player couldn't open here; use Open on the site."; }
        }
    }

    public void HidePlayer()
    {
        _playing = null;
        Dock.IsVisible = false;
        if (_web != null)
        {
            try { _web.NavigateToString($"<html><body style='background:{Display.PanelCss}'></body></html>", new Uri("https://artist-feed.app/")); }
            catch (Exception) { }
        }
    }

    void OpenPlaying(object? sender, RoutedEventArgs e)
    {
        if (_playing != null) Desktop.Open(_playing.Url);
    }

    // ---------- dialogs and other windows ----------

    public Task<bool> ConfirmAsync(string title, string message, string yes = "OK", string no = "Cancel") =>
        ConfirmDialog.AskAsync(Front, title, message, yes, no);

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string text)
    {
        var owner = (TopLevel)Front;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV spreadsheet") { Patterns = new[] { "*.csv" } } },
        });
        if (file == null) return null;
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        await writer.WriteAsync(text);
        return file.Name;
    }

    public async Task<string?> OpenTextFileAsync(string title)
    {
        var owner = (TopLevel)Front;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("CSV or text") { Patterns = new[] { "*.csv", "*.txt", "*.tsv" } } },
        });
        if (files.Count == 0) return null;
        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }

    public async Task<string?> OpenDatabaseFileAsync()
    {
        var owner = (TopLevel?)(OwnedWindows.OfType<SettingsWindow>().FirstOrDefault() ?? (Window)this);
        var files = await owner!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the web version's feed.db (in its data folder)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Artist Feed database") { Patterns = new[] { "*.db" } }, FilePickerFileTypes.All },
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public Task ShowTrackedArtistsAsync()
    {
        if (Vm == null) return Task.CompletedTask;
        if (_artists is { IsVisible: true })
        {
            _artists.Activate();
            return Task.CompletedTask;
        }
        var vm = new TrackedArtistsViewModel(Vm) { Ui = this };
        _artists = new TrackedArtistsWindow { DataContext = vm };
        _artists.Closed += (_, _) => _artists = null;
        _artists.Show(this);
        return Task.CompletedTask;
    }

    public async Task<byte[]?> OpenTableFileAsync(string title)
    {
        var files = await ((TopLevel)Front).StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Excel, CSV or text") { Patterns = new[] { "*.xlsx", "*.csv", "*.tsv", "*.txt" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.Count == 0) return null;
        await using var stream = await files[0].OpenReadAsync();
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    public Task ShowLinkFinderAsync()
    {
        if (Vm == null) return Task.CompletedTask;
        if (_finder is { IsVisible: true })
        {
            _finder.Activate();
            return Task.CompletedTask;
        }
        _finder = new LinkFinderWindow { DataContext = new LinkFinderViewModel(Vm) { Ui = this } };
        _finder.Closed += (_, _) =>
        {
            _finder = null;
            Vm?.RefreshDownloads();
        };
        _finder.Show(this);
        return Task.CompletedTask;
    }

    public async Task ShowSettingsAsync()
    {
        if (Vm == null) return;
        var dialog = new SettingsWindow { DataContext = new SettingsViewModel(Vm) { Ui = this } };
        await dialog.ShowDialog(this);
    }
}
