using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.RegularExpressions;
using ArtistFeed.Core;

namespace ArtistFeed.App.ViewModels;

/// <summary>A workspace as it is being edited in Tracked Artists (kept when the table is saved).</summary>
public partial class WorkspaceDraft(string id, string name) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] private string _name = name;
    [ObservableProperty] private int _count;
    public string CountText => Count == 1 ? "1 artist" : $"{Count} artists";
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));
}

/// <summary>One row's tick for one workspace.</summary>
public partial class GroupTick(WorkspaceDraft space, bool isIn) : ObservableObject
{
    public WorkspaceDraft Space { get; } = space;
    [ObservableProperty] private bool _isIn = isIn;
}

/// <summary>One profile, artist or label an account follows, in the Import follows list.</summary>
public partial class FollowItem(Followed f, bool inTable) : ObservableObject
{
    public Followed Followed { get; } = f;
    public string Name => Followed.Name;
    [ObservableProperty] private bool _inTable = inTable;
    [ObservableProperty] private bool _pick = true;
    public bool Open => !Followed.Tracked && !InTable;
    public string Detail => Followed.Tracked ? "already tracked" : InTable ? "already in the table" : Followed.Handle;
    partial void OnInTableChanged(bool value)
    {
        OnPropertyChanged(nameof(Open));
        OnPropertyChanged(nameof(Detail));
    }
}

/// <summary>One editable row of Tracked Artists.</summary>
public partial class ArtistRowViewModel : ObservableObject
{
    public long? Id { get; init; }
    public long PostCount { get; init; }
    public List<string> Skip { get; init; } = new();
    public Dictionary<string, List<string>> Rejected { get; init; } = new();
    public List<string> Searched { get; set; } = new();   // sites Find missing links already searched for this row
    public List<string> Problems { get; init; } = new();
    public int Order { get; set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _bandcamp = "";
    [ObservableProperty] private string _soundCloud = "";
    [ObservableProperty] private string _youTube = "";
    [ObservableProperty] private bool _youtubeLookup;
    [ObservableProperty] private bool _remove;
    [ObservableProperty] private bool _keepSeparate;
    [ObservableProperty] private bool _sameName;          // the Keep both box shows while two rows share a name
    [ObservableProperty] private string _errors = "";
    [ObservableProperty] private bool _checking;
    [ObservableProperty] private bool _rescan;            // Find missing links searches this row's cleared cells again
    [ObservableProperty] private bool _searching;         // Find missing links is looking for this row now
    // Cells filled in by Find missing links, shown highlighted until edited or saved.
    [ObservableProperty] private bool _autoName;
    [ObservableProperty] private bool _autoBandcamp;
    [ObservableProperty] private bool _autoSoundCloud;
    internal bool Filling;                                 // cells being set by Find missing links, not typed

    /// <summary>Which of this row's cells Find missing links may look for (set by Tracked Artists).</summary>
    public Func<ArtistRowViewModel, List<string>>? OpenCells { get; set; }
    public bool HasOpen => !Remove && !IsBlank && (OpenCells?.Invoke(this).Count ?? 0) > 0;

    // A cell emptied on purpose is remembered: Find missing links leaves it alone unless Rescan is
    // ticked, and whatever was in it is never offered for that cell again.
    void Edited(string col, string? was, string now)
    {
        if (!Filling)
        {
            if (col == "name") AutoName = false;
            if (col == "bandcamp") AutoBandcamp = false;
            if (col == "soundcloud") AutoSoundCloud = false;
            if (now.Trim().Length == 0 && (was ?? "").Trim().Length > 0)
            {
                if (!Skip.Contains(col)) Skip.Add(col);
                if (col != "name")
                {
                    if (!Rejected.TryGetValue(col, out var list)) Rejected[col] = list = new List<string>();
                    if (!list.Contains(was!.Trim())) list.Add(was!.Trim());
                }
            }
            else if (now.Trim().Length > 0) Skip.Remove(col);
        }
        OnPropertyChanged(nameof(HasOpen));
    }

    partial void OnNameChanged(string? oldValue, string newValue) => Edited("name", oldValue, newValue);
    partial void OnBandcampChanged(string? oldValue, string newValue) => Edited("bandcamp", oldValue, newValue);
    partial void OnSoundCloudChanged(string? oldValue, string newValue) => Edited("soundcloud", oldValue, newValue);
    partial void OnRescanChanged(bool value) => OnPropertyChanged(nameof(HasOpen));
    internal void OpenChanged() => OnPropertyChanged(nameof(HasOpen));
    partial void OnSearchingChanged(bool value) => OnPropertyChanged(nameof(StatusShort));

    /// <summary>A tick for each workspace; ticking one marks the row changed (as "Groups").</summary>
    public ObservableCollection<GroupTick> Ticks { get; } = new();
    public List<string> Groups => Ticks.Where(t => t.IsIn).Select(t => t.Space.Id).ToList();
    public bool InGroup(string id) => Ticks.Any(t => t.IsIn && t.Space.Id == id);

    /// <summary>Ticks for these workspaces, keeping the row's choices (or taking `start` the first time).</summary>
    public void SetSpaces(IEnumerable<WorkspaceDraft> spaces, IEnumerable<string>? start = null)
    {
        var keep = (start ?? Groups).ToHashSet();
        foreach (var t in Ticks) t.PropertyChanged -= TickChanged;
        Ticks.Clear();
        foreach (var w in spaces)
        {
            var t = new GroupTick(w, keep.Contains(w.Id));
            t.PropertyChanged += TickChanged;
            Ticks.Add(t);
        }
    }

    void TickChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GroupTick.IsIn)) OnPropertyChanged(nameof(Groups));
    }

    public bool IsNew => Id == null;
    public bool IsBlank => Name.Trim().Length == 0 && Bandcamp.Trim().Length == 0 && SoundCloud.Trim().Length == 0 && YouTube.Trim().Length == 0;
    public bool HasIssue => Errors.Length > 0 || Problems.Any(p => !p.EndsWith("checking…"));
    public bool IsChecking => Problems.Any(p => p.EndsWith("checking…"));

    public string StatusText => IsNew ? "Not saved yet"
        : Problems.Count > 0 ? string.Join("\n", Problems)
        : PostCount == 1 ? "1 post" : $"{PostCount} posts";

    public string StatusShort => Searching ? "searching…" : IsNew ? "new"
        : HasIssue ? "⚠ " + (Problems.FirstOrDefault(p => !p.EndsWith("checking…")) ?? "")
        : IsChecking ? "checking…"
        : PostCount == 1 ? "1 post" : $"{PostCount} posts";

    public string RemoveGlyph => Remove ? "↺" : "✕";
    public string RemoveTip => Remove ? "Keep tracking this artist" : "Stop tracking this artist (when you save)";

    partial void OnErrorsChanged(string value)
    {
        OnPropertyChanged(nameof(HasIssue));
        OnPropertyChanged(nameof(StatusShort));
        OnPropertyChanged(nameof(HasErrors));
    }

    public bool HasErrors => Errors.Length > 0;

    partial void OnRemoveChanged(bool value)
    {
        OnPropertyChanged(nameof(HasOpen));
        OnPropertyChanged(nameof(RemoveGlyph));
        OnPropertyChanged(nameof(RemoveTip));
    }

    public string Cell(string key) => key switch { "bandcamp" => Bandcamp, "soundcloud" => SoundCloud, "youtube" => YouTube, _ => "" };

    public void SetCell(string key, string value)
    {
        switch (key)
        {
            case "bandcamp": Bandcamp = value; break;
            case "soundcloud": SoundCloud = value; break;
            case "youtube": YouTube = value; break;
        }
    }

    public static ArtistRowViewModel From(TableRow r, IEnumerable<WorkspaceDraft> spaces)
    {
        var row = From(r);
        row.SetSpaces(spaces, r.Groups.Concat(r.Focus ? new[] { "focus" } : Array.Empty<string>()));
        return row;
    }

    static ArtistRowViewModel From(TableRow r) => new()
    {
        Id = r.Id, PostCount = r.PostCount, Skip = r.Skip, Rejected = r.Rejected, Searched = r.Searched, Problems = r.Problems,
        Name = r.Name, Bandcamp = r.Cells.GetValueOrDefault("bandcamp") ?? "", SoundCloud = r.Cells.GetValueOrDefault("soundcloud") ?? "",
        YouTube = r.Cells.GetValueOrDefault("youtube") ?? "", YoutubeLookup = r.YoutubeLookup == true, SameName = r.SameName,
        KeepSeparate = r.SameName,
    };
}

public partial class TrackedArtistsViewModel : ObservableObject
{
    readonly MainViewModel _main;
    Engine Engine => _main.Engine;
    public IUi? Ui { get; set; }

    public ObservableCollection<ArtistRowViewModel> Rows { get; } = new();
    public ObservableCollection<ArtistRowViewModel> Visible { get; } = new();
    public ObservableCollection<Option> ShowOptions { get; } = new();
    public ObservableCollection<WorkspaceDraft> Spaces { get; } = new();
    [ObservableProperty] private bool _showSpaces;

    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private Option? _show;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _pasteText = "";
    [ObservableProperty] private bool _saving;

    List<long?> _savedIds = new();
    bool _loading;

    public bool BandcampOn => Engine.Sites.IsOn("bandcamp");
    public bool SoundCloudOn => Engine.Sites.IsOn("soundcloud");
    public bool YouTubeOn => Engine.Sites.IsOn("youtube");

    public TrackedArtistsViewModel(MainViewModel main)
    {
        _main = main;
        LoadFillPrefs();
        Load();
    }

    public void Load()
    {
        _loading = true;
        foreach (var r in Rows) r.PropertyChanged -= RowChanged;
        Rows.Clear();
        Spaces.Clear();
        foreach (var w in Engine.Sites.Workspaces())
        {
            var draft = new WorkspaceDraft(w.Id, w.Name);
            draft.PropertyChanged += SpaceRenamed;
            Spaces.Add(draft);
        }
        OnPropertyChanged(nameof(SpaceChoices));
        var rows = Engine.Table.Rows();
        // In name order, alphabetically (case and accents aside), as the web version loads them.
        var ordered = rows.Select(r => ArtistRowViewModel.From(r, Spaces)).OrderBy(r => r.Name.Trim(), NameOrder).ToList();
        foreach (var r in ordered) Add(r);
        _savedIds = rows.Select(r => r.Id).ToList();
        Error = "";
        Note = "";
        FillStatus = "";
        _loading = false;
        Dirty = false;
        RefreshView();
        OnPropertyChanged(nameof(BandcampOn));
        OnPropertyChanged(nameof(SoundCloudOn));
        OnPropertyChanged(nameof(YouTubeOn));
    }

    void Add(ArtistRowViewModel r)
    {
        r.OpenCells = x => Wanted(x, true);
        r.Order = Rows.Count;
        r.PropertyChanged += RowChanged;
        Rows.Add(r);
    }

    static readonly HashSet<string> Edits = new()
    {
        nameof(ArtistRowViewModel.Name), nameof(ArtistRowViewModel.Bandcamp), nameof(ArtistRowViewModel.SoundCloud),
        nameof(ArtistRowViewModel.YouTube), nameof(ArtistRowViewModel.Groups), nameof(ArtistRowViewModel.YoutubeLookup),
        nameof(ArtistRowViewModel.Remove), nameof(ArtistRowViewModel.KeepSeparate),
    };

    // ---------- Find missing links ----------

    [ObservableProperty] private bool _filling;
    [ObservableProperty] private string _fillStatus = "";
    CancellationTokenSource? _fillStop;

    bool Filtered => FilterText.Trim().Length > 0 || !string.IsNullOrEmpty(Show?.Value);
    public string FillLabel => Filling ? "Stop" : Filtered ? "Find missing links (shown rows)" : "Find missing links";

    /// <summary>The cells Find missing links may fill in a row: the name, and the Bandcamp and SoundCloud
    /// cells while those sites are on; empty, and not cleared on purpose unless Rescan is ticked.</summary>
    List<string> Open(ArtistRowViewModel r)
    {
        var cols = new List<string> { "name" };
        if (BandcampOn) cols.Add("bandcamp");
        if (SoundCloudOn) cols.Add("soundcloud");
        return cols.Where(c => (c == "name" ? r.Name : r.Cell(c)).Trim().Length == 0 && (r.Rescan || !r.Skip.Contains(c))).ToList();
    }

    partial void OnFillingChanged(bool value) => OnPropertyChanged(nameof(FillLabel));

    /// <summary>What Find missing links looks for: both sites, or one.</summary>
    public List<Option> FillSiteOptions { get; } = new()
    {
        new("", "Bandcamp and SoundCloud"), new("bandcamp", "Bandcamp only"), new("soundcloud", "SoundCloud only"),
    };
    [ObservableProperty] private Option? _fillSite;
    /// <summary>Leave out the sites a row has already been searched for (unless its Rescan is ticked).</summary>
    [ObservableProperty] private bool _skipSearched = true;
    bool _fillPrefsLoaded;

    void LoadFillPrefs()
    {
        var p = Html.ParseJson(Engine.Db.KvGet("desktop_find")) as JsonObject;
        var site = Html.AsString(p?["sites"]) ?? "";
        FillSite = FillSiteOptions.FirstOrDefault(o => o.Value == site) ?? FillSiteOptions[0];
        SkipSearched = p?["skipSearched"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : true;
        _fillPrefsLoaded = true;
    }

    void SaveFillPrefs()
    {
        if (!_fillPrefsLoaded) return;
        Engine.Db.KvSet("desktop_find", new JsonObject { ["sites"] = FillSite?.Value ?? "", ["skipSearched"] = SkipSearched }.ToJsonString());
    }

    partial void OnFillSiteChanged(Option? value) { SaveFillPrefs(); foreach (var r in Rows) r.OpenChanged(); }
    partial void OnSkipSearchedChanged(bool value) => SaveFillPrefs();

    /// <summary>The cells this run looks for in a row: its open cells, on the chosen site, and (unless it's
    /// a single row's Find links, Rescan is ticked or Skip rows already searched is off) not searched before.</summary>
    List<string> Wanted(ArtistRowViewModel r, bool single)
    {
        var site = FillSite?.Value ?? "";
        return Open(r).Where(c => c == "name" || site.Length == 0 || c == site)
            .Where(c => c == "name" || single || r.Rescan || !SkipSearched || !r.Searched.Contains(c)).ToList();
    }

    /// <summary>Every row with something missing; while the table is filtered, only the rows shown.</summary>
    [RelayCommand]
    Task FindMissing()
    {
        if (Filling) { _fillStop?.Cancel(); return Task.CompletedTask; }
        return FillAsync((Filtered ? Visible.ToList() : Rows.ToList()), Filtered ? "in the rows shown" : "", false);
    }

    /// <summary>One row's own Find links.</summary>
    [RelayCommand]
    Task FindRow(ArtistRowViewModel row) => Filling ? Task.CompletedTask : FillAsync(new() { row }, "in that row", true);

    async Task FillAsync(List<ArtistRowViewModel> pool, string where, bool single)
    {
        var live = pool.Where(r => !r.IsBlank && !r.Remove).ToList();
        var todo = live.Where(r => Wanted(r, single).Count > 0).ToList();
        if (todo.Count == 0)
        {
            var allSearched = !single && SkipSearched && live.Any(r => Wanted(r, true).Count > 0);
            FillStatus = $"Nothing to look for{(where.Length > 0 ? " " + where : "")}. " + (allSearched
                ? "Every row with a missing link has been searched before: untick Skip rows already searched, or tick Rescan on a row, to search again."
                : "Cells you cleared are left alone; tick Rescan on a row to search it again.");
            return;
        }
        Filling = true;
        Error = "";
        _fillStop = new CancellationTokenSource();
        var ct = _fillStop.Token;
        int filled = 0, n = 0;
        bool stopped = false;
        try
        {
            foreach (var row in todo)
            {
                if (ct.IsCancellationRequested) { stopped = true; break; }
                FillStatus = $"Looking for missing links: {++n} of {todo.Count} ({(row.Name.Trim().Length > 0 ? row.Name : row.Bandcamp + row.SoundCloud)})…";
                row.Searching = true;
                var cells = new Dictionary<string, string> { ["bandcamp"] = row.Bandcamp, ["soundcloud"] = row.SoundCloud };
                var wanted = Wanted(row, single);
                var skip = (row.Rescan ? new List<string>() : row.Skip.ToList())
                    .Concat(new[] { "bandcamp", "soundcloud" }.Where(c => !wanted.Contains(c))).Distinct().ToList();
                var avoid = row.Rejected.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
                var name = row.Name;
                Suggestion found;
                try
                {
                    found = await Task.Run(() => Engine.Suggester.SuggestAsync(name, cells, skip, avoid, ct), ct);
                }
                catch (OperationCanceledException) { stopped = true; break; }
                catch (BlockedException e) { Error = e.Message; break; }
                catch (Exception e) { Error = $"Couldn't look that up ({e.Message})."; break; }
                finally { row.Searching = false; }
                row.Searched = row.Searched.Union(found.Searched).ToList();
                var open = Open(row).Where(wanted.Contains).ToList();   // never over something typed meanwhile, or a cleared cell
                row.Filling = true;
                if (found.Name.Length > 0 && open.Contains("name")) { row.Name = found.Name; row.AutoName = true; filled++; }
                foreach (var (col, value) in found.Cells)
                {
                    if (value.Length == 0 || !open.Contains(col)) continue;
                    row.SetCell(col, value);
                    if (col == "bandcamp") row.AutoBandcamp = true; else row.AutoSoundCloud = true;
                    row.Skip.Remove(col);
                    filled++;
                }
                row.Filling = false;
                row.Rescan = false;
            }
        }
        finally
        {
            Filling = false;
            _fillStop.Dispose();
            _fillStop = null;
            foreach (var r in todo) r.Searching = false;
        }
        if (filled > 0) Dirty = true;
        FillStatus = (stopped ? "Stopped. " : "") + (filled > 0 ? $"Filled {filled} cell{(filled > 1 ? "s" : "")}. Check the highlighted ones, then Save changes." : "Nothing new found.");
    }

    void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || !Edits.Contains(e.PropertyName ?? "")) return;
        Dirty = true;
        if (e.PropertyName is nameof(ArtistRowViewModel.Remove) or nameof(ArtistRowViewModel.Groups)) RefreshShowCounts();
    }

    readonly (string Key, string Label, Func<ArtistRowViewModel, bool> Test)[] _shows =
    {
        ("", "All rows", _ => true),
        ("issue", "Needs attention", r => r.HasIssue),
        ("checking", "Still being checked", r => r.IsChecking),
        ("empty", "No posts yet", r => r.Id != null && r.PostCount == 0),
        ("new", "New, not saved yet", r => r.Id == null),
        ("youtube", "Ticked for YouTube search", r => r.YoutubeLookup),
        ("removed", "Marked for removal", r => r.Remove),
    };

    /// <summary>The row filters: the fixed ones, with one for each workspace before "Marked for removal".</summary>
    List<(string Key, string Label, Func<ArtistRowViewModel, bool> Test)> Shows() =>
        _shows.Take(_shows.Length - 1)
            .Concat(Spaces.Select(w => ("ws:" + w.Id, $"In {w.Name}", (Func<ArtistRowViewModel, bool>)(r => r.InGroup(w.Id)))))
            .Append(_shows[^1]).ToList();

    void RefreshShowCounts()
    {
        var keep = Show?.Value ?? "";
        var counted = Rows.Where(r => !r.IsBlank).ToList();
        _loading = true;
        ShowOptions.Clear();
        foreach (var (key, label, test) in Shows().Where(s => s.Key != "youtube" || YouTubeOn))
            ShowOptions.Add(new Option(key, $"{label} ({counted.Count(test)})"));
        foreach (var w in Spaces) w.Count = counted.Count(r => !r.Remove && r.InGroup(w.Id));
        Show = ShowOptions.FirstOrDefault(o => o.Value == keep) ?? ShowOptions[0];
        _loading = false;
    }

    static readonly StringComparer NameOrder = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture,
        System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace);

    // ---------- sorting: click a heading to sort by it; again to reverse; a third time to go back ----------

    [ObservableProperty] private string _sortKey = "";
    [ObservableProperty] private bool _sortDesc;

    public void SortBy(string key)
    {
        if (SortKey != key) (SortKey, SortDesc) = (key, false);
        else if (!SortDesc) SortDesc = true;
        else (SortKey, SortDesc) = ("", false);
        RefreshView();
    }

    string SortText(ArtistRowViewModel r) => SortKey switch
    {
        "name" => r.Name.Trim(), "bandcamp" => r.Bandcamp.Trim(), "soundcloud" => r.SoundCloud.Trim(), "youtube" => r.YouTube.Trim(), _ => "",
    };

    /// <summary>The rows in the chosen order: problems first then fewest posts for Status, empty cells last either way,
    /// and rows not yet typed into always at the bottom.</summary>
    IEnumerable<ArtistRowViewModel> Sorted(IEnumerable<ArtistRowViewModel> rows)
    {
        var list = rows.ToList();
        var blanks = list.Where(r => r.IsBlank).ToList();
        var rest = list.Where(r => !r.IsBlank).ToList();
        if (SortKey.Length == 0) return rest.Concat(blanks);
        int dir = SortDesc ? -1 : 1;
        rest.Sort((a, b) =>
        {
            int c;
            if (SortKey == "state")
            {
                (int, long) Rank(ArtistRowViewModel r) => (r.HasIssue ? 0 : r.IsChecking ? 1 : 2, r.IsNew ? -1 : r.PostCount);
                c = Rank(a).CompareTo(Rank(b)) * dir;
            }
            else
            {
                string x = SortText(a), y = SortText(b);
                if ((x.Length == 0) != (y.Length == 0)) return x.Length == 0 ? 1 : -1;      // empty cells last
                c = NameOrder.Compare(x, y) * dir;
            }
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });
        return rest.Concat(blanks);
    }

    public void RefreshView()
    {
        RefreshShowCounts();
        Visible.Clear();
        var words = FilterText.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var test = Shows().FirstOrDefault(s => s.Key == (Show?.Value ?? "")).Test ?? (_ => true);
        foreach (var r in Sorted(Rows))
        {
            if (!r.IsBlank)
            {
                var text = string.Join(" ", r.Name, r.Bandcamp, r.SoundCloud, r.YouTube).ToLowerInvariant();
                if (!words.All(text.Contains) || !test(r)) continue;
            }
            Visible.Add(r);
        }
    }

    partial void OnFilterTextChanged(string value)
    {
        RefreshView();
        OnPropertyChanged(nameof(FillLabel));
    }
    partial void OnShowChanged(Option? value)
    {
        if (!_loading) RefreshView();
        OnPropertyChanged(nameof(FillLabel));
    }

    [RelayCommand]
    void AddRow()
    {
        if (Rows.Count > 0 && Rows[^1].IsBlank && Rows[^1].IsNew) return;
        var row = new ArtistRowViewModel();
        row.SetSpaces(Spaces);
        Add(row);
        RefreshView();
    }

    [RelayCommand]
    void ToggleRemove(ArtistRowViewModel row) => row.Remove = !row.Remove;

    [RelayCommand]
    async Task CheckNow(ArtistRowViewModel row)
    {
        if (row.Id == null) return;
        row.Checking = true;
        try
        {
            await Task.Run(() => Engine.CheckArtistAsync(row.Id.Value));
            var fresh = Engine.Table.Rows().FirstOrDefault(r => r.Id == row.Id);
            if (fresh != null && !Dirty)
            {
                Load();
                _main.SitesOrArtistsChanged();
            }
            else if (fresh != null)
            {
                Note = $"Checked {fresh.Name}: {fresh.PostCount} posts" + (fresh.Problems.Count > 0 ? " · " + string.Join("; ", fresh.Problems) : "") + ".";
                _main.SitesOrArtistsChanged();
            }
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            row.Checking = false;
        }
    }

    [RelayCommand]
    async Task ClearAll()
    {
        var n = Rows.Count(r => !r.IsBlank);
        if (n == 0 || Ui == null) return;
        if (!await Ui.ConfirmAsync("Clear All", $"Are you sure? This empties all {n} row{(n > 1 ? "s" : "")} of the table. Nothing is removed " +
                                                "from the feed until you press Save changes, and Discard brings the rows back.", "Empty the table")) return;
        foreach (var r in Rows) r.PropertyChanged -= RowChanged;
        Rows.Clear();
        Dirty = true;
        RefreshView();
    }

    [RelayCommand]
    async Task Discard()
    {
        if (Ui != null && !await Ui.ConfirmAsync("Discard", "Discard unsaved changes?", "Discard")) return;
        Load();
    }

    List<ArtistRowViewModel> Trimmed()
    {
        var list = Rows.ToList();
        while (list.Count > 0 && list[^1].IsBlank) list.RemoveAt(list.Count - 1);
        return list;
    }

    [RelayCommand]
    async Task Save()
    {
        var list = Trimmed();
        var staying = list.Where(r => !r.IsBlank && !r.Remove).Select(r => r.Id).ToHashSet();
        var removed = _savedIds.Count(id => !staying.Contains(id));
        if (removed > 0 && Ui != null && !await Ui.ConfirmAsync("Stop tracking",
                $"This stops tracking {removed} artist{(removed > 1 ? "s" : "")} and removes their posts from the feed. Continue?", "Continue"))
            return;
        Error = "";
        foreach (var r in Rows)
        {
            r.Errors = "";
        }
        var youtubeOn = YouTubeOn;
        var rows = list.Select(r =>
        {
            var row = new TableRow
            {
                Id = r.Id, Name = r.Name, Groups = r.Groups, Remove = r.Remove, KeepSeparate = r.KeepSeparate,
                Skip = r.Skip.Where(c => c != "name").ToList(), Rejected = r.Rejected,
                YoutubeLookup = youtubeOn ? r.YoutubeLookup : null,
            };
            foreach (var s in Engine.Sites.Enabled()) row.Cells[s] = r.Cell(s);
            return row;
        }).ToList();
        Saving = true;
        try
        {
            var added = Engine.Table.Save(rows, Spaces.Select(w => (w.Id, w.Name)).ToList());
            Load();
            _main.SitesOrArtistsChanged();
            if (added.Count > 0)
            {
                Note = $"Saved. Reading {added.Count} new page{(added.Count > 1 ? "s" : "")}…";
                _ = Task.Run(async () =>
                {
                    try { await Engine.Refresher.RefreshAsync(added); }
                    catch (Exception) { }
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (!Dirty) Load();
                            Note = "";
                            _main.SitesOrArtistsChanged();
                        }
                        catch (InvalidOperationException) { }        // the database has closed: the app is shutting down
                    });
                });
            }
            else Note = "Saved.";
        }
        catch (TableSaveException e)
        {
            Error = e.Message;
            foreach (var err in e.Errors)
            {
                if (err.Row < 0 || err.Row >= list.Count) { Error += " " + err.Message; continue; }
                var r = list[err.Row];
                r.Errors = r.Errors.Length > 0 ? r.Errors + "\n" + err.Message : err.Message;
                if (err.Kind == "same_name") r.SameName = true;
            }
            RefreshView();
        }
        finally
        {
            Saving = false;
        }
    }

    [RelayCommand]
    async Task ImportCsv()
    {
        if (Ui == null) return;
        var text = await Ui.OpenTextFileAsync("Import a CSV of artists");
        if (text == null) return;
        var rows = TableImport.FromCsv(text, Engine.Sites.All.Where(s => Engine.Sites.IsOn(s.Key)).ToList());
        if (rows.Count == 0) { Note = "Nothing to import."; return; }
        AppendRows(rows);
        Note = $"Imported {rows.Count} row{(rows.Count > 1 ? "s" : "")}. Check them, then Save changes.";
    }

    [RelayCommand]
    async Task ExportCsv()
    {
        if (Ui == null) return;
        await Ui.SaveFileAsync("Export Tracked Artists", "artists.csv", Engine.Table.ExportCsv());
    }

    [RelayCommand]
    void AddPasted()
    {
        var rows = TableImport.FromPaste(PasteText, Engine.Sites.All.Where(s => Engine.Sites.IsOn(s.Key)).ToList());
        if (rows.Count > 0) PasteText = "";
        AppendRows(rows);
        Note = rows.Count > 0 ? $"Added {rows.Count} row{(rows.Count > 1 ? "s" : "")}. Check them, then Save changes." : "Nothing to add.";
    }

    void AppendRows(List<TableRow> rows)
    {
        while (Rows.Count > 0 && Rows[^1].IsBlank)
        {
            Rows[^1].PropertyChanged -= RowChanged;
            Rows.RemoveAt(Rows.Count - 1);
        }
        foreach (var r in rows) Add(ArtistRowViewModel.From(r, Spaces));
        if (rows.Count > 0) Dirty = true;
        RefreshView();
    }

    // ---------- workspaces (kept with the table, on Save) ----------

    [RelayCommand]
    void ToggleSpaces() => ShowSpaces = !ShowSpaces;

    void SpacesChanged()
    {
        foreach (var r in Rows) r.SetSpaces(Spaces);
        Dirty = true;
        RefreshView();
        OnPropertyChanged(nameof(SpaceChoices));
        SpacesEdited?.Invoke();
    }

    /// <summary>Raised when workspaces are added, removed or moved, so the window can redraw its columns.</summary>
    public event Action? SpacesEdited;

    [RelayCommand]
    void AddSpace()
    {
        int n = Spaces.Count + 1;
        var name = "New workspace";
        while (Spaces.Any(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase))) name = $"New workspace {++n}";
        var w = new WorkspaceDraft("w" + DateTime.UtcNow.Ticks.ToString("x"), name);
        w.PropertyChanged += SpaceRenamed;
        Spaces.Add(w);
        SpacesChanged();
    }

    void SpaceRenamed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkspaceDraft.Name)) return;
        Dirty = true;
        RefreshShowCounts();
        OnPropertyChanged(nameof(SpaceChoices));
    }

    [RelayCommand]
    void RemoveSpace(WorkspaceDraft w)
    {
        w.PropertyChanged -= SpaceRenamed;
        Spaces.Remove(w);
        SpacesChanged();
    }

    [RelayCommand]
    void MoveSpaceUp(WorkspaceDraft w)
    {
        var i = Spaces.IndexOf(w);
        if (i <= 0) return;
        Spaces.Move(i, i - 1);
        SpacesChanged();
    }

    [RelayCommand]
    void MoveSpaceDown(WorkspaceDraft w)
    {
        var i = Spaces.IndexOf(w);
        if (i < 0 || i >= Spaces.Count - 1) return;
        Spaces.Move(i, i + 1);
        SpacesChanged();
    }

    // ---------- Import follows: the artists and labels an account follows ----------

    public List<Option> FollowSites { get; } = new() { new("soundcloud", "SoundCloud"), new("bandcamp", "Bandcamp") };
    public ObservableCollection<FollowItem> FollowItems { get; } = new();
    /// <summary>"No workspace", then each workspace, for putting imported artists in one.</summary>
    public List<Option> SpaceChoices => new[] { new Option("", "no workspace") }.Concat(Spaces.Select(w => new Option(w.Id, w.Name))).ToList();

    [ObservableProperty] private bool _showFollows;
    [ObservableProperty] private Option? _followSite;
    [ObservableProperty] private string _followUser = "";
    [ObservableProperty] private string _followStatus = "";
    [ObservableProperty] private string _followCount = "";
    [ObservableProperty] private bool _followAll;
    [ObservableProperty] private bool _reading;
    [ObservableProperty] private Option? _followSpace;
    string _followsFrom = "";
    bool _counting;

    public bool HasFollows => FollowItems.Count > 0;

    [RelayCommand]
    void ToggleFollows()
    {
        ShowFollows = !ShowFollows;
        if (!ShowFollows) return;
        FollowSite ??= FollowSites[0];
        FollowSpace = SpaceChoices.FirstOrDefault(o => o.Value == FollowSpace?.Value) ?? SpaceChoices[0];
        if (FollowUser.Trim().Length == 0) FollowUser = Engine.Follows.Defaults().GetValueOrDefault(FollowSite.Value) ?? "";
    }

    partial void OnFollowSiteChanged(Option? oldValue, Option? newValue)
    {
        var defaults = Engine.Follows.Defaults();
        if (newValue != null && (FollowUser.Trim().Length == 0 || defaults.Values.Contains(FollowUser.Trim())))
            FollowUser = defaults.GetValueOrDefault(newValue.Value) ?? "";
    }

    bool InTable(string site, string handle)
    {
        var shortName = Engine.Sites.Get(site)?.Short(handle) ?? handle;
        var want = new[] { handle.ToLowerInvariant(), shortName.ToLowerInvariant() };
        return Rows.Any(r => !r.Remove && r.Cell(site).Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Regex.Replace(p.Trim().ToLowerInvariant(), @"^https?://|/+$|^(www\.|m\.)?soundcloud\.com/", ""))
            .Any(want.Contains));
    }

    [RelayCommand]
    async Task ReadFollows()
    {
        var site = FollowSite?.Value ?? "soundcloud";
        var user = FollowUser.Trim();
        if (user.Length == 0) { FollowStatus = "Enter a username first."; return; }
        Reading = true;
        FollowStatus = $"Reading who {user} follows on {FollowSite?.Text}…";
        try
        {
            var found = await Task.Run(() => Engine.Follows.ReadAsync(site, user));
            ClearFollows();
            _followsFrom = site;
            foreach (var f in found)
            {
                var item = new FollowItem(f, !f.Tracked && InTable(site, f.Handle));
                item.PropertyChanged += FollowPicked;
                FollowItems.Add(item);
            }
            var fresh = FollowItems.Count(i => i.Open);
            FollowStatus = found.Count > 0 ? $"{user} follows {found.Count}; {fresh} not tracked yet."
                : $"{user} doesn't follow anyone (or the list is private).";
        }
        catch (Exception e)
        {
            ClearFollows();
            FollowStatus = e is FollowsException or SourceException ? e.Message : $"Couldn't read those follows ({e.GetType().Name}).";
        }
        finally
        {
            Reading = false;
            CountFollows();
        }
    }

    void ClearFollows()
    {
        foreach (var i in FollowItems) i.PropertyChanged -= FollowPicked;
        FollowItems.Clear();
    }

    void FollowPicked(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FollowItem.Pick)) CountFollows();
    }

    void CountFollows()
    {
        var open = FollowItems.Where(i => i.Open).ToList();
        var picked = open.Count(i => i.Pick);
        FollowCount = $"{picked} of {open.Count} picked" + (open.Count < FollowItems.Count ? $" · {FollowItems.Count - open.Count} already tracked or in the table" : "");
        _counting = true;
        FollowAll = open.Count > 0 && picked == open.Count;
        _counting = false;
        OnPropertyChanged(nameof(HasFollows));
        AddFollowsCommand.NotifyCanExecuteChanged();
    }

    partial void OnFollowAllChanged(bool value)
    {
        if (_counting) return;
        foreach (var i in FollowItems.Where(i => i.Open)) i.Pick = value;
    }

    bool CanAddFollows() => FollowItems.Any(i => i.Open && i.Pick);

    [RelayCommand(CanExecute = nameof(CanAddFollows))]
    void AddFollows()
    {
        var site = _followsFrom;
        if (!Engine.Sites.IsOn(site)) { FollowStatus = $"{Engine.Sites.Get(site)?.Label ?? site} is switched off in Settings."; return; }
        while (Rows.Count > 0 && Rows[^1].IsBlank)
        {
            Rows[^1].PropertyChanged -= RowChanged;
            Rows.RemoveAt(Rows.Count - 1);
        }
        var space = FollowSpace?.Value ?? "";
        int added = 0, joined = 0;
        foreach (var item in FollowItems.Where(i => i.Open && i.Pick).ToList())
        {
            var f = item.Followed;
            var name = Names.Norm(f.Name);
            var page = Engine.Sites.Get(site)?.Short(f.Handle) ?? f.Handle;
            // A row for someone of the same name takes the page; otherwise a new row.
            var row = name.Length == 0 ? null : Rows.FirstOrDefault(r => !r.Remove && Names.SplitNames(r.Name).Any(n => Names.Norm(n) == name));
            if (row != null)
            {
                var have = row.Cell(site).Trim();
                row.SetCell(site, have.Length > 0 ? $"{have}, {page}" : page);
                joined++;
            }
            else
            {
                row = new ArtistRowViewModel { Name = f.Name };
                row.SetSpaces(Spaces);
                row.SetCell(site, page);
                Add(row);
                added++;
            }
            if (space.Length > 0)
                foreach (var t in row.Ticks.Where(t => t.Space.Id == space)) t.IsIn = true;
            item.InTable = true;
        }
        if (added + joined == 0) return;
        Dirty = true;
        RefreshView();
        CountFollows();
        Note = $"Added {added} row{(added == 1 ? "" : "s")}" + (joined > 0 ? $" and {joined} page{(joined == 1 ? "" : "s")} to rows with the same name" : "") +
               ". Check them, then Save changes.";
    }
}
