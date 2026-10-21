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
        ["duration"] = "Length", ["date"] = "Released", ["posted"] = "Posted",
    };

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
        ConfirmDialog.AskAsync(_artists is { IsVisible: true } ? _artists : this, title, message, yes, no);

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string text)
    {
        var owner = (TopLevel?)(_artists is { IsVisible: true } ? _artists : this);
        var file = await owner!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
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
        var owner = (TopLevel?)(_artists is { IsVisible: true } ? _artists : this);
        var files = await owner!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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

    public async Task ShowSettingsAsync()
    {
        if (Vm == null) return;
        var dialog = new SettingsWindow { DataContext = new SettingsViewModel(Vm) { Ui = this } };
        await dialog.ShowDialog(this);
    }
}
