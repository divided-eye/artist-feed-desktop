using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using ArtistFeed.App.Services;
using ArtistFeed.Core;

namespace ArtistFeed.App.ViewModels;

public partial class SiteToggle(string key, string label, string color, bool on, string note = "", string tip = "") : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Note { get; } = note;                    // "Link Finder only · experimental"
    public bool HasNote => Note.Length > 0;
    public string Tip { get; } = tip;
    public IBrush Dot { get; } = new SolidColorBrush(Color.Parse(color));
    public bool Saved { get; set; } = on;
    [ObservableProperty] private bool _on = on;
    [ObservableProperty] private bool _available = true;   // for Link Finder's ticks: the site is switched on above
    public string? AvailableTip => Available ? null : "Switched off under Sites: switch it on there first";
    partial void OnAvailableChanged(bool value) => OnPropertyChanged(nameof(AvailableTip));
}

/// <summary>Settings: changes are kept in a draft until Save; Close asks before dropping unsaved ones.</summary>
public partial class SettingsViewModel : ObservableObject
{
    readonly MainViewModel _main;
    public ObservableCollection<SiteToggle> Sites { get; } = new();
    public ObservableCollection<SiteToggle> LookupSites { get; } = new();

    // Discogs token and your SoundCloud account
    [ObservableProperty] private string _discogsToken = "";
    [ObservableProperty] private bool _hasDiscogsToken;
    [ObservableProperty] private bool _showDiscogs;
    [ObservableProperty] private string _soundCloudMe = "";
    [ObservableProperty] private string _followsStatus = "";
    [ObservableProperty] private bool _readingFollows;
    string _savedMe = "";
    LinkFinder F => _main.Engine.LinkFinder;
    public static readonly Avalonia.Data.Converters.FuncValueConverter<bool, string> TokenPlaceholder =
        new(saved => saved ? "Token saved (paste a new one to replace it)" : "Discogs access token");
    public string DataFolder => _main.DataFolder;
    public string DatabasePath => _main.Engine.Db.Path;

    public Option[] Sizes => Display.Sizes;
    public Option[] Themes => Display.Themes;
    [ObservableProperty] private Option _size;
    [ObservableProperty] private Option _theme;
    string _savedSize, _savedTheme;

    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private string _savedNote = "";
    [ObservableProperty] private string _error = "";

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        (_savedSize, _savedTheme) = Display.Read(main.Engine.Db);
        _size = Sizes.Single(o => o.Value == _savedSize);
        _theme = Themes.Single(o => o.Value == _savedTheme);
        var sites = main.Engine.Sites;
        foreach (var s in sites.All)
        {
            var t = new SiteToggle(s.Key, s.Label, s.Color, sites.IsOn(s.Key));
            t.PropertyChanged += (_, _) => Mark();
            Sites.Add(t);
        }
        foreach (var (key, (label, color)) in ArtistFeed.Core.Sites.LookupOnly)
        {
            var t = new SiteToggle(key, label, color, sites.IsOn(key), "Link Finder only · experimental", key == "beatport"
                ? "Beatport: a place to buy tracks. Link Finder looks there after Bandcamp. Off by default. Experimental: results may be missing or wrong."
                : "Discogs: releases found nowhere else, often vinyl-only; links the release's main page. Link Finder's last resort; needs a free access token. Off by default. Experimental: results may be missing or wrong.");
            t.PropertyChanged += (_, _) => Mark();
            Sites.Add(t);
        }
        var chosen = sites.LookupChosen();
        foreach (var key in ArtistFeed.Core.Sites.LookupOrder)
        {
            var t = new SiteToggle(key, sites.Label(key), sites.ColorOf(key), chosen.Contains(key));
            t.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SiteToggle.On)) Mark(); };
            LookupSites.Add(t);
        }
        HasDiscogsToken = F.DiscogsToken().Length > 0;
        _soundCloudMe = _savedMe = F.SoundCloudMe();
        ShowFollows();
        Availability();
    }

    /// <summary>A site switched off above can't be ticked for Link Finder.</summary>
    void Availability()
    {
        foreach (var t in LookupSites) t.Available = Sites.FirstOrDefault(s => s.Key == t.Key)?.On ?? false;
        ShowDiscogs = LookupSites.Any(t => t.Key == "discogs" && t.On && t.Available);
    }

    partial void OnDiscogsTokenChanged(string value) => Mark();
    partial void OnSoundCloudMeChanged(string value) => Mark();

    void ShowFollows()
    {
        var st = F.FollowsStatus();
        var user = st["user"]?.GetValue<string>() ?? "";
        var count = (long)(Html.AsNumber(st["count"]) ?? 0);
        var error = st["error"]?.GetValue<string>() ?? "";
        FollowsStatus = user.Length == 0 ? "Not set"
            : error.Length > 0 ? error
            : st["read_at"] is null ? "Follows not read yet; they are read the next time a profile has to be picked by name"
            : $"{count:N0} followed profile{(count == 1 ? "" : "s")} read {Show.Ago(st["read_at"]!.GetValue<string>())}";
    }

    [RelayCommand]
    async Task ReadFollows()
    {
        if (SoundCloudMe.Trim() != _savedMe)
        {
            Save();
            if (Error.Length > 0) return;
        }
        if (_savedMe.Length == 0) return;
        ReadingFollows = true;
        FollowsStatus = "Reading who you follow…";
        try { await F.FollowedProfilesAsync(force: true); }
        catch (Exception) { }
        ReadingFollows = false;
        ShowFollows();
    }

    [RelayCommand]
    void RemoveDiscogsToken()
    {
        F.SetDiscogsToken("");
        DiscogsToken = "";
        HasDiscogsToken = false;
        SavedNote = "Token removed";
    }

    bool LookupChanged => LookupSites.Any(t => t.On != t.Saved);

    partial void OnSizeChanged(Option value) => Mark();
    partial void OnThemeChanged(Option value) => Mark();

    bool DisplayChanged => Size.Value != _savedSize || Theme.Value != _savedTheme;

    void Mark()
    {
        Availability();
        Dirty = Sites.Any(s => s.On != s.Saved) || DisplayChanged || LookupChanged || DiscogsToken.Trim().Length > 0 || SoundCloudMe.Trim() != _savedMe;
        SavedNote = Dirty ? "Unsaved changes" : "";
        Error = "";
    }

    [RelayCommand]
    void Save()
    {
        if (!Dirty)
        {
            SavedNote = "Nothing to save";
            return;
        }
        if (Sites.Where(s => !s.HasNote).All(s => !s.On))
        {
            Error = "At least one site has to stay on.";
            return;
        }
        try
        {
            // Switch on before off, so the "at least one" rule never trips half way.
            foreach (var s in Sites.Where(s => s.On && !s.Saved)) _main.Engine.Sites.SetOn(s.Key, true);
            foreach (var s in Sites.Where(s => !s.On && s.Saved)) _main.Engine.Sites.SetOn(s.Key, false);
            if (SoundCloudMe.Trim() != _savedMe)
            {
                SoundCloudMe = _savedMe = F.SetSoundCloudMe(SoundCloudMe);
                ShowFollows();
            }
            var sitesChanged = Sites.Any(s => s.On != s.Saved);
            foreach (var s in Sites) s.Saved = s.On;
            if (LookupChanged)
            {
                _main.Engine.Sites.SetLookupSites(LookupSites.Where(t => t.On).Select(t => t.Key));
                foreach (var t in LookupSites) t.Saved = t.On;
            }
            if (DiscogsToken.Trim().Length > 0)
            {
                F.SetDiscogsToken(DiscogsToken);
                DiscogsToken = "";
                HasDiscogsToken = true;
            }
            F.SitesChanged();                                   // lookups on screen follow the choice of sites
            if (DisplayChanged)
            {
                Display.Save(_main.Engine.Db, Size.Value, Theme.Value);
                (_savedSize, _savedTheme) = (Size.Value, Theme.Value);
            }
            Dirty = false;
            SavedNote = "Saved";
            Availability();
            if (sitesChanged) _main.SitesOrArtistsChanged();
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    [RelayCommand]
    void OpenDataFolder() => Desktop.Open(DataFolder);

    public IUi? Ui { get; set; }
    [ObservableProperty] private string _importNote = "";

    /// <summary>Bring over the web version's artists, feed and settings (its data/feed.db), from the next start.</summary>
    [RelayCommand]
    async Task ImportDatabase()
    {
        if (Ui == null) return;
        var path = await Ui.OpenDatabaseFileAsync();
        if (path == null) return;
        try
        {
            Engine.PrepareImport(path, DatabasePath);
            ImportNote = "Copied. Close Artist Feed and open it again to use it; what is here now is kept as feed.db.before-import.";
            Error = "";
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }
}
