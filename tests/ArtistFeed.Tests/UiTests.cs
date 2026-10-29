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

    /// <summary>Answers "are you sure?" questions and remembers them; nothing else is needed here.</summary>
    sealed class AskingUi(Func<string, bool> answer) : IUi
    {
        public List<(string Title, bool Danger)> Asked { get; } = new();
        public Task<bool> ConfirmAsync(string title, string message, string yes = "OK", string no = "Cancel", bool danger = false)
        {
            Asked.Add((title, danger));
            return Task.FromResult(answer(title));
        }
        public Task<string?> SaveFileAsync(string title, string suggestedName, string text) => throw new NotSupportedException();
        public Task<string?> OpenTextFileAsync(string title) => throw new NotSupportedException();
        public Task<string?> OpenDatabaseFileAsync() => throw new NotSupportedException();
        public Task<byte[]?> OpenTableFileAsync(string title) => throw new NotSupportedException();
        public Task ShowTrackedArtistsAsync() => throw new NotSupportedException();
        public Task ShowLinkFinderAsync() => throw new NotSupportedException();
        public Task ShowSettingsAsync() => throw new NotSupportedException();
        public void ShowPlayer(PlayItem item, PlayerPage page) => throw new NotSupportedException();
        public void HidePlayer() => throw new NotSupportedException();
    }

    [AvaloniaFact]
    public async Task ClearFollowsTheFilterAndSavingAnEmptyListWarnsOnceMore()
    {
        var (app, vm, w) = await OpenAsync();
        using var _ = app;
        var keepList = true;
        var ui = new AskingUi(title => !(title.StartsWith("Clear your whole") && keepList));
        var ta = new TrackedArtistsViewModel(vm) { Ui = ui };
        var win = new TrackedArtistsWindow { DataContext = ta, Width = 1240, Height = 800 };
        win.Show();
        var all = ta.Rows.Count(r => !r.IsBlank);
        Assert.True(all >= 2);
        Assert.Equal("Clear all", ta.ClearLabel);

        // filtered: only the rows shown go
        var first = ta.Rows.First(r => !r.IsBlank).Name;
        ta.FilterText = first;
        var shown = ta.Visible.Count(r => !r.IsBlank);
        Assert.Equal($"Clear {shown} shown row{(shown == 1 ? "" : "s")}", ta.ClearLabel);
        ta.ToggleFollowsCommand.Execute(null);               // a panel open: its button shows it
        Dispatcher.UIThread.RunJobs();
        Save(win, "26-clear-shown-rows");
        ta.ToggleFollowsCommand.Execute(null);
        await ta.ClearAllCommand.ExecuteAsync(null);
        Assert.Equal("Clear shown rows", ui.Asked[^1].Title);
        Assert.Equal(all - shown, ta.Rows.Count(r => !r.IsBlank));
        ta.FilterText = "";
        Assert.Equal("Clear all", ta.ClearLabel);

        // everything cleared, then Save: one more warning, with "Keep my list" as the safe default
        await ta.ClearAllCommand.ExecuteAsync(null);
        Assert.Equal(0, ta.Rows.Count(r => !r.IsBlank));
        await ta.SaveCommand.ExecuteAsync(null);
        Assert.Equal(("Clear your whole Tracked Artists list?", true), ui.Asked[^1]);
        Assert.Equal(all, app.Engine.Db.ListArtists().Count);        // kept
        Assert.True(ta.Dirty);
        keepList = false;
        await ta.SaveCommand.ExecuteAsync(null);
        Assert.Empty(app.Engine.Db.ListArtists());
        win.Close();
        w.Close();
    }

    [AvaloniaFact]
    public async Task FindMissingLinksForOneSiteAndOnlyRowsNotSearchedBefore()
    {
        var (app, vm, w) = await OpenAsync();
        using var _ = app;
        var bc = new List<string>();
        var sc = new List<string>();
        app.Web.Extra = r =>
        {
            var q = StandIn.QueryOf(r.RequestUri!);
            if (r.RequestUri!.Host == "bandcamp.com" && r.RequestUri.AbsolutePath == "/search")
            {
                lock (bc) bc.Add(q["q"]);
                var slug = q["q"].ToLowerInvariant().Replace(" ", "");
                return StandIn.Text($"<ul><li class=\"searchresult\"><div class=\"result-info\"><div class=\"itemtype\">ARTIST</div>" +
                    $"<div class=\"heading\"><a href=\"https://{slug}.bandcamp.com\">{q["q"]}</a></div>" +
                    $"<div class=\"itemurl\"><a href=\"https://{slug}.bandcamp.com\">https://{slug}.bandcamp.com</a></div></div></li></ul>");
            }
            if (r.RequestUri.Host == "api-v2.soundcloud.com" && r.RequestUri.AbsolutePath == "/search/users")
            {
                lock (sc) sc.Add(q["q"]);
                return StandIn.Json(new JsonObject { ["collection"] = new JsonArray() });
            }
            return null;
        };
        var ta = new TrackedArtistsViewModel(vm);
        var win = new TrackedArtistsWindow { DataContext = ta, Width = 1240, Height = 800 };
        win.Show();
        Assert.Equal("", ta.FillSite!.Value);                 // both sites, skipping rows searched before: the defaults
        Assert.True(ta.SkipSearched);
        ta.PasteText = "Gamma Act\nDelta Act";
        ta.AddPastedCommand.Execute(null);
        var gamma = ta.Rows.Single(r => r.Name == "Gamma Act");
        Assert.Empty(gamma.Searched);
        ta.FilterText = "act";                                // just these two rows

        // Bandcamp only
        ta.FillSite = ta.FillSiteOptions.Single(o => o.Value == "bandcamp");
        await ta.FindMissingCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Delta Act", "Gamma Act" }, bc.Order());
        Assert.Empty(sc);
        Assert.Equal("gammaact", gamma.Bandcamp);
        Assert.Equal(new[] { "bandcamp" }, gamma.Searched);

        // both sites: only SoundCloud is left to look for
        ta.FillSite = ta.FillSiteOptions[0];
        await ta.FindMissingCommand.ExecuteAsync(null);
        Assert.Equal(2, bc.Count);
        Assert.Equal(new[] { "Delta Act", "Gamma Act" }, sc.Order());
        Assert.Equal(new[] { "bandcamp", "soundcloud" }, gamma.Searched.Order());

        // nothing searched twice while Skip rows already searched is ticked
        await ta.FindMissingCommand.ExecuteAsync(null);
        Assert.Equal(2, sc.Count);
        Assert.Contains("searched before", ta.FillStatus);
        Dispatcher.UIThread.RunJobs();
        Save(win, "17b-find-missing-options");

        // a row's own Find links, Rescan, or unticking the box searches again
        await ta.FindRowCommand.ExecuteAsync(gamma);
        Assert.Equal(3, sc.Count);
        ta.SkipSearched = false;
        await ta.FindMissingCommand.ExecuteAsync(null);
        Assert.Equal(5, sc.Count);

        // remembered by name in the shared "suggest_searched" entry, and the choices kept
        var saved = app.Engine.Table.Searched();
        Assert.Equal(new[] { "bandcamp", "soundcloud" }, saved[ArtistTable.SearchKey("Gamma Act", new Dictionary<string, string>())].Keys.Order());
        var again = new TrackedArtistsViewModel(vm);
        Assert.False(again.SkipSearched);
        Assert.Equal("", again.FillSite!.Value);
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

    [AvaloniaFact]
    public async Task RelatedTracksAndListColumns()
    {
        var (app, vm, w) = await OpenAsync();
        using var _ = app;
        var replay = new WebReplay();
        int related = 0;
        app.Web.Extra = r =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/related")) Interlocked.Increment(ref related);
            return r.RequestUri.AbsolutePath.EndsWith("/related") ? replay.Answer(r) : null;
        };
        // the web version's checks on the related tracks themselves
        var (first, total) = await app.Engine.RelatedAsync("900");
        Assert.Equal(10, total);
        Assert.Equal(new[] { "Rel 0", "Rel 1", "Rel 2" }, first.Select(t => t["track"]!.GetValue<string>()));
        Assert.Equal(("Pub Artist", "Uploader 1", 181L), (first[0]["artist"]!.GetValue<string>(), first[1]["artist"]!.GetValue<string>(), (long)Html.AsNumber(first[0]["duration"])!));
        Assert.StartsWith("2025-01-01", first[0]["posted"]!.GetValue<string>());
        var (more, _) = await app.Engine.RelatedAsync("900", 3, 5);
        Assert.Equal(new[] { "Rel 3", "Rel 4", "Rel 5", "Rel 6", "Rel 7" }, more.Select(t => t["track"]!.GetValue<string>()));
        Assert.Equal(1, related);                               // read once, then handed out from the saved copy
        await Assert.ThrowsAsync<SourceException>(() => app.Engine.RelatedAsync("abc"));

        vm.ShowListCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var demo = vm.Rows.Single(r => r.Track == "Demo");
        Assert.True(demo.CanRelate);
        Assert.False(vm.Rows.Single(r => r.Track == "Intro").CanRelate);       // Bandcamp: no related tracks
        await vm.ToggleRelatedCommand.ExecuteAsync(demo);
        Assert.True(demo.RelatedOpen);
        Assert.Equal(3, demo.Related.Count);
        Assert.Equal("Show 5 more", demo.MoreRelatedLabel);
        await vm.MoreRelatedCommand.ExecuteAsync(demo);
        Assert.Equal(8, demo.Related.Count);
        Assert.Equal("Show 2 more", demo.MoreRelatedLabel);
        vm.PlayRelatedCommand.Execute(demo.Related[1]);
        Assert.True(demo.Related[1].IsPlaying);
        Dispatcher.UIThread.RunJobs();
        Save(w, "22-related-tracks");
        await vm.MoreRelatedCommand.ExecuteAsync(demo);
        Assert.Equal("That's all SoundCloud lists.", demo.RelatedFoot);
        await vm.ToggleRelatedCommand.ExecuteAsync(demo);
        Assert.False(demo.RelatedOpen);
        vm.ClosePlayerCommand.Execute(null);

        // Edit Columns: hide, reorder, remembered, reset
        Assert.Equal(new[] { "artist", "label", "release", "track", "duration", "year", "date", "posted", "site" }, vm.Columns.Select(c => c.Key));
        Assert.False(vm.Columns.Single(c => c.Key == "year").Shown);
        var grid = Find<DataGrid>(w, g => g.Name == "List");
        DataGridColumn Col(string key) => grid.Columns.Single(c => c.Tag as string == key);
        Assert.False(Col("year").IsVisible);
        vm.Columns.Single(c => c.Key == "label").Shown = false;
        vm.MoveColumnUpCommand.Execute(vm.Columns.Single(c => c.Key == "track"));
        vm.Columns.Single(c => c.Key == "year").Shown = true;
        Assert.False(Col("label").IsVisible);
        Assert.True(Col("track").DisplayIndex < Col("release").DisplayIndex);
        Assert.True(Col("year").IsVisible);
        Assert.False(vm.ColumnsDefault);
        Dispatcher.UIThread.RunJobs();
        Save(w, "23-list-columns");
        var again = new MainViewModel(app.Engine, app.Folder);                  // remembered
        Assert.Equal(new[] { "artist", "label", "track", "release" }, again.Columns.Take(4).Select(c => c.Key));
        Assert.False(again.Columns.Single(c => c.Key == "label").Shown);
        foreach (var c in vm.Columns.Where(c => c.Key != "artist")) c.Shown = false;
        Assert.False(vm.Columns.Single(c => c.Key == "artist").CanHide);        // always keep one column
        vm.ResetColumnsCommand.Execute(null);
        Assert.True(vm.ColumnsDefault);
        Assert.True(Col("label").IsVisible);
        Assert.False(Col("year").IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public async Task ShowNewPostsWhileARefreshRuns()
    {
        var (app, vm, w) = await OpenAsync();
        using var _ = app;
        Assert.False(vm.HasNewWaiting);
        vm.NewWaiting = 24;                                     // as UpdateStatus sets it from the running refresh
        Dispatcher.UIThread.RunJobs();
        var btn = Find<Button>(w, b => b.Content as string == "Show 24 new posts");
        Assert.True(btn.IsVisible);
        Save(w, "24-show-new-posts");
        vm.ShowNewCommand.Execute(null);
        Assert.False(vm.HasNewWaiting);
        w.Close();
    }

    [AvaloniaFact]
    public async Task RefreshDetailsBehindTheInfoIcon()
    {
        var (app, vm, w) = await OpenAsync();
        using var _ = app;
        var db = app.Engine.Db;
        // a quiet Bandcamp page: checked an hour ago, newest release long ago
        foreach (var a in db.ListAccounts().Where(a => a.Source == "bandcamp"))
        {
            db.Execute("UPDATE accounts SET last_checked=? WHERE id=?", DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), a.Id);
            db.Execute("UPDATE posts SET published_at='2020-01-01T00:00:00Z', posted_at=NULL WHERE account_id=?", a.Id);
        }
        await app.Engine.Refresher.RefreshAsync();
        vm.UpdateStatus();
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("Updated", vm.Status);
        Assert.DoesNotContain("quiet", vm.Status);                        // the line stays short
        Assert.True(vm.HasStatusDetails);
        Assert.Contains(vm.StatusDetails, l => l.StartsWith("Pages checked: "));
        Assert.Contains(vm.StatusDetails, l => l.Contains("1 quiet Bandcamp page "));
        Assert.True(vm.CanCheckQuiet);
        var info = Find<Button>(w, b => b.Name == "StatusInfo");
        info.Flyout!.ShowAt(info);
        Dispatcher.UIThread.RunJobs();
        Save(w, "25-refresh-details");
        info.Flyout.Hide();
        vm.CheckQuietCommand.Execute(null);
        for (int i = 0; i < 400 && (app.Engine.Refresher.Refreshing || app.Engine.Refresher.LastResult?.Resting != 0); i++) await Task.Delay(20);   // started, and finished
        await Task.Delay(100);
        vm.UpdateStatus();
        Assert.Equal(0, app.Engine.Refresher.LastResult!.Resting);
        Assert.False(vm.CanCheckQuiet);
        w.Close();
    }

    [AvaloniaFact]
    public async Task TrackedArtistsInNameOrderAndSortable()
    {
        var (app, vm, w) = await OpenAsync(data: false);
        using var _ = app;
        app.Engine.Table.Save(new List<TableRow>
        {
            new() { Name = "zed", Cells = { ["soundcloud"] = "zed" } },
            new() { Name = "Ōme", Cells = { ["soundcloud"] = "ome", ["bandcamp"] = "omemusic" } },
            new() { Name = "Alpha", Cells = { ["soundcloud"] = "alpha" } },
            new() { Name = "beta", Cells = { ["soundcloud"] = "beta", ["bandcamp"] = "betamusic" } },
        });
        var ta = new TrackedArtistsViewModel(vm);
        var win = new TrackedArtistsWindow { DataContext = ta, Width = 1240, Height = 800 };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        string[] Names() => ta.Visible.Where(r => !r.IsBlank).Select(r => r.Name).ToArray();
        Assert.Equal(new[] { "Alpha", "beta", "Ōme", "zed" }, Names());               // alphabetical, case and accents aside
        ta.SortBy("name");
        Assert.Equal(new[] { "Alpha", "beta", "Ōme", "zed" }, Names());
        ta.SortBy("name");
        Assert.Equal(new[] { "zed", "Ōme", "beta", "Alpha" }, Names());               // reversed
        ta.SortBy("bandcamp");
        Assert.Equal(new[] { "beta", "Ōme", "Alpha", "zed" }, Names());               // by Bandcamp; empty cells last
        ta.SortBy("bandcamp");
        Assert.Equal(new[] { "Ōme", "beta", "Alpha", "zed" }, Names());               // reversed, empty still last
        ta.SortBy("bandcamp");
        Assert.Equal(new[] { "Alpha", "beta", "Ōme", "zed" }, Names());               // third click: back to the loaded order
        Dispatcher.UIThread.RunJobs();
        Save(win, "26-tracked-artists-sorted");
        win.Close();
        w.Close();
    }

    [AvaloniaFact]
    public async Task ClickingATrackedArtistsHeadingSorts()
    {
        var (app, vm, w) = await OpenAsync(data: false);
        using var _ = app;
        app.Engine.Table.Save(new List<TableRow>
        {
            new() { Name = "zed", Cells = { ["soundcloud"] = "zed" } },
            new() { Name = "Alpha", Cells = { ["soundcloud"] = "alpha" } },
        });
        var ta = new TrackedArtistsViewModel(vm);
        var win = new TrackedArtistsWindow { DataContext = ta, Width = 1240, Height = 800 };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var header = Find<Avalonia.Controls.DataGridColumnHeader>(win, h => (h.Content as string ?? "").StartsWith("Artist"));
        var at = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), win)!.Value;
        win.MouseMove(at);
        win.MouseDown(at, MouseButton.Left);
        win.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("name", ta.SortKey);
        Assert.False(ta.SortDesc);
        win.MouseDown(at, MouseButton.Left);
        win.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(ta.SortDesc);
        Assert.Equal(new[] { "zed", "Alpha" }, ta.Visible.Where(r => !r.IsBlank).Select(r => r.Name));
        win.Close();
        w.Close();
    }

    [AvaloniaFact]
    public async Task ClickingAListHeadingSorts()
    {
        var (app, vm, w) = await OpenAsync();
        using var _ = app;
        vm.ShowListCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var header = Find<Avalonia.Controls.DataGridColumnHeader>(w, h => (h.Content as string ?? "").StartsWith("Artist"));
        var at = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), w)!.Value;
        Assert.Equal("posted", vm.SortKey);
        w.MouseMove(at);
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("artist", vm.SortKey);              // a click on the list's Artist heading sorts by it
        w.Close();
    }
}
