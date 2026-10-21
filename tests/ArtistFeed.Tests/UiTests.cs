using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ArtistFeed.App;
using ArtistFeed.App.Services;
using ArtistFeed.App.ViewModels;
using ArtistFeed.App.Views;
using ArtistFeed.Core;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(ArtistFeed.Tests.HeadlessApp))]

namespace ArtistFeed.Tests;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>The windows themselves, driven the way a person would, with pictures saved for checking.</summary>
public class UiTests
{
    static readonly string Shots = Path.Combine(Path.GetTempPath(), "artistfeed-shots");

    static void Save(Window w, string name)
    {
        Directory.CreateDirectory(Shots);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        frame?.Save(Path.Combine(Shots, name + ".png"));
    }

    static async Task<(TestApp App, MainViewModel Vm, MainWindow Window)> OpenAsync(bool data = true)
    {
        var app = new TestApp();
        if (data)
        {
            await app.AddAsync(("bandcamp", "nightferry"), ("soundcloud", "nightferry"));
            await app.Engine.Refresher.RefreshAsync();
            await app.AddAsync(("youtube", "@tubeband"));
        }
        var vm = new MainViewModel(app.Engine, app.Folder);
        var w = new MainWindow { DataContext = vm, Width = 1320, Height = 880 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (app, vm, w);
    }

    static T Find<T>(Visual root, Func<T, bool>? test = null) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().First(x => test == null || test(x));

    [AvaloniaFact]
    public async Task FeedGridListAndFilters()
    {
        var (app, vm, w) = await OpenAsync();
        using var _ = app;
        Assert.Equal(new[] { "Tube Band - Glass River", "Demo", "Studio Diary", "Second", "First", "Third", "No Art" },
            vm.Posts.Select(p => p.Title));
        Assert.Equal(new[] { "All", "Bandcamp", "SoundCloud", "YouTube" }, vm.Chips.Select(c => c.Label));
        Assert.Contains("Updated", vm.Status);
        Save(w, "1-grid");

        // a site button narrows the feed; All brings everything back
        vm.PickSiteCommand.Execute(vm.Chips.Single(c => c.Label == "Bandcamp"));
        Assert.Equal(new[] { "Second", "First", "Third" }, vm.Posts.Select(p => p.Title));
        Assert.True(vm.HasFilters);
        vm.ClearFiltersCommand.Execute(null);
        Assert.Equal(7, vm.Posts.Count);

        // search waits for typing to pause
        vm.Search = "intro";
        Assert.Equal(7, vm.Posts.Count);
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "First" }, vm.Posts.Select(p => p.Title));
        vm.Search = "";
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();

        // filters
        vm.Year = vm.Years.Single(y => y.Value == "2025");
        Assert.Equal(new[] { "First" }, vm.Posts.Select(p => p.Title));
        vm.Year = vm.Years[0];
        vm.Kind = vm.Kinds.Single(k => k.Value == "album");
        Assert.Equal(new[] { "First", "Third" }, vm.Posts.Select(p => p.Title));
        vm.ClearFiltersCommand.Execute(null);

        // hiding a card takes it away; Show hidden brings it back to put back
        var first = vm.Posts.Single(p => p.Title == "First");
        vm.ToggleHiddenCardCommand.Execute(first);
        Assert.DoesNotContain("First", vm.Posts.Select(p => p.Title));
        Assert.Equal(1, vm.HiddenCount);
        vm.ShowHidden = true;
        var hidden = vm.Posts.Single(p => p.Title == "First");
        Assert.True(hidden.IsHidden);
        vm.ToggleHiddenCardCommand.Execute(hidden);
        Assert.False(hidden.IsHidden);
        Assert.Equal(0, vm.HiddenCount);
        vm.ShowHidden = false;

        // the list view, sorted by the database
        vm.ShowListCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsList);
        Assert.Equal(8, vm.Rows.Count);
        vm.SortBy("duration");
        Assert.Equal("Long One", vm.Rows[0].Track);
        Assert.Equal("1:02:05", vm.Rows[0].Duration);
        vm.SortBy("duration");
        Assert.Equal("Intro", vm.Rows.First(r => r.Duration != "").Track);
        vm.SortBy("artist");
        Assert.Equal("Guest Person", vm.Rows[0].Artist);
        Save(w, "2-list");

        // played: from the list, and the cards follow
        var intro = vm.Rows.Single(r => r.Track == "Intro");
        vm.TogglePlayedRowCommand.Execute(intro);
        Assert.True(intro.IsPlayed);
        vm.ShowGridCommand.Execute(null);
        Assert.True(vm.Posts.Single(p => p.Title == "First").IsPlayed);

        // playing opens the player dock (the site's player itself can't load without a network here)
        vm.PlayCardCommand.Execute(vm.Posts.Single(p => p.Title == "Demo"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("soundcloud", vm.Playing?.Source);
        Assert.True(Find<Border>(w, b => b.Name == "Dock").IsVisible);
        Assert.True(vm.Posts.Single(p => p.Title == "Demo").IsPlaying);
        Save(w, "3-player");
        vm.ClosePlayerCommand.Execute(null);
        Assert.False(Find<Border>(w, b => b.Name == "Dock").IsVisible);

        // preferences are kept: the next start opens in the same view
        vm.ShowListCommand.Execute(null);
        var again = new MainViewModel(app.Engine, app.Folder);
        Assert.True(again.IsList);
        w.Close();
    }

    [AvaloniaFact]
    public async Task EmptyFeedSaysWhatToDo()
    {
        var (app, vm, w) = await OpenAsync(data: false);
        using var _ = app;
        Assert.True(vm.IsEmpty);
        Assert.Equal("No artists yet", vm.EmptyTitle);
        Save(w, "0-empty");
        w.Close();
    }

    [AvaloniaFact]
    public async Task TrackedArtistsEditSaveAndErrors()
    {
        var (app, vm, w) = await OpenAsync();
        using var _ = app;
        var ta = new TrackedArtistsViewModel(vm);
        var win = new TrackedArtistsWindow { DataContext = ta, Width = 1240, Height = 800 };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, ta.Rows.Count);
        Assert.False(ta.Dirty);
        Save(win, "4-tracked-artists");

        // typing into a cell edits the row
        var nameBox = Find<TextBox>(win, t => t.Text == "Night Ferry");
        nameBox.Focus();
        nameBox.CaretIndex = nameBox.Text!.Length;
        win.KeyTextInput(", NF");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Night Ferry, NF", ta.Rows.Single(r => r.Name.StartsWith("Night Ferry")).Name);
        Assert.True(ta.Dirty);
        await ta.SaveCommand.ExecuteAsync(null);
        Assert.Equal("NF", app.Engine.Db.ListArtists().Single(a => a.Name == "Night Ferry").Aliases);

        // a bad page and a page already on another row: nothing is saved, the rows say why
        ta.AddRowCommand.Execute(null);
        var row = ta.Rows[^1];
        row.Name = "Someone";
        row.SoundCloud = "not a profile!";
        row.Bandcamp = "nightferry";
        Assert.True(ta.Dirty);
        await ta.SaveCommand.ExecuteAsync(null);
        Assert.Contains("SoundCloud", row.Errors);
        Assert.Contains("also on row", row.Errors);
        Assert.Equal(2, app.Engine.Db.ListArtists().Count);
        Dispatcher.UIThread.RunJobs();
        Save(win, "5-tracked-artists-errors");

        // fixed: saved, and the new page is read
        row.SoundCloud = "";
        row.Bandcamp = "solo";
        row.Focus = true;
        await ta.SaveCommand.ExecuteAsync(null);
        Assert.Equal("", ta.Error);
        Assert.False(ta.Dirty);
        Assert.Equal(3, app.Engine.Db.ListArtists().Count);
        for (int i = 0; i < 300 && !app.Feed().Any(p => p.Title == "Only One"); i++) await Task.Delay(10);
        for (int i = 0; i < 300 && app.Engine.Refresher.Refreshing; i++) await Task.Delay(10);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("Only One", app.Feed().Select(p => p.Title));
        Assert.Contains("someone", app.Engine.Sites.FocusNames());
        vm.Focus = true;
        Assert.Equal(new[] { "Only One" }, vm.Posts.Select(p => p.Title));
        vm.Focus = false;

        // filter rows; mark one for removal
        ta.FilterText = "tube";
        Assert.Single(ta.Visible);
        ta.FilterText = "";
        var tube = ta.Rows.Single(r => r.Name == "Tube Band");
        ta.ToggleRemoveCommand.Execute(tube);
        Assert.True(tube.Remove);
        Assert.True(ta.Dirty);
        ta.Show = ta.ShowOptions.Single(o => o.Value == "removed");
        Assert.Single(ta.Visible);
        win.Close();
        w.Close();
    }

    [AvaloniaFact]
    public async Task SettingsSwitchASiteOffAndOn()
    {
        var (app, vm, w) = await OpenAsync();
        using var _ = app;
        var s = new SettingsViewModel(vm);
        var win = new SettingsWindow { DataContext = s };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(s.Dirty);
        var yt = s.Sites.Single(x => x.Key == "youtube");
        yt.On = false;
        Assert.True(s.Dirty);
        Assert.Equal("Unsaved changes", s.SavedNote);
        Save(win, "6-settings");
        s.SaveCommand.Execute(null);
        Assert.Equal("Saved", s.SavedNote);
        Assert.False(app.Engine.Sites.IsOn("youtube"));
        Assert.DoesNotContain("YouTube", vm.Chips.Select(c => c.Label));
        Assert.DoesNotContain("Studio Diary", vm.Posts.Select(p => p.Title));
        // every site off is refused
        foreach (var t in s.Sites) t.On = false;
        s.SaveCommand.Execute(null);
        Assert.Contains("At least one", s.Error);
        foreach (var t in s.Sites) t.On = true;
        s.SaveCommand.Execute(null);
        Assert.Contains("YouTube", vm.Chips.Select(c => c.Label));
        win.Close();
        w.Close();
    }

    [AvaloniaFact]
    public async Task DisplaySizeAndColourScheme()
    {
        var (app, vm, w) = await OpenAsync();
        using var _ = app;
        try
        {
            var s = new SettingsViewModel(vm);
            var win = new SettingsWindow { DataContext = s };
            win.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("100", s.Size.Value);
            Assert.Equal("dark", s.Theme.Value);

            // nothing changes until Save
            s.Theme = s.Themes.Single(t => t.Value == "light");
            s.Size = s.Sizes.Single(t => t.Value == "130");
            Assert.True(s.Dirty);
            Assert.Equal("dark", Display.Theme);
            s.SaveCommand.Execute(null);
            Assert.False(s.Dirty);
            Assert.Equal("Saved", s.SavedNote);
            Assert.Equal("light", Display.Theme);
            Assert.Equal(1.3, Display.Scale, 3);
            Assert.Equal(Avalonia.Styling.ThemeVariant.Light, Avalonia.Application.Current!.RequestedThemeVariant);
            Assert.Equal(Avalonia.Media.Color.Parse("#f5f6f8"), ((Avalonia.Media.ISolidColorBrush)Avalonia.Application.Current.Resources["Bg"]!).Color);
            Dispatcher.UIThread.RunJobs();
            var zoom = Find<LayoutTransformControl>(w);
            Assert.Equal(1.3, ((Avalonia.Media.ScaleTransform)zoom.LayoutTransform!).ScaleX, 3);
            Save(win, "7-settings-light-130");
            win.Close();

            // kept for the next start
            Assert.Equal(("130", "light"), Display.Read(app.Engine.Db));
            Save(w, "8-grid-light-130");
            vm.ShowListCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Save(w, "9-list-light-130");

            Display.Save(app.Engine.Db, "100", "contrast");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("contrast", w.Classes);
            Assert.Null(zoom.LayoutTransform);
            vm.ShowGridCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Save(w, "10-grid-contrast");
            var ta = new TrackedArtistsWindow { DataContext = new TrackedArtistsViewModel(vm), Width = 1240, Height = 800 };
            ta.Show();
            Dispatcher.UIThread.RunJobs();
            Save(ta, "11-tracked-artists-contrast");
            ta.Close();
            Display.Save(app.Engine.Db, "100", "light");
            var ta2 = new TrackedArtistsWindow { DataContext = new TrackedArtistsViewModel(vm), Width = 1240, Height = 800 };
            ta2.Show();
            Dispatcher.UIThread.RunJobs();
            Save(ta2, "12-tracked-artists-light");
            ta2.Close();
        }
        finally
        {
            Display.Apply("100", "dark");
            w.Close();
        }
    }
}
