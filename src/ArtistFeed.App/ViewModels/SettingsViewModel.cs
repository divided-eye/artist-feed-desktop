using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using ArtistFeed.App.Services;
using ArtistFeed.Core;

namespace ArtistFeed.App.ViewModels;

public partial class SiteToggle(string key, string label, string color, bool on) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public IBrush Dot { get; } = new SolidColorBrush(Color.Parse(color));
    public bool Saved { get; set; } = on;
    [ObservableProperty] private bool _on = on;
}

/// <summary>Settings: changes are kept in a draft until Save; Close asks before dropping unsaved ones.</summary>
public partial class SettingsViewModel : ObservableObject
{
    readonly MainViewModel _main;
    public ObservableCollection<SiteToggle> Sites { get; } = new();
    public string DataFolder => _main.DataFolder;
    public string DatabasePath => _main.Engine.Db.Path;

    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private string _savedNote = "";
    [ObservableProperty] private string _error = "";

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        foreach (var s in main.Engine.Sites.All)
        {
            var t = new SiteToggle(s.Key, s.Label, s.Color, main.Engine.Sites.IsOn(s.Key));
            t.PropertyChanged += (_, _) => Mark();
            Sites.Add(t);
        }
    }

    void Mark()
    {
        Dirty = Sites.Any(s => s.On != s.Saved);
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
        if (Sites.All(s => !s.On))
        {
            Error = "At least one site has to stay on.";
            return;
        }
        try
        {
            // Switch on before off, so the "at least one" rule never trips half way.
            foreach (var s in Sites.Where(s => s.On && !s.Saved)) _main.Engine.Sites.SetOn(s.Key, true);
            foreach (var s in Sites.Where(s => !s.On && s.Saved)) _main.Engine.Sites.SetOn(s.Key, false);
            foreach (var s in Sites) s.Saved = s.On;
            Dirty = false;
            SavedNote = "Saved";
            _main.SitesOrArtistsChanged();
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
