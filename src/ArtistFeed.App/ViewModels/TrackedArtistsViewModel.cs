using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ArtistFeed.Core;

namespace ArtistFeed.App.ViewModels;

/// <summary>One editable row of Tracked Artists.</summary>
public partial class ArtistRowViewModel : ObservableObject
{
    public long? Id { get; init; }
    public long PostCount { get; init; }
    public List<string> Skip { get; init; } = new();
    public Dictionary<string, List<string>> Rejected { get; init; } = new();
    public List<string> Problems { get; init; } = new();
    public int Order { get; set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _bandcamp = "";
    [ObservableProperty] private string _soundCloud = "";
    [ObservableProperty] private string _youTube = "";
    [ObservableProperty] private bool _focus;
    [ObservableProperty] private bool _youtubeLookup;
    [ObservableProperty] private bool _remove;
    [ObservableProperty] private bool _keepSeparate;
    [ObservableProperty] private bool _sameName;          // the Keep both box shows while two rows share a name
    [ObservableProperty] private string _errors = "";
    [ObservableProperty] private bool _checking;

    public bool IsNew => Id == null;
    public bool IsBlank => Name.Trim().Length == 0 && Bandcamp.Trim().Length == 0 && SoundCloud.Trim().Length == 0 && YouTube.Trim().Length == 0;
    public bool HasIssue => Errors.Length > 0 || Problems.Any(p => !p.EndsWith("checking…"));
    public bool IsChecking => Problems.Any(p => p.EndsWith("checking…"));

    public string StatusText => IsNew ? "Not saved yet"
        : Problems.Count > 0 ? string.Join("\n", Problems)
        : PostCount == 1 ? "1 post" : $"{PostCount} posts";

    public string StatusShort => IsNew ? "new"
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

    public static ArtistRowViewModel From(TableRow r) => new()
    {
        Id = r.Id, PostCount = r.PostCount, Skip = r.Skip, Rejected = r.Rejected, Problems = r.Problems,
        Name = r.Name, Bandcamp = r.Cells.GetValueOrDefault("bandcamp") ?? "", SoundCloud = r.Cells.GetValueOrDefault("soundcloud") ?? "",
        YouTube = r.Cells.GetValueOrDefault("youtube") ?? "", Focus = r.Focus, YoutubeLookup = r.YoutubeLookup == true, SameName = r.SameName,
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
        Load();
    }

    public void Load()
    {
        _loading = true;
        foreach (var r in Rows) r.PropertyChanged -= RowChanged;
        Rows.Clear();
        var rows = Engine.Table.Rows();
        // Problems first, then fewest posts, so what needs attention is on top.
        var ordered = rows.Select(ArtistRowViewModel.From)
            .OrderBy(r => r.HasIssue ? 0 : r.IsChecking ? 1 : 2).ThenBy(r => r.PostCount).ToList();
        foreach (var r in ordered) Add(r);
        _savedIds = rows.Select(r => r.Id).ToList();
        Error = "";
        Note = "";
        _loading = false;
        Dirty = false;
        RefreshView();
        OnPropertyChanged(nameof(BandcampOn));
        OnPropertyChanged(nameof(SoundCloudOn));
        OnPropertyChanged(nameof(YouTubeOn));
    }

    void Add(ArtistRowViewModel r)
    {
        r.Order = Rows.Count;
        r.PropertyChanged += RowChanged;
        Rows.Add(r);
    }

    static readonly HashSet<string> Edits = new()
    {
        nameof(ArtistRowViewModel.Name), nameof(ArtistRowViewModel.Bandcamp), nameof(ArtistRowViewModel.SoundCloud),
        nameof(ArtistRowViewModel.YouTube), nameof(ArtistRowViewModel.Focus), nameof(ArtistRowViewModel.YoutubeLookup),
        nameof(ArtistRowViewModel.Remove), nameof(ArtistRowViewModel.KeepSeparate),
    };

    void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || !Edits.Contains(e.PropertyName ?? "")) return;
        Dirty = true;
        if (e.PropertyName == nameof(ArtistRowViewModel.Remove)) RefreshShowCounts();
    }

    readonly (string Key, string Label, Func<ArtistRowViewModel, bool> Test)[] _shows =
    {
        ("", "All rows", _ => true),
        ("issue", "Needs attention", r => r.HasIssue),
        ("checking", "Still being checked", r => r.IsChecking),
        ("empty", "No posts yet", r => r.Id != null && r.PostCount == 0),
        ("new", "New, not saved yet", r => r.Id == null),
        ("youtube", "Ticked for YouTube search", r => r.YoutubeLookup),
        ("focus", "Ticked for Focus", r => r.Focus),
        ("removed", "Marked for removal", r => r.Remove),
    };

    void RefreshShowCounts()
    {
        var keep = Show?.Value ?? "";
        var counted = Rows.Where(r => !r.IsBlank).ToList();
        _loading = true;
        ShowOptions.Clear();
        foreach (var (key, label, test) in _shows.Where(s => s.Key != "youtube" || YouTubeOn))
            ShowOptions.Add(new Option(key, $"{label} ({counted.Count(test)})"));
        Show = ShowOptions.FirstOrDefault(o => o.Value == keep) ?? ShowOptions[0];
        _loading = false;
    }

    public void RefreshView()
    {
        RefreshShowCounts();
        Visible.Clear();
        var words = FilterText.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var test = _shows.First(s => s.Key == (Show?.Value ?? "")).Test;
        foreach (var r in Rows)
        {
            if (!r.IsBlank)
            {
                var text = string.Join(" ", r.Name, r.Bandcamp, r.SoundCloud, r.YouTube).ToLowerInvariant();
                if (!words.All(text.Contains) || !test(r)) continue;
            }
            Visible.Add(r);
        }
    }

    partial void OnFilterTextChanged(string value) => RefreshView();
    partial void OnShowChanged(Option? value)
    {
        if (!_loading) RefreshView();
    }

    [RelayCommand]
    void AddRow()
    {
        if (Rows.Count > 0 && Rows[^1].IsBlank && Rows[^1].IsNew) return;
        Add(new ArtistRowViewModel());
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
                Id = r.Id, Name = r.Name, Focus = r.Focus, Remove = r.Remove, KeepSeparate = r.KeepSeparate,
                Skip = r.Skip.Where(c => c != "name").ToList(), Rejected = r.Rejected,
                YoutubeLookup = youtubeOn ? r.YoutubeLookup : null,
            };
            foreach (var s in Engine.Sites.Enabled()) row.Cells[s] = r.Cell(s);
            return row;
        }).ToList();
        Saving = true;
        try
        {
            var added = Engine.Table.Save(rows);
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
                        if (!Dirty) Load();
                        Note = "";
                        _main.SitesOrArtistsChanged();
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
        foreach (var r in rows) Add(ArtistRowViewModel.From(r));
        if (rows.Count > 0) Dirty = true;
        RefreshView();
    }
}
