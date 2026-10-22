using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ArtistFeed.App.Services;
using ArtistFeed.Core;

namespace ArtistFeed.App.ViewModels;

/// <summary>What the window can do for a view model: ask, pick files, open other windows.</summary>
public interface IUi
{
    Task<bool> ConfirmAsync(string title, string message, string yes = "OK", string no = "Cancel");
    Task<string?> SaveFileAsync(string title, string suggestedName, string text);
    Task<string?> OpenTextFileAsync(string title);
    Task<string?> OpenDatabaseFileAsync();
    Task<byte[]?> OpenTableFileAsync(string title);
    Task ShowTrackedArtistsAsync();
    Task ShowLinkFinderAsync();
    Task ShowSettingsAsync();
    void ShowPlayer(PlayItem item, PlayerPage page);
    void HidePlayer();
}

public partial class MainViewModel : ObservableObject
{
    public const int Page = 60, ListPage = 200;

    public Engine Engine { get; }
    public string DataFolder { get; }
    public ImageCache Images { get; }
    public IUi? Ui { get; set; }
    public string Version => "v" + Engine.Version + " desktop";

    public ObservableCollection<SiteChip> Chips { get; } = new();
    public ObservableCollection<ArtistOption> Artists { get; } = new();
    public ObservableCollection<Option> Years { get; } = new();
    public ObservableCollection<Option> Labels { get; } = new();
    public ObservableCollection<PostCardViewModel> Posts { get; } = new();
    public ObservableCollection<TrackRowViewModel> Rows { get; } = new();

    public List<Option> Kinds { get; } = new() { new("", "Any type"), new("album", "Albums & EPs"), new("track", "Single tracks") };
    public List<Option> LengthOptions { get; } = new()
    {
        new("", "Any length"), new("short", "Under 3 min"), new("medium", "3–6 min"), new("long", "6–10 min"), new("extended", "Over 10 min"),
    };
    public List<Option> Ranges { get; } = new()
    {
        new("", "Any date"), new("7", "Last 7 days"), new("30", "Last 30 days"), new("90", "Last 90 days"),
        new("365", "Last 12 months"), new("custom", "Date range…"),
    };
    public List<Option> DateBys { get; } = new() { new("posted", "by posted date"), new("released", "by release date") };
    public List<Option> MaxOptions { get; } = new()
    {
        new("", "All results"), new("25", "First 25"), new("50", "First 50"), new("100", "First 100"), new("250", "First 250"), new("500", "First 500"),
    };

    [ObservableProperty] private ArtistOption? _artist;
    [ObservableProperty] private string _search = "";
    /// <summary>The workspace menu: Every artist, then each workspace with how many artists it holds.</summary>
    public ObservableCollection<Option> WorkspaceOptions { get; } = new();
    [ObservableProperty] private Option? _workspace;
    [ObservableProperty] private bool _isGrid = true;
    [ObservableProperty] private Option? _year;
    [ObservableProperty] private Option? _label;
    [ObservableProperty] private Option? _kind;
    [ObservableProperty] private Option? _length;
    [ObservableProperty] private Option? _range;
    [ObservableProperty] private Option? _dateBy;
    [ObservableProperty] private DateTime? _since;
    [ObservableProperty] private DateTime? _until;
    [ObservableProperty] private bool _previewOnly;
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private int _hiddenCount;
    [ObservableProperty] private Option? _max;
    [ObservableProperty] private bool _canLoadMore;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _statusTip = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyTitle = "";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _sortKey = "posted";
    [ObservableProperty] private bool _sortAscending;
    [ObservableProperty] private bool _refreshDisabled;
    [ObservableProperty] private PlayItem? _playing;
    [ObservableProperty] private bool _hasFilters;

    int _offset;
    int _seq;
    bool _quiet;                    // while filters are being reset together
    bool _wasRefreshing;
    readonly DispatcherTimer _statusTimer;
    readonly DispatcherTimer _searchTimer;

    public bool IsList => !IsGrid;
    public bool ShowRangeBox => Range?.Value == "custom";
    public bool ShowDateBy => !string.IsNullOrEmpty(Range?.Value);

    public MainViewModel(Engine engine, string dataFolder)
    {
        Engine = engine;
        DataFolder = dataFolder;
        Images = new ImageCache(engine.MakeClient(), Path.Combine(dataFolder, "covers"));
        _quiet = true;
        _kind = Kinds[0];
        _length = LengthOptions[0];
        _range = Ranges[0];
        _dateBy = DateBys[0];
        _max = MaxOptions[0];
        LoadPrefs();
        _quiet = false;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Reload(); };
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        engine.Refresher.Changed += () => Dispatcher.UIThread.Post(UpdateStatus);
        RebuildChips();
        LoadArtists();
        LoadFacets();
        Reload();
        UpdateStatus();
    }

    // ---------- preferences kept between runs ----------

    void LoadPrefs()
    {
        if (Html.ParseJson(Engine.Db.KvGet("desktop_prefs")) is not JsonObject p) return;
        _isGrid = p["view"]?.GetValue<string>() != "list";
        _previewOnly = p["hideNoPreview"]?.GetValue<bool>() ?? false;
        var max = p["maxResults"]?.GetValue<string>() ?? "";
        _max = MaxOptions.FirstOrDefault(o => o.Value == max) ?? MaxOptions[0];
        _sortKey = p["sort"]?.GetValue<string>() ?? "posted";
        _sortAscending = p["sortAsc"]?.GetValue<bool>() ?? false;
    }

    void SavePrefs()
    {
        var p = new JsonObject
        {
            ["view"] = IsGrid ? "grid" : "list", ["hideNoPreview"] = PreviewOnly, ["maxResults"] = Max?.Value ?? "",
            ["sort"] = SortKey, ["sortAsc"] = SortAscending,
        };
        Engine.Db.KvSet("desktop_prefs", p.ToJsonString());
    }

    // ---------- filters ----------

    public void RebuildChips()
    {
        var selected = Chips.FirstOrDefault(c => c.IsChecked)?.Key;
        Chips.Clear();
        var on = Engine.Sites.Enabled();
        if (selected != null && !on.Contains(selected)) selected = null;
        Chips.Add(new SiteChip(null, "All", "#888888") { IsChecked = selected == null });
        foreach (var s in Engine.Sites.All.Where(s => on.Contains(s.Key)))
            Chips.Add(new SiteChip(s.Key, s.Label, s.Color) { IsChecked = selected == s.Key });
    }

    [RelayCommand]
    void PickSite(SiteChip chip)
    {
        foreach (var c in Chips) c.IsChecked = c == chip;
        Reload();
    }

    string? SelectedSource => Chips.FirstOrDefault(c => c.IsChecked)?.Key;

    public void LoadArtists()
    {
        var keep = Artist?.Id;
        _quiet = true;
        Artists.Clear();
        Artists.Add(new ArtistOption(null, "All artists"));
        foreach (var a in Engine.Db.ListArtists()) Artists.Add(new ArtistOption(a.Id, a.Name));
        Artist = Artists.FirstOrDefault(a => a.Id == keep) ?? Artists[0];
        var space = Workspace?.Value;
        WorkspaceOptions.Clear();
        WorkspaceOptions.Add(new Option("", "Every artist"));
        foreach (var (w, n) in Engine.Sites.WorkspaceCounts()) WorkspaceOptions.Add(new Option(w.Id, $"{w.Name} ({n})"));
        Workspace = WorkspaceOptions.FirstOrDefault(o => o.Value == space) ?? WorkspaceOptions[0];
        _quiet = false;
    }

    public void LoadFacets()
    {
        var f = Engine.Db.GetFacets();
        HiddenCount = f.Hidden;
        _quiet = true;
        var year = Year?.Value;
        var label = Label?.Value;
        Years.Clear();
        Years.Add(new Option("", "Any year"));
        foreach (var y in f.Years) Years.Add(new Option(y, y));
        Labels.Clear();
        Labels.Add(new Option("", "Any label"));
        Labels.Add(new Option("-", "No label"));
        foreach (var l in f.Labels) Labels.Add(new Option(l, l));
        Year = Years.FirstOrDefault(o => o.Value == year) ?? Years[0];      // a filter whose value no longer exists is dropped
        Label = Labels.FirstOrDefault(o => o.Value == label) ?? Labels[0];
        _quiet = false;
    }

    (string? Since, string? Until) DateRange()
    {
        var r = Range?.Value ?? "";
        if (r.Length == 0) return (null, null);
        string? Day(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (r == "custom") return (Day(Since), Day(Until));
        return (DateTime.Now.Date.AddDays(-int.Parse(r, CultureInfo.InvariantCulture)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);
    }

    public FeedFilter CurrentFilter()
    {
        var (since, until) = DateRange();
        var f = new FeedFilter
        {
            Source = SelectedSource,
            ArtistId = Artist?.Id,
            Query = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
            Year = NullIfEmpty(Year?.Value),
            Label = NullIfEmpty(Label?.Value),
            Kind = NullIfEmpty(Kind?.Value),
            Length = NullIfEmpty(Length?.Value),
            ShowHidden = ShowHidden,
            PreviewOnly = PreviewOnly,
            Since = since,
            Until = until,
            DateByReleased = (since != null || until != null) && DateBy?.Value == "released",
        };
        return Engine.WithWorkspace(f, NullIfEmpty(Workspace?.Value));
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    int MaxResults => int.TryParse(Max?.Value, out var n) ? n : 0;

    int PageSize(int size) => MaxResults > 0 ? Math.Max(0, Math.Min(size, MaxResults - _offset)) : size;

    partial void OnArtistChanged(ArtistOption? value) => FilterChanged();
    partial void OnSearchChanged(string value)
    {
        if (_quiet) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }
    partial void OnWorkspaceChanged(Option? value) => FilterChanged();
    partial void OnYearChanged(Option? value) => FilterChanged();
    partial void OnLabelChanged(Option? value) => FilterChanged();
    partial void OnKindChanged(Option? value) => FilterChanged();
    partial void OnLengthChanged(Option? value) => FilterChanged();
    partial void OnRangeChanged(Option? value)
    {
        OnPropertyChanged(nameof(ShowRangeBox));
        OnPropertyChanged(nameof(ShowDateBy));
        if (value?.Value != "custom" || Since != null || Until != null) FilterChanged();
    }
    partial void OnDateByChanged(Option? value) => FilterChanged();
    partial void OnSinceChanged(DateTime? value) => FilterChanged();
    partial void OnUntilChanged(DateTime? value) => FilterChanged();
    partial void OnPreviewOnlyChanged(bool value) { SavePrefs(); FilterChanged(); }
    partial void OnShowHiddenChanged(bool value) => FilterChanged();
    partial void OnMaxChanged(Option? value) { SavePrefs(); FilterChanged(); }
    partial void OnIsGridChanged(bool value)
    {
        OnPropertyChanged(nameof(IsList));
        SavePrefs();
        FilterChanged();
    }

    void FilterChanged()
    {
        if (_quiet) return;
        Reload();
    }

    [RelayCommand]
    void ClearFilters()
    {
        _quiet = true;
        foreach (var c in Chips) c.IsChecked = c.Key == null;
        Artist = Artists.FirstOrDefault();
        Search = "";
        Workspace = WorkspaceOptions.FirstOrDefault();
        Year = Years.FirstOrDefault();
        Label = Labels.FirstOrDefault();
        Kind = Kinds[0];
        Length = LengthOptions[0];
        Range = Ranges[0];
        Since = Until = null;
        ShowHidden = false;
        _quiet = false;
        Reload();
    }

    [RelayCommand] void ShowGrid() => IsGrid = true;
    [RelayCommand] void ShowList() => IsGrid = false;

    public void SortBy(string key)
    {
        if (SortKey == key) SortAscending = !SortAscending;
        else
        {
            SortKey = key;
            SortAscending = key is "artist" or "label" or "release" or "track";      // words A to Z, dates newest first
        }
        SavePrefs();
        Reload();
    }

    // ---------- loading ----------

    public void Reload()
    {
        _offset = 0;
        var seq = ++_seq;
        var f = CurrentFilter();
        HasFilters = SelectedSource != null || f.ArtistId != null || f.Query != null || f.Year != null || f.Label != null ||
                     f.Kind != null || f.Length != null || ShowHidden || !string.IsNullOrEmpty(Range?.Value) || !string.IsNullOrEmpty(Workspace?.Value);
        Error = "";
        try
        {
            if (IsGrid) LoadGrid(f, true, seq);
            else LoadList(f, true, seq);
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    [RelayCommand]
    void LoadMore()
    {
        var f = CurrentFilter();
        try
        {
            if (IsGrid) LoadGrid(f, false, _seq);
            else LoadList(f, false, _seq);
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    void LoadGrid(FeedFilter f, bool reset, int seq)
    {
        var want = PageSize(Page);
        var posts = want > 0 ? Engine.Db.Feed(f, Engine.Sites.Enabled(), want, _offset) : new List<FeedPost>();
        if (seq != _seq) return;
        if (reset) Posts.Clear();
        foreach (var p in posts) Posts.Add(new PostCardViewModel(p, Engine.Sites.Get(p.Source), Images, Playing?.Key));
        _offset += posts.Count;
        CanLoadMore = posts.Count == Page && !(MaxResults > 0 && _offset >= MaxResults);
        if (reset) Rows.Clear();
        ShowEmpty(Posts.Count == 0);
    }

    void LoadList(FeedFilter f, bool reset, int seq)
    {
        var want = PageSize(ListPage);
        var rows = want > 0 ? Engine.Db.Tracks(f, Engine.Sites.Enabled(), SortKey, SortAscending, want, _offset) : new List<TrackRow>();
        if (seq != _seq) return;
        if (reset) Rows.Clear();
        foreach (var r in rows) Rows.Add(new TrackRowViewModel(r, Engine.Sites.Get(r.Source), Playing?.Key));
        _offset += rows.Count;
        CanLoadMore = rows.Count == ListPage && !(MaxResults > 0 && _offset >= MaxResults);
        if (reset) Posts.Clear();
        ShowEmpty(Rows.Count == 0);
    }

    void ShowEmpty(bool none)
    {
        IsEmpty = none;
        if (!none) return;
        var artists = Artists.Count > 1;
        var space = Engine.Sites.WorkspaceCounts().FirstOrDefault(w => w.Space.Id == Workspace?.Value);
        if (space.Space != null && artists && space.Count == 0)
        {
            EmptyTitle = $"No artists in {space.Space.Name} yet";
            EmptyText = $"Tick the {space.Space.Name} box beside an artist in Tracked Artists, then Save.";
            return;
        }
        EmptyTitle = !artists ? "No artists yet" : HasFilters ? "Nothing matches" : "Nothing here yet";
        EmptyText = !artists ? "Use the Tracked Artists button to add your first one."
            : HasFilters ? "Try a different search or filter." : "New posts appear after the next refresh.";
    }

    // ---------- status and refresh ----------

    static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    static string Summary(Refresher.Summary? r)
    {
        if (r == null) return "";
        var parts = new List<string> { r.New > 0 ? Plural(r.New, "new post") : "nothing new" };
        if (r.Held > 0)
            parts.Add($"{Plural(r.Held, "Bandcamp page")} skipped{(r.LookupRunning ? " while a lookup ran" : "")}, tried again next time");
        if (r.Errors > 0) parts.Add($"{r.Errors} page{(r.Errors > 1 ? "s" : "")} couldn't be read (see Tracked Artists)");
        return " · " + string.Join(" · ", parts);
    }

    static string ProgressText(Refresher.Progress? p)
    {
        if (p == null || p.Pages == 0) return "";
        var text = $" {p.Done:N0} of {p.Pages:N0} page{(p.Pages == 1 ? "" : "s")}";
        if (p.SecondsLeft is { } left && p.Done < p.Pages)
        {
            var min = (int)Math.Round(left / 60.0);
            text += " · " + (left < 60 ? "less than a minute left"
                : min < 60 ? $"about {min} min left"
                : $"about {min / 60} h {(min % 60 > 0 ? $"{min % 60} min " : "")}left");
        }
        return text;
    }

    public void UpdateStatus()
    {
        var r = Engine.Refresher;
        var refreshing = r.Refreshing;
        var onHold = Engine.Pace.OnHold || r.Held > 0;
        Status = refreshing
            ? "Checking for new posts…" + ProgressText(r.CurrentProgress()) + (r.Queued ? " (a full refresh follows)" : "")
            : r.LastRefresh != null ? "Updated " + Show.Ago(r.LastRefresh) + Summary(r.LastResult) : "";
        if (onHold && !refreshing && !(r.LastResult?.Held > 0)) Status += " · Bandcamp on hold";
        StatusTip = onHold ? "Bandcamp checks are on hold while Bandcamp has asked for a pause; they resume on the next refresh." : "";
        RefreshDisabled = refreshing && r.Queued;
        RefreshDownloads();
        _statusTimer.Interval = TimeSpan.FromSeconds(refreshing ? 2 : 15);
        if (_wasRefreshing && !refreshing)
        {
            LoadArtists();
            LoadFacets();
            Reload();
        }
        _wasRefreshing = refreshing;
    }

    [RelayCommand]
    void Refresh()
    {
        var state = Engine.Refresher.RequestRefresh();
        _wasRefreshing = true;
        Status = state == "queued" ? "Checking for new posts… (a full refresh follows)" : "Checking for new posts…";
    }

    [RelayCommand]
    async Task Export()
    {
        if (Ui == null) return;
        var f = CurrentFilter();
        var rows = Engine.Db.Tracks(f, Engine.Sites.Enabled(), SortKey, SortAscending, MaxResults > 0 ? MaxResults : null);
        await Ui.SaveFileAsync("Export the feed", "tracks.csv", "﻿" + FeedExport.TracksCsv(rows, Engine.Sites));
    }

    // ---------- playing, hiding, opening ----------

    [RelayCommand]
    void PlayCard(PostCardViewModel card)
    {
        if (!card.CanPlay)
        {
            Desktop.Open(card.Post.Url);
            return;
        }
        Play(card.Item);
        card.IsPlayed = true;
    }

    [RelayCommand]
    void PlayRow(TrackRowViewModel row)
    {
        if (!row.CanPlay)
        {
            Desktop.Open(row.Row.Url);
            return;
        }
        Play(row.Item);
        row.IsPlayed = true;
    }

    void Play(PlayItem item)
    {
        var page = Players.For(item);
        if (page == null) return;
        Playing = item;
        foreach (var c in Posts) c.IsPlaying = c.Item.Key == item.Key;
        foreach (var r in Rows) r.IsPlaying = r.Item.Key == item.Key;
        Ui?.ShowPlayer(item, page);
        Engine.Db.SetPlayed(item.PostId, item.TrackKey);
    }

    [RelayCommand]
    void ClosePlayer()
    {
        Playing = null;
        foreach (var c in Posts) c.IsPlaying = false;
        foreach (var r in Rows) r.IsPlaying = false;
        Ui?.HidePlayer();
    }

    [RelayCommand]
    static void Open(string? url)
    {
        if (!string.IsNullOrEmpty(url)) Desktop.Open(url);
    }

    [RelayCommand]
    void ToggleHiddenCard(PostCardViewModel card)
    {
        var hide = !card.IsHidden;
        if (!Engine.Db.SetHidden(card.Post.Id, hide)) return;
        HiddenCount = Engine.Db.GetFacets().Hidden;
        if (ShowHidden) card.IsHidden = hide;
        else
        {
            Posts.Remove(card);
            _offset = Math.Max(0, _offset - 1);
            if (Posts.Count == 0) Reload();
        }
    }

    [RelayCommand]
    void ToggleHiddenRow(TrackRowViewModel row)
    {
        var hide = !row.Row.Hidden;
        // One track of a release on its own; a release of one track is hidden whole.
        if (!Engine.Db.SetTrackHidden(row.Row.PostId, row.Row.TrackKey, hide)) return;
        HiddenCount = Engine.Db.GetFacets().Hidden;
        Reload();
    }

    [RelayCommand]
    void TogglePlayedRow(TrackRowViewModel row)
    {
        var played = !row.IsPlayed;
        Engine.Db.SetPlayed(row.Row.PostId, row.Row.TrackKey, played);
        row.IsPlayed = played;
    }

    [RelayCommand]
    async Task OpenTrackedArtists()
    {
        if (Ui != null) await Ui.ShowTrackedArtistsAsync();
    }

    [RelayCommand]
    async Task OpenLinkFinder()
    {
        if (Ui != null) await Ui.ShowLinkFinderAsync();
    }

    // ---------- the Downloads button ----------

    [ObservableProperty] private bool _downloadsOff = true;
    [ObservableProperty] private int _downloadsCount;
    [ObservableProperty] private string _downloadsTip = "";
    string _downloadsUrl = "";
    public bool HasDownloads => DownloadsCount > 0;
    public string DownloadsBadge => DownloadsCount > 99 ? "99+" : DownloadsCount.ToString();
    partial void OnDownloadsCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasDownloads));
        OnPropertyChanged(nameof(DownloadsBadge));
    }

    /// <summary>Bandcamp purchases and pre-orders ready to download, as the Downloads button shows them.</summary>
    public void RefreshDownloads()
    {
        JsonObject d;
        try { d = Engine.LinkFinder.Downloads(); }
        catch (Exception) { return; }                        // the database is closing (the app is shutting down)
        var on = d["on"]?.GetValue<bool>() == true;
        var fan = d["fan"]?.GetValue<string>() ?? "";
        DownloadsOff = !on || fan.Length == 0;
        if (DownloadsOff)
        {
            DownloadsCount = 0;
            _downloadsUrl = "";
            DownloadsTip = !on
                ? "Downloads is switched off. Turn it on in Link Finder (tick Show the Downloads button, beside your Bandcamp username) to see when your Bandcamp purchases and pre-orders are ready to download."
                : "Set your Bandcamp username in Link Finder to use Downloads.";
            return;
        }
        _downloadsUrl = d["url"]?.GetValue<string>() ?? "";
        var items = (d["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(x => $"{x["band"]?.GetValue<string>()} \u2014 {x["title"]?.GetValue<string>()}").ToList();
        DownloadsCount = items.Count;
        DownloadsTip = items.Count > 0
            ? "Ready to download on Bandcamp:\n" + string.Join("\n", items.Take(10)) + (items.Count > 10 ? $"\n…and {items.Count - 10} more" : "")
              + "\n\nOpens your Bandcamp collection and clears this list."
            : "Nothing new to download. Opens your Bandcamp collection.";
    }

    [RelayCommand]
    void OpenDownloads()
    {
        if (DownloadsOff || _downloadsUrl.Length == 0) return;
        Services.Desktop.Open(_downloadsUrl);
        Engine.LinkFinder.ClearDownloads();
        RefreshDownloads();
    }

    [RelayCommand]
    async Task OpenSettings()
    {
        if (Ui != null) await Ui.ShowSettingsAsync();
    }

    /// <summary>After Tracked Artists or Settings saved something: the buttons, menus and feed follow.</summary>
    public void SitesOrArtistsChanged()
    {
        RebuildChips();
        LoadArtists();
        LoadFacets();
        Reload();
        UpdateStatus();
    }
}
