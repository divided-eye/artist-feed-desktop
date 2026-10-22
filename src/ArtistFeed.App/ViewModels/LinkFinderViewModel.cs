using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ArtistFeed.Core;

namespace ArtistFeed.App.ViewModels;

/// <summary>How a result is labelled: the web version's LOOKUP_TAGS.</summary>
public static class LookupTags
{
    static readonly Dictionary<string, (string Kind, string Label)> Tags = new()
    {
        ["imported"] = ("mine", "From your list"), ["manual"] = ("mine", "Link you set"), ["bandcamp"] = ("ok", "Bandcamp release"),
        ["bandcamp_check"] = ("check", "Bandcamp · check match"), ["bandcamp_artist"] = ("page", "Bandcamp artist page"),
        ["soundcloud_track"] = ("ok", "SoundCloud track"), ["soundcloud"] = ("sc", "SoundCloud artist page"),
        ["soundcloud_search"] = ("check", "SoundCloud search"), ["youtube"] = ("ok", "YouTube video"), ["youtube_channel"] = ("page", "YouTube channel"),
        ["beatport"] = ("ok", "Beatport track"), ["beatport_check"] = ("check", "Beatport · check match"), ["discogs"] = ("ok", "Discogs release"),
        ["none"] = ("bad", "Not found"), ["error"] = ("bad", "Error"), ["pending"] = ("wait", "Waiting to be searched"), ["skipped"] = ("page", "Skipped"),
    };

    // Tinted labels that read on dark and light backgrounds alike.
    static readonly Dictionary<string, Color> Ink = new()
    {
        ["ok"] = Color.Parse("#3aa876"), ["check"] = Color.Parse("#c98a12"), ["bad"] = Color.Parse("#d65454"), ["page"] = Color.Parse("#7a8aa0"),
        ["sc"] = Color.Parse("#e0702a"), ["mine"] = Color.Parse("#8f6fe0"), ["wait"] = Color.Parse("#7a8aa0"), ["review"] = Color.Parse("#c98a12"),
    };

    public static (string Kind, string Label) Of(string status, string fallback = "") =>
        Tags.TryGetValue(status, out var t) ? t : ("wait", fallback.Length > 0 ? fallback : status);

    public static IBrush Fore(string kind) => new SolidColorBrush(Ink.GetValueOrDefault(kind, Ink["page"]));
    public static IBrush Back(string kind)
    {
        var c = Ink.GetValueOrDefault(kind, Ink["page"]);
        return new SolidColorBrush(Color.FromArgb(0x2e, c.R, c.G, c.B));
    }

    public static string Bare(string url) => url.StartsWith("https://") ? url[8..] : url.StartsWith("http://") ? url[7..] : url;
}

/// <summary>One row of a lookup, redrawn as the lookup fills in.</summary>
public partial class LookupRowViewModel(int n, string artist, string title) : ObservableObject
{
    public int N { get; } = n;
    public string Artist { get; } = artist;
    public string Title { get; } = title;
    public string Status { get; private set; } = "pending";

    [ObservableProperty] private string _speltArtist = "";
    [ObservableProperty] private string _speltTitle = "";
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _found = "";
    [ObservableProperty] private string _release = "";
    [ObservableProperty] private string _year = "";
    [ObservableProperty] private string _tag = "";
    [ObservableProperty] private IBrush _tagFore = LookupTags.Fore("wait");
    [ObservableProperty] private IBrush _tagBack = LookupTags.Back("wait");
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _review = "";
    [ObservableProperty] private string _steps = "";
    [ObservableProperty] private bool _showSteps;
    [ObservableProperty] private bool _waiting;
    [ObservableProperty] private bool _now;
    [ObservableProperty] private bool _skipping;
    [ObservableProperty] private bool _done;
    [ObservableProperty] private bool _canSearchAgain;

    // the link editor, open in place of the link
    [ObservableProperty] private bool _editing;
    [ObservableProperty] private string _editUrl = "";
    [ObservableProperty] private string _editYear = "";
    [ObservableProperty] private string _editError = "";

    public bool HasUrl => Url.Length > 0;
    public string LinkText => LookupTags.Bare(Url);
    public bool HasSpeltArtist => SpeltArtist.Length > 0;
    public bool HasSpeltTitle => SpeltTitle.Length > 0;
    public bool HasRelease => Release.Length > 0;
    public string ReleaseText => Release.Length > 0 ? "on " + Release : "";
    public bool HasNote => Note.Length > 0;
    public bool HasReview => Review.Length > 0 && !Waiting;
    public string ReviewText => Review.Length > 0 ? Review + "." : "";
    public bool HasSteps => Steps.Length > 0;
    public bool CanLooksRight => Review.Length > 0 && Url.Length > 0 && Done;
    public string EditLabel => Url.Length > 0 ? "Edit" : "Add link";
    public bool IsManual => Status == "manual";
    public bool ShowLink => !Editing;
    public bool CanSkip => Waiting && !Skipping;
    public string SkipTip => Now ? "Stop searching for this track and move on" : "Don't search for this track";

    partial void OnUrlChanged(string value) { OnPropertyChanged(nameof(HasUrl)); OnPropertyChanged(nameof(LinkText)); OnPropertyChanged(nameof(CanLooksRight)); OnPropertyChanged(nameof(EditLabel)); }
    partial void OnSpeltArtistChanged(string value) => OnPropertyChanged(nameof(HasSpeltArtist));
    partial void OnSpeltTitleChanged(string value) => OnPropertyChanged(nameof(HasSpeltTitle));
    partial void OnReleaseChanged(string value) { OnPropertyChanged(nameof(HasRelease)); OnPropertyChanged(nameof(ReleaseText)); }
    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));
    partial void OnReviewChanged(string value) { OnPropertyChanged(nameof(HasReview)); OnPropertyChanged(nameof(ReviewText)); OnPropertyChanged(nameof(CanLooksRight)); }
    partial void OnWaitingChanged(bool value) { OnPropertyChanged(nameof(HasReview)); OnPropertyChanged(nameof(CanSkip)); }
    partial void OnSkippingChanged(bool value) => OnPropertyChanged(nameof(CanSkip));
    partial void OnNowChanged(bool value) => OnPropertyChanged(nameof(SkipTip));
    partial void OnDoneChanged(bool value) => OnPropertyChanged(nameof(CanLooksRight));
    partial void OnStepsChanged(string value) => OnPropertyChanged(nameof(HasSteps));
    partial void OnEditingChanged(bool value) => OnPropertyChanged(nameof(ShowLink));

    public void Update(JsonObject r, LookupJob job)
    {
        Status = r.T("status");
        OnPropertyChanged(nameof(IsManual));
        var waiting = !job.Finished && Status == "pending";
        var now = waiting && job.Current == N;
        Waiting = waiting;
        Now = now;
        Skipping = waiting && job.Skip.Contains(N);
        Done = !waiting && Status != "pending";
        CanSearchAgain = job.Finished && Status != "manual" && Status != "pending";
        SpeltArtist = r.T("list_artist") is { Length: > 0 } la && la != Artist ? la : "";
        SpeltTitle = r.T("list_title") is { Length: > 0 } lt && lt != Title ? lt : "";
        Url = r.T("url").StartsWith("http") ? r.T("url") : "";
        Found = r.T("found");
        Release = r.T("release");
        Year = r.T("year");
        var (kind, label) = LookupTags.Of(Status);
        Tag = now ? "Searching now…" : label;
        TagFore = LookupTags.Fore(kind);
        TagBack = LookupTags.Back(kind);
        Note = r.T("note");
        Review = r.T("review");
        Steps = string.Join("\n", (r["steps"] as JsonArray ?? new JsonArray()).Select((s, i) => $"{i + 1}. {s}"));
    }
}

/// <summary>One track in Saved results.</summary>
public partial class SavedRowViewModel : ObservableObject
{
    public JsonObject Row { get; }
    public SavedRowViewModel(JsonObject row)
    {
        Row = row;
        var (kind, label) = LookupTags.Of(row.T("status"), row.T("result"));
        Tag = label;
        TagFore = LookupTags.Fore(kind);
        TagBack = LookupTags.Back(kind);
    }

    public string Key => Row.T("key");
    public string TidyArtist => Row.T("tidy_artist");
    public string TidyTitle => Row.T("tidy_title") + (Row.B("version") ? $" [{Row.T("version")}]" : "");
    public string TypedArtist => Row.B("artist") && Row.T("artist").ToLowerInvariant() != Row.T("tidy_artist") ? "typed: " + Row.T("artist") : "";
    public string TypedTitle => Row.B("title") && Row.T("title").ToLowerInvariant() != Row.T("tidy_title") ? "typed: " + Row.T("title") : "";
    public bool HasTypedArtist => TypedArtist.Length > 0;
    public bool HasTypedTitle => TypedTitle.Length > 0;
    public string ListArtist => Row.B("list_artist") ? Row.T("list_artist") : "none on file";
    public string ListTitle => Row.B("list_title") ? Row.T("list_title") : "none on file";
    public bool NoListArtist => !Row.B("list_artist");
    public bool NoListTitle => !Row.B("list_title");
    public bool Guess => Row.B("list_guess");
    public string SpellLabel => Row.B("list_title") || Row.B("list_artist") ? "Edit spelling" : "Add spelling";
    public string Url => Row.T("url").StartsWith("http") ? Row.T("url") : "";
    public bool HasUrl => Url.Length > 0;
    public string LinkText => LookupTags.Bare(Url);
    public string ListNote => Row.B("list_note") ? "note: " + Row.T("list_note") : "";
    public bool HasListNote => ListNote.Length > 0;
    public string ListHad => Row.B("list_link") && Row.T("list_link") != Row.T("url") ? "your list had: " + LookupTags.Bare(Row.T("list_link")) : "";
    public bool HasListHad => ListHad.Length > 0;
    public string Year => Row.T("year");
    public string Tag { get; }
    public IBrush TagFore { get; }
    public IBrush TagBack { get; }
    public string Note => Row.T("note");
    public bool HasNote => Note.Length > 0;
    public bool HasReview => Row.B("review");
    public string ReviewText => Row.B("review") ? Row.T("review") + "." : "";
    public bool CanLooksRight => HasReview && HasUrl;
    public string EditLabel => HasUrl ? "Edit" : "Add link";
    public string Saved => Row.T("at").Length >= 10 ? Row.T("at")[..10] : Row.T("at");
    public bool CanForget => Row.B("searched") || Row.B("list_key");
    public bool CanForgetAnswer => Row.B("searched");
    public bool CanForgetEntry => Row.B("list_key");
    public bool CanForgetBoth => Row.B("searched") && Row.B("list_key");
    public string EntryLabel => Guess ? "Closest list entry" : "List entry";
    public string ListName => string.Join(" – ", new[] { Row.T("list_artist"), Row.T("list_title") }.Where(x => x.Length > 0));
    public string EntryNote => Row.B("list_key") ? "list entry: " + ListName : "";

    /// <summary>Answers saved by early versions have only the tidied names; the version is put back in a form the lookup reads.</summary>
    string OldTitle()
    {
        var v = Row.T("version");
        if (v.Length == 0) return Row.T("tidy_title");
        if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^vip\b"))
            return $"{Row.T("tidy_title")} ({(System.Text.RegularExpressions.Regex.Replace(v, @"^vip\s*", "") + " VIP").Trim()})";
        return $"{Row.T("tidy_title")} ({v} Remix)";
    }
    public string AskArtist => Row.B("artist") ? Row.T("artist") : Row.B("list_artist") ? Row.T("list_artist") : Row.T("tidy_artist");
    public string AskTitle => Row.B("title") ? Row.T("title") : Row.B("list_title") ? Row.T("list_title") : OldTitle();

    [ObservableProperty] private bool _editingLink;
    [ObservableProperty] private string _editUrl = "";
    [ObservableProperty] private bool _editingSpell;
    [ObservableProperty] private string _spellArtist = "";
    [ObservableProperty] private string _spellTitle = "";
    [ObservableProperty] private bool _forgetting;
    [ObservableProperty] private string _error = "";
    public bool ShowSpell => !EditingSpell;
    public bool ShowLink => !EditingLink;
    partial void OnEditingSpellChanged(bool value) => OnPropertyChanged(nameof(ShowSpell));
    partial void OnEditingLinkChanged(bool value) => OnPropertyChanged(nameof(ShowLink));
}

public partial class LinkFinderViewModel : ObservableObject, IDisposable
{
    readonly MainViewModel _main;
    readonly DispatcherTimer _timer;
    int _ticks;
    LinkFinder F => _main.Engine.LinkFinder;
    public IUi? Ui { get; set; }

    public LinkFinderViewModel(MainViewModel main)
    {
        _main = main;
        _fanName = F.CollectionFan();
        _downloadsOn = F.DownloadsOn();
        _fallback = FallbackOptions.First(o => o.Value == F.FallbackPref());
        _savedFilter = SavedFilterOptions[0];
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        ShowFan();
        // The lookup still running (or the latest one) when the window was closed is shown again.
        LookupJob? last;
        lock (F.Jobs) last = F.Jobs.Values.LastOrDefault();
        if (last != null) Show(last);
    }

    public void Dispose() => _timer.Stop();

    void Tick()
    {
        if (_job != null && (!_job.Finished || _wasRunning)) Redraw();
        if (++_ticks % 3 == 0 || F.Collection.Syncing) ShowFan();
    }

    // ================= Look up =================

    public string Intro =>
        "Each row needs two things: the artist and the track title. That is all a search uses; any other columns are ignored. " +
        "Paste rows copied from your spreadsheet below (artist, then title), type one Artist - Title per line, or choose an Excel (.xlsx) or CSV file. " +
        "A header row is optional. A row with no title can only return the artist's page. Each track is looked for in your feed, your Bandcamp purchases, " +
        "Bandcamp search and the artist's own Bandcamp page, then on the other sites ticked in Settings; if it isn't found you get the artist's " +
        "SoundCloud profile or Bandcamp page instead (the remixer's, for an edit, remix, bootleg or flip). Hard-to-find tracks can take up to a minute each.";

    public ObservableCollection<LookupRowViewModel> Rows { get; } = new();
    LookupJob? _job;
    bool _wasRunning;

    [ObservableProperty] private string _pasteText = "";
    [ObservableProperty] private bool _fresh;
    [ObservableProperty] private bool _retryMissing;
    [ObservableProperty] private string _lookupStatus = "";
    [ObservableProperty] private string _lookupError = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _canExport;
    [ObservableProperty] private int _tab;

    partial void OnFreshChanged(bool value) { if (value) RetryMissing = false; }
    partial void OnRetryMissingChanged(bool value) { if (value) Fresh = false; }

    [RelayCommand]
    void LookUpPasted()
    {
        if (PasteText.Trim().Length == 0)
        {
            LookupError = "Paste some rows first, or choose a file.";
            return;
        }
        Begin(System.Text.Encoding.UTF8.GetBytes(PasteText), Fresh, RetryMissing);
    }

    [RelayCommand]
    async Task ChooseFile()
    {
        if (Ui == null) return;
        var data = await Ui.OpenTableFileAsync("Choose a tracklist (Excel or CSV)");
        if (data != null) Begin(data, Fresh, RetryMissing);
    }

    void Begin(byte[] data, bool fresh, bool retryMissing)
    {
        LookupError = "";
        if (data.Length > 5_000_000)
        {
            LookupError = "That file is too large (5 MB limit).";
            return;
        }
        List<WantedTrack> tracks;
        try
        {
            tracks = Tracklist.PickTracks(Tracklist.ReadTable(data), F.MaxRows);
        }
        catch (TracklistException e)
        {
            LookupError = e.Message;
            return;
        }
        Show(F.Start(tracks, fresh, retryMissing));
    }

    void Show(LookupJob job)
    {
        _job = job;
        Rows.Clear();
        lock (job)
            foreach (var r in job.Rows) Rows.Add(new LookupRowViewModel((int)(r.N("n") ?? 0), r.T("artist"), r.T("title")));
        Redraw();
    }

    void Redraw()
    {
        if (_job == null) return;
        var job = _job;
        var view = F.View(job);
        for (int i = 0; i < view.Count && i < Rows.Count; i++)
            if (!Rows[i].Editing) Rows[i].Update(view[i], job);
        _wasRunning = !job.Finished;
        Busy = !job.Finished;
        CanExport = job.Finished;
        LookupError = job.Warning;
        if (job.Finished)
        {
            int Count(params string[] s) => view.Count(r => s.Contains(r.T("status")));
            var (bc, sc, yt, bp, dg) = (Count("bandcamp", "bandcamp_check"), Count("soundcloud_track"), Count("youtube"), Count("beatport", "beatport_check"), Count("discogs"));
            var doubt = view.Count(r => r.B("review"));
            LookupStatus = $"Done: {bc} of {job.Total} on Bandcamp" + (sc > 0 ? $", {sc} on SoundCloud" : "") + (yt > 0 ? $", {yt} on YouTube" : "")
                           + (bp > 0 ? $", {bp} on Beatport" : "") + (dg > 0 ? $", {dg} on Discogs" : "") + (doubt > 0 ? $" · {doubt} to review" : "");
            if (_wasRunningShown) ShowFan();
            _wasRunningShown = false;
        }
        else
        {
            var paused = _main.Engine.Pace.PausedSeconds;
            LookupStatus = (job.Phase.Length > 0 ? job.Phase : $"Looking up {Math.Min(job.Done + 1, job.Total)} of {job.Total}…")
                           + (paused > 0 ? $" · Bandcamp asked for a pause; carrying on in {WaitText(paused)}" : "");
            _wasRunningShown = true;
        }
    }
    bool _wasRunningShown;

    static string WaitText(double s) => s >= 90 ? $"about {Math.Round(s / 60)} min" : $"{Math.Round(s)}s";

    [RelayCommand]
    void Skip(LookupRowViewModel row)
    {
        if (_job == null) return;
        try { F.SkipRow(_job, row.N); }
        catch (Exception e) { LookupError = e.Message; }
        Redraw();
    }

    [RelayCommand]
    void EditLink(LookupRowViewModel row)
    {
        foreach (var r in Rows.Where(r => r.Editing && r != row)) r.Editing = false;
        row.EditUrl = row.Url;
        row.EditYear = row.Year;
        row.EditError = "";
        row.Editing = true;
    }

    [RelayCommand]
    void CancelLink(LookupRowViewModel row)
    {
        row.Editing = false;
        Redraw();
    }

    [RelayCommand]
    Task SaveLink(LookupRowViewModel row) => SetRowLinkAsync(row, row.EditUrl.Trim(), row.EditYear);

    [RelayCommand]
    Task RemoveLink(LookupRowViewModel row) => SetRowLinkAsync(row, "", "");

    /// <summary>"Looks right": the link becomes one you set, so it is kept as is (and, for a SoundCloud profile,
    /// remembered as that artist's).</summary>
    [RelayCommand]
    Task LooksRight(LookupRowViewModel row) => SetRowLinkAsync(row, row.Url, row.Year);

    async Task SetRowLinkAsync(LookupRowViewModel row, string url, string year)
    {
        row.EditError = "";
        try
        {
            await F.SetLinkAsync(row.Artist, row.Title, url, url.Length > 0 ? year : "");
            row.Editing = false;
            Redraw();
        }
        catch (ArgumentException e)
        {
            row.EditError = e.Message;
            if (!row.Editing) LookupError = e.Message;
        }
    }

    [RelayCommand]
    void SearchRowAgain(LookupRowViewModel row)
    {
        if (_job == null) return;
        try
        {
            F.RetryRow(_job, row.N);
            _wasRunning = true;
            Redraw();
        }
        catch (Exception e) { LookupError = e.Message; }
    }

    [RelayCommand]
    void ToggleSteps(LookupRowViewModel row) => row.ShowSteps = !row.ShowSteps;

    [RelayCommand]
    async Task ExportResults()
    {
        if (_job == null || Ui == null) return;
        await Ui.SaveFileAsync("Export Link Finder results", "tracklist-links.csv", F.ToCsv(_job));
    }

    // ================= Your purchases, Downloads and the fallback =================

    public Option[] FallbackOptions { get; } =
    {
        new("soundcloud", "SoundCloud profile, else Bandcamp"),
        new("bandcamp", "Bandcamp page, else SoundCloud"),
    };
    [ObservableProperty] private Option _fallback;
    [ObservableProperty] private string _fanName = "";
    [ObservableProperty] private string _fanStatus = "";
    [ObservableProperty] private bool _fanError;
    [ObservableProperty] private bool _canSync = true;
    [ObservableProperty] private string _syncLabel = "Read purchases";
    [ObservableProperty] private bool _showDeep;
    [ObservableProperty] private bool _downloadsOn;

    partial void OnFallbackChanged(Option value)
    {
        try
        {
            if (value.Value == F.FallbackPref()) return;
            F.SetFallback(value.Value);               // saved and on-screen answers follow, without searching again
            Redraw();
        }
        catch (ArgumentException e) { LookupError = e.Message; }
    }

    partial void OnDownloadsOnChanged(bool value)
    {
        if (value == F.DownloadsOn()) return;
        F.SetDownloadsOn(value);
        _main.RefreshDownloads();
    }

    [RelayCommand]
    void ReadPurchases()
    {
        try
        {
            FanName = F.SetCollectionFan(FanName);
        }
        catch (ArgumentException e)
        {
            FanStatus = e.Message;
            FanError = true;
            return;
        }
        if (FanName.Length > 0) F.SyncInBackground();
        _main.RefreshDownloads();
        DispatcherTimer.RunOnce(ShowFan, TimeSpan.FromMilliseconds(400));
    }

    [RelayCommand]
    void ReadTracklists()
    {
        F.DeepReadInBackground();
        DispatcherTimer.RunOnce(ShowFan, TimeSpan.FromMilliseconds(400));
    }

    static string Ago(string? iso)
    {
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) return "";
        var s = (DateTimeOffset.UtcNow - t).TotalSeconds;
        return s < 60 ? "just now" : s < 3600 ? $"{(int)(s / 60)} min ago" : s < 86400 ? $"{(int)(s / 3600)} h ago" : $"{(int)(s / 86400)} d ago";
    }

    static string N0(double? n) => ((long)(n ?? 0)).ToString("N0", CultureInfo.InvariantCulture);

    void ShowFan()
    {
        var c = F.CollectionStatus();
        var paused = c.N("paused") ?? 0;
        var syncing = c.B("syncing");
        FanStatus = syncing ? (c.T("progress").Length > 0 ? c.T("progress") : "Reading your purchases…") + (paused > 0 ? $" · Bandcamp asked for a pause; carrying on in {WaitText(paused)}" : "")
            : c.T("error").Length > 0 ? c.T("error")
            : c.T("fan").Length == 0 ? "Not set: purchases won't be checked"
            : !c.B("complete") && c.N("items") > 0 ? $"{N0(c.N("items"))} purchases read so far; the rest are read on the next lookup"
            : c.T("synced_at").Length > 0
                ? $"{N0(c.N("items"))} purchases · read {Ago(c.T("synced_at"))} · full track lists: {N0(c.N("releases_read"))} of {N0(c.N("releases"))} releases · label pages: {N0(c.N("labels_read"))} of {N0(c.N("labels"))}"
                  + (c.B("deep_running") ? $" (reading in the background{(c.T("deep_note").Length > 0 ? "; " + c.T("deep_note") : "")}{(paused > 0 ? "; Bandcamp asked for a pause, " + WaitText(paused) : "")})" : "")
            : "Not read yet; it happens on the first lookup";
        FanError = !syncing && c.T("error").Length > 0;
        CanSync = !syncing;
        SyncLabel = c.N("items") > 0 && !c.B("complete") ? "Continue reading" : "Read purchases";
        ShowDeep = !syncing && !c.B("deep_running") && c.B("complete")
                   && (c.N("releases_read") < c.N("releases") || c.N("labels_read") < c.N("labels"));
    }

    // ================= Saved results =================

    public Option[] SavedFilterBase { get; } =
    {
        new("", "All"), new("lastrun", "Searched in the last lookup"), new("review", "Needs review"),
        new("missing", "Track not found (artist page only)"), new("found", "Track found"), new("manual", "Links you set"),
        new("list", "In your list"), new("nospelling", "No spelling on file"), new("unsearched", "In your list, not looked up yet"),
    };
    public ObservableCollection<Option> SavedFilterOptions { get; } = new(new[] { new Option("", "All") });
    public ObservableCollection<SavedRowViewModel> SavedRows { get; } = new();
    const int SavedPage = 200;
    int _savedTotal;
    bool _savedLoaded;

    [ObservableProperty] private string _savedQuery = "";
    [ObservableProperty] private Option _savedFilter;
    [ObservableProperty] private string _savedStatus = "";
    [ObservableProperty] private string _savedError = "";
    [ObservableProperty] private string _knownInfo = "";
    [ObservableProperty] private bool _hasKnown;
    [ObservableProperty] private bool _canLoadMore;
    [ObservableProperty] private string _savedSort = "";
    [ObservableProperty] private bool _savedDesc;
    bool _settingFilters;
    DispatcherTimer? _queryTimer;

    public string SavedIntro =>
        "Every track Link Finder has looked up, newest first, under the tidied form of its name (lower case, no punctuation or describing tags), " +
        "which is what tracks are matched by. Import your list adds how each artist and title should be written and, if the list has them, links: " +
        "columns named Artist and Title, plus any link columns; text beside a link, such as \"(limited time only)\", is kept as a note. " +
        "A link from your list is used when the lookup has found nothing better; a release the lookup found itself, or a link you set here, comes first.";

    partial void OnTabChanged(int value)
    {
        if (value == 1) LoadSaved(true);
    }

    partial void OnSavedFilterChanged(Option value)
    {
        if (!_settingFilters && _savedLoaded) LoadSaved(true);
    }

    partial void OnSavedQueryChanged(string value)
    {
        _queryTimer?.Stop();
        _queryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _queryTimer.Tick += (_, _) =>
        {
            _queryTimer?.Stop();
            LoadSaved(true);
        };
        _queryTimer.Start();
    }

    /// <summary>Click a heading to sort by it; again to reverse; a third time to go back to newest first.</summary>
    public void SortSaved(string key)
    {
        var first = key is "saved" or "year";            // newest and latest first
        if (SavedSort != key) (SavedSort, SavedDesc) = (key, first);
        else if (SavedDesc == first) SavedDesc = !SavedDesc;
        else (SavedSort, SavedDesc) = ("", false);
        LoadSaved(true);
    }

    public void LoadSaved(bool reset)
    {
        _savedLoaded = true;
        SavedError = "";
        try
        {
            if (reset) SavedRows.Clear();
            var (total, counts, rows) = F.SavedListing(SavedQuery, SavedFilter?.Value ?? "", SavedPage, SavedRows.Count, SavedSort, SavedDesc);
            foreach (var r in rows) SavedRows.Add(new SavedRowViewModel(r));
            _savedTotal = total;
            var keep = SavedFilter?.Value ?? "";
            _settingFilters = true;
            SavedFilterOptions.Clear();
            foreach (var o in SavedFilterBase) SavedFilterOptions.Add(new Option(o.Value, $"{o.Text} ({counts.GetValueOrDefault(o.Value)})"));
            SavedFilter = SavedFilterOptions.First(o => o.Value == keep);
            _settingFilters = false;
            SavedStatus = $"{N0(total)} track{(total == 1 ? "" : "s")}";
            var known = F.KnownCounts();
            HasKnown = known.N("known_tracks") > 0 || known.N("known_artists") > 0;
            if (!_keepKnownInfo)
                KnownInfo = HasKnown ? $"Your list: {N0(known.N("known_tracks"))} tracks, {N0(known.N("known_artists"))} artist spellings." : "No list imported yet.";
            _keepKnownInfo = false;
            CanLoadMore = SavedRows.Count < _savedTotal;
        }
        catch (Exception e)
        {
            _settingFilters = false;
            SavedError = e.Message;
        }
    }
    bool _keepKnownInfo;

    [RelayCommand]
    void LoadMore() => LoadSaved(false);

    [RelayCommand]
    async Task ExportSaved()
    {
        if (Ui == null) return;
        await Ui.SaveFileAsync("Export saved results", "link-finder-saved.csv", F.SavedCsv(SavedQuery, SavedFilter?.Value ?? "", SavedSort, SavedDesc));
    }

    [RelayCommand]
    async Task ImportList()
    {
        if (Ui == null) return;
        var data = await Ui.OpenTableFileAsync("Choose your list (Excel or CSV)");
        if (data == null) return;
        SavedError = "";
        SavedStatus = "Importing…";
        try
        {
            if (data.Length > 25_000_000) throw new TracklistException("That file is too large (25 MB limit).");
            var d = F.ImportKnown(Tracklist.ReadKnown(Tracklist.ReadTable(data)));
            string N(string k) => N0(d.N(k));
            var parts = new List<string>();
            if (d.N("link_clashes") > 0) parts.Add($"{N("link_clashes")} track{(d.N("link_clashes") == 1 ? " has" : "s have")} different links on different rows (a release link is preferred, then the one given most often)");
            if (d.N("spelling_clashes") > 0) parts.Add($"{N("spelling_clashes")} name{(d.N("spelling_clashes") == 1 ? " is" : "s are")} written more than one way (the commonest is kept)");
            if (d.N("skipped") > 0) parts.Add($"{N("skipped")} row{(d.N("skipped") == 1 ? "" : "s")} with no readable title left out");
            var eg = (d["clash_examples"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            KnownInfo = $"Imported {N("rows")} rows: {N("tracks")} tracks ({N("with_link")} with a link, {N("release_links")} of them Bandcamp releases; {N("with_note")} with a note) and {N("artists")} artist spellings."
                        + (parts.Count > 0 ? " " + string.Join("; ", parts) + "." : "") + (eg != null ? $" For example, kept \"{eg.T("kept")}\" over \"{eg.T("ignored")}\"." : "");
            _keepKnownInfo = true;
        }
        catch (TracklistException e)
        {
            SavedError = e.Message;
        }
        LoadSaved(true);
    }

    [RelayCommand]
    async Task RemoveList()
    {
        if (Ui == null || !await Ui.ConfirmAsync("Remove your list", "Remove the list you imported? Saved results and links you set are kept.", "Remove")) return;
        F.ClearKnown();
        LoadSaved(true);
    }

    [RelayCommand]
    void EditSavedLink(SavedRowViewModel row)
    {
        row.EditUrl = row.Url;
        row.Error = "";
        row.EditingLink = true;
    }

    [RelayCommand]
    void CancelSavedLink(SavedRowViewModel row) => row.EditingLink = false;

    [RelayCommand]
    Task SaveSavedLink(SavedRowViewModel row) => SetSavedLinkAsync(row, row.EditUrl.Trim());

    [RelayCommand]
    Task SavedLooksRight(SavedRowViewModel row) => SetSavedLinkAsync(row, row.Url);

    async Task SetSavedLinkAsync(SavedRowViewModel row, string url)
    {
        row.Error = "";
        try
        {
            await F.SetLinkAsync(row.AskArtist, row.AskTitle, url, url == row.Url ? row.Year : "",
                                 key: row.Row.B("searched") && !row.Row.B("title") ? row.Key : "");
            LoadSaved(true);
        }
        catch (ArgumentException e) { row.Error = e.Message; }
    }

    [RelayCommand]
    void SearchSavedAgain(SavedRowViewModel row)
    {
        Tab = 0;
        PasteText = $"{row.AskArtist}\t{row.AskTitle}";
        Begin(System.Text.Encoding.UTF8.GetBytes(PasteText), fresh: true, retryMissing: false);
    }

    [RelayCommand]
    void EditSpelling(SavedRowViewModel row)
    {
        row.SpellArtist = row.Row.B("list_artist") ? row.Row.T("list_artist") : row.AskArtist;
        row.SpellTitle = row.Row.B("list_title") ? row.Row.T("list_title") : row.AskTitle;
        row.Error = "";
        row.EditingSpell = true;
    }

    [RelayCommand]
    void CancelSpelling(SavedRowViewModel row) => row.EditingSpell = false;

    [RelayCommand]
    void SaveSpelling(SavedRowViewModel row) => Spell(row, row.SpellArtist.Trim(), row.SpellTitle.Trim());

    [RelayCommand]
    void RemoveSpelling(SavedRowViewModel row) => Spell(row, "", "");

    void Spell(SavedRowViewModel row, string artist, string title)
    {
        var r = row.Row;
        try
        {
            var key = r.T("key");
            key = key.Contains('~') ? key[..key.IndexOf('~')] : key;
            F.SetSpelling(key, artist, title, r.B("list_guess") ? "" : r.T("list_variant"),
                          r.B("list_guess") ? (r.T("list_key"), r.T("list_variant")) : null);
            LoadSaved(true);
        }
        catch (Exception e) { row.Error = e.Message; }
    }

    [RelayCommand]
    void ToggleForget(SavedRowViewModel row) => row.Forgetting = !row.Forgetting;

    [RelayCommand]
    void ForgetAnswer(SavedRowViewModel row) => Forget(row, true, false);

    [RelayCommand]
    void ForgetEntry(SavedRowViewModel row) => Forget(row, false, true);

    [RelayCommand]
    void ForgetBoth(SavedRowViewModel row) => Forget(row, true, true);

    void Forget(SavedRowViewModel row, bool answer, bool entry)
    {
        try
        {
            if (answer) F.ForgetSaved(row.Key.Contains('~') ? row.Key[..row.Key.IndexOf('~')] : row.Key);
            if (entry) F.ForgetEntry(row.Row.T("list_key"), row.Row.T("list_variant"));
            LoadSaved(true);
        }
        catch (Exception e) { row.Error = e.Message; }
    }
}
