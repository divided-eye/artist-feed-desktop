using System.Text.Json.Nodes;
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
        row.Ticks.Single(t => t.Space.Id == "focus").IsIn = true;
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
        vm.Workspace = vm.WorkspaceOptions.Single(o => o.Value == "focus");
        Assert.Equal(new[] { "Only One" }, vm.Posts.Select(p => p.Title));
        vm.Workspace = vm.WorkspaceOptions[0];

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

    [AvaloniaFact]
    public async Task ANoteShowsOnTheCardAndInTheList()
    {
        var (app, vm, w) = await OpenAsync();
        using var _ = app;
        app.Engine.Db.Query("UPDATE posts SET note=? WHERE title='Third'", Bandcamp.NotForSale);
        vm.SitesOrArtistsChanged();
        Dispatcher.UIThread.RunJobs();
        var third = vm.Posts.Single(p => p.Title == "Third");
        Assert.True(third.HasNote);
        Assert.Contains("nothing to buy", third.NoteTip);
        Assert.False(vm.Posts.Single(p => p.Title == "First").HasNote);
        Save(w, "13-note-card");
        vm.ShowListCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Third · " + Bandcamp.NotForSale, vm.Rows.Single(r => r.Row.PostTitle == "Third").Release);
        w.Close();
    }

    [AvaloniaFact]
    public async Task WorkspacesAndImportFollows()
    {
        var (app, vm, w) = await OpenAsync();
        using var _ = app;
        Assert.Equal(new[] { "Every artist", "Focus (0)" }, vm.WorkspaceOptions.Select(o => o.Text));
        var ta = new TrackedArtistsViewModel(vm);
        var win = new TrackedArtistsWindow { DataContext = ta, Width = 1240, Height = 860 };
        win.Show();

        // a new workspace, renamed, with Night Ferry in it
        ta.ToggleSpacesCommand.Execute(null);
        ta.AddSpaceCommand.Execute(null);
        var deep = ta.Spaces[^1];
        deep.Name = "Deep dubstep";
        Assert.True(ta.Dirty);
        var nf = ta.Rows.Single(r => r.Name == "Night Ferry");
        nf.Ticks.Single(t => t.Space == deep).IsIn = true;
        Assert.Equal(1, deep.Count);
        Assert.Contains(ta.ShowOptions, o => o.Text == "In Deep dubstep (1)");

        // follows read from SoundCloud: one already tracked, one joins the row of the same name, one new
        app.Web.Extra = r => r.RequestUri!.Host == "api-v2.soundcloud.com" && r.RequestUri.AbsolutePath == "/resolve" && r.RequestUri.Query.Contains("mefan")
            ? StandIn.Json(new JsonObject { ["kind"] = "user", ["id"] = 7, ["username"] = "Me" })
            : r.RequestUri!.Host == "api-v2.soundcloud.com" && r.RequestUri.AbsolutePath == "/users/7/followings"
            ? StandIn.Json(new JsonObject { ["collection"] = new JsonArray(
                new JsonObject { ["username"] = "Night Ferry", ["permalink"] = "nightferry" },
                new JsonObject { ["username"] = "Tube Band", ["permalink"] = "tube-band-sc" },
                new JsonObject { ["username"] = "Brand New Act", ["permalink"] = "brandnewact" }) })
            : null;
        ta.ToggleFollowsCommand.Execute(null);
        ta.FollowUser = "https://soundcloud.com/MeFan";
        await ta.ReadFollowsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "already tracked", "tube-band-sc", "brandnewact" }, ta.FollowItems.Select(i => i.Detail));
        Assert.Equal("2 of 2 picked · 1 already tracked or in the table", ta.FollowCount);
        ta.FollowSpace = ta.SpaceChoices.Single(o => o.Text == "Deep dubstep");
        Save(win, "14-import-follows");
        ta.AddFollowsCommand.Execute(null);
        Assert.Equal("tube-band-sc", ta.Rows.Single(r => r.Name == "Tube Band").SoundCloud);
        Assert.True(ta.Rows.Single(r => r.Name == "Brand New Act").InGroup(deep.Id));
        Assert.All(ta.FollowItems, i => Assert.False(i.Open));
        Save(win, "15-workspaces");

        await ta.SaveCommand.ExecuteAsync(null);
        Assert.Equal("", ta.Error);
        Assert.Equal(new[] { "Every artist", "Focus (0)", "Deep dubstep (3)" }, vm.WorkspaceOptions.Select(o => o.Text));
        Assert.Equal(new[] { "focus", deep.Id }, app.Engine.Sites.Workspaces().Select(x => x.Id));
        vm.Workspace = vm.WorkspaceOptions[2];
        Dispatcher.UIThread.RunJobs();
        Assert.All(vm.Posts, p => Assert.Contains(p.Post.Artist, new[] { "Night Ferry", "Tube Band", "Brand New Act" }));
        Save(w, "16-workspace-feed");

        // two workspaces with one name are refused
        ta.AddSpaceCommand.Execute(null);
        ta.Spaces[^1].Name = "focus";
        await ta.SaveCommand.ExecuteAsync(null);
        Assert.Contains("Two workspaces", ta.Error);
        win.Close();
        w.Close();
    }

    [AvaloniaFact]
    public async Task FindMissingLinksOnTheRowsShownOrOneRow()
    {
        var (app, vm, w) = await OpenAsync();
        using var _ = app;
        var asked = new List<string>();
        app.Web.Extra = r =>
        {
            var q = StandIn.QueryOf(r.RequestUri!);
            if (r.RequestUri!.Host == "bandcamp.com" && r.RequestUri.AbsolutePath == "/search")
            {
                lock (asked) asked.Add(q["q"]);
                var name = q["q"];
                var slug = name.ToLowerInvariant().Replace(" ", "");
                return StandIn.Text($"<ul><li class=\"searchresult\"><div class=\"result-info\"><div class=\"itemtype\">ARTIST</div>" +
                    $"<div class=\"heading\"><a href=\"https://{slug}.bandcamp.com\">{name}</a></div>" +
                    $"<div class=\"itemurl\"><a href=\"https://{slug}.bandcamp.com\">https://{slug}.bandcamp.com</a></div></div></li></ul>");
            }
            if (r.RequestUri.Host == "api-v2.soundcloud.com" && r.RequestUri.AbsolutePath == "/search/users")
                return StandIn.Json(new JsonObject { ["collection"] = new JsonArray() });
            return null;
        };
        var ta = new TrackedArtistsViewModel(vm);
        var win = new TrackedArtistsWindow { DataContext = ta, Width = 1240, Height = 800 };
        win.Show();
        ta.PasteText = "Alpha Act\nBeta Act";
        ta.AddPastedCommand.Execute(null);
        var alpha = ta.Rows.Single(r => r.Name == "Alpha Act");
        var beta = ta.Rows.Single(r => r.Name == "Beta Act");
        Assert.True(alpha.HasOpen);

        // filtered: only the rows shown
        ta.FilterText = "beta";
        Assert.Equal("Find missing links (shown rows)", ta.FillLabel);
        await ta.FindMissingCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Beta Act" }, asked);
        Assert.Equal("betaact", beta.Bandcamp);
        Assert.True(beta.AutoBandcamp);
        Assert.Contains("Filled 1 cell", ta.FillStatus);
        ta.FilterText = "";
        Assert.Equal("Find missing links", ta.FillLabel);

        // one row's own button
        await ta.FindRowCommand.ExecuteAsync(alpha);
        Assert.Equal(new[] { "Beta Act", "Alpha Act" }, asked);
        Assert.Equal("alphaact", alpha.Bandcamp);
        Dispatcher.UIThread.RunJobs();
        Save(win, "17-find-missing-links");

        // a guess cleared is remembered, and not looked for again unless Rescan is ticked
        alpha.Bandcamp = "";
        Assert.False(alpha.AutoBandcamp);
        Assert.Contains("bandcamp", alpha.Skip);
        Assert.Equal(new[] { "alphaact" }, alpha.Rejected["bandcamp"]);
        await ta.FindRowCommand.ExecuteAsync(alpha);
        Assert.Equal("", alpha.Bandcamp);
        alpha.Rescan = true;
        await ta.FindRowCommand.ExecuteAsync(alpha);
        Assert.Equal("", alpha.Bandcamp);                 // the only result was turned down before
        Assert.False(alpha.Rescan);
        win.Close();
        w.Close();
    }

    [AvaloniaFact]
    public async Task LinkFinderLooksUpEditsAndSaves()
    {
        using var t = new LinkFinderTests.Lookups();
        await t.App.AddAsync(("bandcamp", "nightferry"), ("soundcloud", "nightferry"));
        var vm = new MainViewModel(t.App.Engine, t.App.Folder);
        var main = new MainWindow { DataContext = vm, Width = 1320, Height = 880 };
        main.Show();
        var lf = new LinkFinderViewModel(vm);
        var win = new LinkFinderWindow { DataContext = lf, Width = 1280, Height = 820 };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        lf.LookUpPastedCommand.Execute(null);
        Assert.Contains("Paste some rows", lf.LookupError);
        lf.PasteText = "#\tArtist\tTitle\n1\tNight Ferry\tintro\n2\tMasot\tEspecies Endémicas\n3\tIdpa & Saša Delimar\tState I\n" +
                       "4\tNobody Known\tZzz\n5\tGhost Person\tQqq Rrr\n6\tRiver\tUnheard Of Tune";
        lf.LookUpPastedCommand.Execute(null);
        Assert.Equal(6, lf.Rows.Count);
        for (int i = 0; i < 1500 && (lf.Busy || !lf.Rows.All(r => r.Done) || !lf.LookupStatus.StartsWith("Done")); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.False(lf.Busy);
        Assert.StartsWith("Done: 3 of 6 on Bandcamp", lf.LookupStatus);
        Assert.True(lf.CanExport);
        var masot = lf.Rows.Single(r => r.Artist == "Masot");
        Assert.Equal(("masot.bandcamp.com/album/flora", "Bandcamp release", "2023", "on Flora"), (masot.LinkText, masot.Tag, masot.Year, masot.ReleaseText));
        var river = lf.Rows.Single(r => r.Artist == "River");
        Assert.True(river.HasReview);                         // a profile picked by name alone
        Assert.True(river.CanLooksRight);
        Save(win, "18-link-finder");

        // set a link by hand: saved, and shown at once
        var ghost = lf.Rows.Single(r => r.Artist == "Ghost Person");
        lf.EditLinkCommand.Execute(ghost);
        Assert.True(ghost.Editing);
        ghost.EditUrl = "not a link";
        await lf.SaveLinkCommand.ExecuteAsync(ghost);
        Assert.Contains("web address", ghost.EditError);
        ghost.EditUrl = "https://example.org/ghost";
        ghost.EditYear = "2020";
        await lf.SaveLinkCommand.ExecuteAsync(ghost);
        Assert.False(ghost.Editing);
        Assert.Equal(("Link you set", "example.org/ghost", "2020"), (ghost.Tag, ghost.LinkText, ghost.Year));
        await lf.LooksRightCommand.ExecuteAsync(river);
        Assert.Equal("Link you set", river.Tag);
        Assert.False(river.HasReview);

        // the fallback setting re-points the rows on screen
        lf.Fallback = lf.FallbackOptions.Single(o => o.Value == "soundcloud");
        Assert.Equal("soundcloud", t.F.FallbackPref());
        lf.Fallback = lf.FallbackOptions.Single(o => o.Value == "bandcamp");

        // purchases and the Downloads button
        lf.FanName = "https://bandcamp.com/divided_eye";
        lf.ReadPurchasesCommand.Execute(null);
        Assert.Equal("divided_eye", lf.FanName);
        for (int i = 0; i < 500 && (t.F.Collection.Syncing || !lf.FanStatus.Contains("purchases")); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Contains("purchases", lf.FanStatus);
        Assert.True(vm.DownloadsOff);
        lf.DownloadsOn = true;
        Assert.False(vm.DownloadsOff);
        Assert.Contains("Nothing new to download", vm.DownloadsTip);

        // Saved results: filtered, sorted, spellings edited
        lf.Tab = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.True(lf.SavedRows.Count >= 4);
        Assert.Contains(lf.SavedFilterOptions, o => o.Text.StartsWith("Links you set (2)"));
        lf.SavedFilter = lf.SavedFilterOptions.Single(o => o.Value == "manual");
        Assert.Equal(2, lf.SavedRows.Count);
        lf.SortSaved("artist");
        Assert.Equal(new[] { "ghost person", "river" }, lf.SavedRows.Select(r => r.TidyArtist));
        var row = lf.SavedRows.First();
        lf.EditSpellingCommand.Execute(row);
        row.SpellArtist = "GHOST person";
        row.SpellTitle = "Qqq Rrr";
        lf.SaveSpellingCommand.Execute(row);
        Assert.Equal("GHOST person", lf.SavedRows.First().ListArtist);
        lf.SavedFilter = lf.SavedFilterOptions.Single(o => o.Value == "");
        Dispatcher.UIThread.RunJobs();
        Save(win, "19-link-finder-saved");
        win.Close();

        vm.RefreshDownloads();
        Dispatcher.UIThread.RunJobs();
        Save(main, "20-downloads-button");
        main.Close();
    }

    [AvaloniaFact]
    public async Task SettingsForLinkFinder()
    {
        var (app, vm, w) = await OpenAsync(data: false);
        using var _ = app;
        var s = new SettingsViewModel(vm);
        var win = new SettingsWindow { DataContext = s };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "bandcamp", "beatport", "soundcloud", "youtube", "discogs" }, s.LookupSites.Select(x => x.Key));
        Assert.False(s.LookupSites.Single(x => x.Key == "beatport").Available);        // switched off above
        s.Sites.Single(x => x.Key == "discogs").On = true;
        Assert.True(s.LookupSites.Single(x => x.Key == "discogs").Available);
        s.LookupSites.Single(x => x.Key == "discogs").On = true;
        Assert.True(s.ShowDiscogs);
        s.DiscogsToken = "dg-secret";
        s.SoundCloudMe = "https://soundcloud.com/MeFan";
        Assert.True(s.Dirty);
        Save(win, "21-settings-link-finder");
        s.SaveCommand.Execute(null);
        Assert.Equal("Saved", s.SavedNote);
        Assert.Contains("discogs", app.Engine.Sites.LookupSites());
        Assert.Equal("dg-secret", app.Engine.LinkFinder.DiscogsToken());
        Assert.True(s.HasDiscogsToken);
        Assert.Equal("", s.DiscogsToken);
        Assert.Equal("mefan", app.Engine.LinkFinder.SoundCloudMe());
        s.RemoveDiscogsTokenCommand.Execute(null);
        Assert.Equal("", app.Engine.LinkFinder.DiscogsToken());
        win.Close();
        w.Close();
    }
}
