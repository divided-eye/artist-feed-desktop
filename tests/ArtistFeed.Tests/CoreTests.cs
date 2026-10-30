using System.Text.Json.Nodes;
using ArtistFeed.Core;
using Xunit;

namespace ArtistFeed.Tests;

/// <summary>The same checks as the web app's tests, against the same stand-in pages.</summary>
public class CoreTests
{
    [Fact]
    public void Normalize()
    {
        using var app = new TestApp();
        var bc = app.Engine.Bandcamp;
        var sc = app.Engine.SoundCloud;
        Assert.Equal(("nightferry.bandcamp.com", "https://nightferry.bandcamp.com"), bc.Normalize("NightFerry"));
        Assert.Equal("https://nightferry.bandcamp.com", bc.Normalize("https://nightferry.bandcamp.com/album/x").Url);
        Assert.Equal("https://music.customdomain.net", bc.Normalize("music.customdomain.net").Url);
        Assert.Equal(("nightferry", "https://soundcloud.com/nightferry"), sc.Normalize("https://soundcloud.com/NightFerry/tracks"));
        Assert.Equal("nightferry", sc.Normalize("@nightferry").Handle);
        foreach (var bad in new[] { "", "https://bandcamp.com" })
            Assert.Throws<SourceException>(() => bc.Normalize(bad));
        Assert.Throws<SourceException>(() => sc.Normalize("https://soundcloud.com/discover"));
        Assert.Equal("nightferry", ((ISource)bc).Short("nightferry.bandcamp.com"));
    }

    [Fact]
    public void Parsers()
    {
        var items = Bandcamp.ParseMusicGrid(StandIn.Grid, "https://nightferry.bandcamp.com");
        Assert.Equal(new[] { "first", "second", "third" }, items.Select(i => i.Url[(i.Url.LastIndexOf('/') + 1)..]));
        Assert.Equal("https://nightferry.bandcamp.com/album/first", items[0].Url);
        Assert.Equal(("Second", "Guest Person"), (items[1].Title, items[1].Author));
        Assert.EndsWith("a222_2.jpg", items[1].Image);
        Assert.EndsWith("a333_2.jpg", items[2].Image);
        var rel = Bandcamp.ParseRelease(StandIn.Release("First", "07 Mar 2025 00:00:00 GMT"), "https://x/album/first");
        Assert.Equal("2025-03-07T00:00:00Z", rel.PublishedAt);
        Assert.True(rel.IsRelease);

        var first = Bandcamp.ParseRelease(StandIn.First, "https://nightferry.bandcamp.com/album/first");
        Assert.Equal("album=111", first.Embed);
        Assert.Equal("Deep Label", first.Label);
        Assert.Equal("2025-03-05T16:00:00Z", first.PostedAt);
        Assert.Equal(new[] { ("Intro", (double?)61.5, (string?)null, "1001", "https://nightferry.bandcamp.com/track/intro"),
                             ("Long One", 3725, "Guest Person", "1002", "https://nightferry.bandcamp.com/track/long-one") },
            first.Tracks!.Select(t => (t.Title, t.Duration, t.Artist, t.ExtId, t.Url)));

        var second = Bandcamp.ParseRelease(StandIn.Second, "https://nightferry.bandcamp.com/track/second");
        Assert.Equal("track=222", second.Embed);
        Assert.Null(second.Label);                          // published by the artist's own page
        Assert.Equal("First", second.ReleaseName);
        Assert.Equal(225, second.Tracks!.Single().Duration);
    }

    [Fact]
    public void TrackNamesWrittenOutInADescription()
    {
        var text = "Includes unlimited streaming via the free Bandcamp app.\n\n1. Serpentine\n2. High Priest\n3. The Message\n" +
                   "4. Otherside\n6. Dema Dub\n7. Raw Signal (5:41)\n8. Unite The Tribes";
        Assert.Equal(new[] { "Serpentine", "High Priest", "The Message", "Otherside", "Dema Dub", "Raw Signal", "Unite The Tribes" },
            Bandcamp.ListedTitles(new[] { text }));
        Assert.Equal(new[] { "Disclosed", "Other", "Third" }, Bandcamp.ListedTitles(new[] { "A1: Disclosed\nA2 - Other\nB1. Third" }));
        Assert.Empty(Bandcamp.ListedTitles(new[] { "1. Only one line" }));
    }

    [Fact]
    public void TidyingNamesMatchesTheWebApp()
    {
        Assert.Equal("especies endemicas", Names.Norm("Especies Endémicas (Original Mix)"));
        Assert.Equal("nocase", Names.Norm("Nøcase"));
        Assert.Equal("voyage", Names.Norm("Voyage (Digital Download)"));
        Assert.Equal("tune", Names.Norm("Tune (WIP)"));
        Assert.Equal("tune", Names.Norm("Tune feat. Somebody"));
        Assert.Equal("∑1", Names.Norm("∑1"));
        Assert.Equal("myca$h", Names.Norm("Myca$h"));
        Assert.Equal("tune", Names.Norm("★ Tune ★"));
        Assert.Equal("strasse", Names.Norm("Straße"));
        Assert.Equal("кино", Names.Norm("Кино"));
        Assert.Equal(new[] { "Night Ferry", "NF", "Ferry, Night" }, Names.SplitNames("Night Ferry, NF, \"Ferry, Night\", night ferry"));
        Assert.Equal("Night Ferry, NF, \"Ferry, Night\"", Names.JoinNames(new[] { "Night Ferry", "NF", "Ferry, Night" }));
    }

    [Fact]
    public async Task AddAndFeed()
    {
        using var app = new TestApp();
        await app.AddAsync(("bandcamp", "nightferry"), ("soundcloud", "nightferry"));
        var artist = app.Engine.Db.ListArtists().Single();
        Assert.Equal("Night Ferry", artist.Name);                // taken from the page
        var bc = artist.Accounts.Single(a => a.Source == "bandcamp");
        Assert.Contains("1 older release", bc.LastError);        // MaxNew=2 of 3
        var feed = app.Feed();
        Assert.Equal(new[] { "Demo", "Second", "First", "No Art" }, feed.Select(p => p.Title));
        Assert.EndsWith("artworks-9-t500x500.jpg", feed[0].Image);
        Assert.EndsWith("avatars-1-t500x500.jpg", feed[3].Image);

        await app.Engine.Refresher.RefreshAsync();                // second pass picks up the rest, no duplicates
        Assert.Equal(new[] { "Demo", "Second", "First", "Third", "No Art" }, app.Feed().Select(p => p.Title));
        Assert.Equal(new[] { "Second", "First", "Third" }, app.Feed(new FeedFilter { Source = "bandcamp" }).Select(p => p.Title));
        Assert.Null(app.Engine.Db.ListArtists()[0].Accounts[0].LastError);
        Assert.Equal(2, app.Engine.Refresher.LastResult!.Pages);        // both pages were read
        Assert.NotNull(app.Engine.Db.KvGet("last_refresh"));
    }

    static async Task<TestApp> NightFerryAsync()
    {
        var app = new TestApp();
        await app.AddAsync(("bandcamp", "nightferry"), ("soundcloud", "nightferry"));
        await app.Engine.Refresher.RefreshAsync();
        return app;
    }

    [Fact]
    public async Task TrackList()
    {
        using var app = await NightFerryAsync();
        var rows = app.Tracks();
        Assert.Equal(new[] { ("Demo EP", "Demo"), ("First", "Second"), ("", "No Art"), ("First", "Intro"), ("First", "Long One"), ("Third", "") },
            rows.Select(r => (r.Release, r.Track)));
        var (demo, second, intro, longOne, third) = (rows[0], rows[1], rows[3], rows[4], rows[5]);
        Assert.Equal(("SC Label", (double?)200.0, "soundcloud"), (demo.Label, demo.Duration, demo.Source));
        Assert.Equal(((double?)225.0, "track=222", ""), (second.Duration, second.Embed, second.Label));
        Assert.Equal(("Deep Label", "album=111", "1001", "Night Ferry"), (intro.Label, intro.Embed, intro.TrackExtId, intro.Artist));
        Assert.Equal(("Guest Person", "https://nightferry.bandcamp.com/track/long-one"), (longOne.Artist, longOne.Url));
        Assert.Null(third.Duration);
        Assert.Null(third.Embed);
        // posted = when it went up on the site; blank when the site doesn't say
        Assert.Equal(new[] { "2026-10-01", "", "2020-01-01", "2025-03-05", "2025-03-05", "" },
            rows.Select(r => (r.Posted ?? "").Length >= 10 ? r.Posted![..10] : ""));
        Assert.Equal(new[] { "2026-10-01", "2026-09-01", "2026-08-01", "2025-03-07", "2025-03-07", "2024-01-15" },
            rows.Select(r => r.PublishedAt![..10]));
        Assert.Equal(new[] { "No Art", "Intro", "Long One", "Demo", "Second", "" }, app.Tracks(sort: "posted", asc: true).Select(r => r.Track));
        Assert.Equal(new[] { "", "Intro" }, app.Tracks(sort: "released", asc: true).Take(2).Select(r => r.Track));
        Assert.Equal(new[] { "Long One", "Second" }, app.Tracks(sort: "duration").Take(2).Select(r => r.Track));
        Assert.Equal("Third", app.Tracks(sort: "duration", asc: true)[^1].Release);       // unknown durations last
        Assert.Equal(new[] { "Demo", "Intro", "Long One", "No Art", "Second", "" }, app.Tracks(sort: "track", asc: true).Select(r => r.Track));
        Assert.Equal(new[] { "Long One" }, app.Tracks(new FeedFilter { Query = "guest" }).Select(r => r.Track));
        Assert.Equal(new[] { "Intro" }, app.Tracks(new FeedFilter { Query = "deep intro" }).Select(r => r.Track));
        Assert.Equal(new[] { "No Art" }, app.Tracks(new FeedFilter { Source = "soundcloud" }, limit: 1, offset: 1).Select(r => r.Track));
        Assert.Empty(app.Tracks(new FeedFilter { Query = "100%" }));

        // filters: year, label ("-" = none), type, length; shared by list, grid and export
        var facets = app.Engine.Db.GetFacets();
        Assert.Equal(new[] { "2026", "2025", "2024" }, facets.Years);
        Assert.Equal(new[] { "Deep Label", "SC Label" }, facets.Labels);
        Assert.Equal(0, facets.Hidden);
        Assert.Equal(new[] { "Intro", "Long One" }, app.Tracks(new FeedFilter { Year = "2025" }).Select(r => r.Track));
        Assert.Equal(new[] { "Long One" }, app.Tracks(new FeedFilter { Label = "Deep Label", Length = "extended" }).Select(r => r.Track));
        Assert.Equal(new[] { "Second", "No Art", "" }, app.Tracks(new FeedFilter { Label = "-" }).Select(r => r.Track));
        Assert.Equal(new[] { "Demo", "Second" }, app.Tracks(new FeedFilter { Kind = "track", Length = "medium" }).Select(r => r.Track));
        Assert.Equal(new[] { "Intro" }, app.Tracks(new FeedFilter { Length = "short" }).Select(r => r.Track));
        Assert.Equal(new[] { "Third" }, app.Tracks(new FeedFilter { Kind = "album", Year = "2024" }).Select(r => r.Release));
        Assert.Equal(new[] { "First" }, app.Feed(new FeedFilter { Length = "extended" }).Select(p => p.Title));
        Assert.Equal(new[] { "Third" }, app.Feed(new FeedFilter { Label = "-", Kind = "album" }).Select(p => p.Title));
        Assert.Equal(new[] { "Second" }, app.Feed(new FeedFilter { Year = "2026", Source = "bandcamp" }).Select(p => p.Title));

        // "Hide no-preview tracks": a Bandcamp track with no length has no stream yet.
        app.Engine.Db.Execute("UPDATE tracks SET duration=NULL WHERE title='Intro'");
        Assert.Equal(new[] { "Demo", "Second", "No Art", "Long One", "" }, app.Tracks(new FeedFilter { PreviewOnly = true }).Select(r => r.Track));
        Assert.Contains("First", app.Feed(new FeedFilter { PreviewOnly = true }).Select(p => p.Title));
        app.Engine.Db.Execute("UPDATE tracks SET duration=NULL WHERE title='Long One'");
        Assert.DoesNotContain("First", app.Feed(new FeedFilter { PreviewOnly = true }).Select(p => p.Title));
        Assert.Contains("First", app.Feed().Select(p => p.Title));
        app.Engine.Db.Execute("UPDATE tracks SET duration=61.5 WHERE title='Intro'");
        app.Engine.Db.Execute("UPDATE tracks SET duration=3725 WHERE title='Long One'");

        var firstPost = app.Feed(new FeedFilter { Query = "intro" }).First();
        Assert.Equal(("First", "album=111", 2L, (double?)3786.5), (firstPost.Title, firstPost.Embed, firstPost.TrackCount, firstPost.Duration));

        var lines = FeedExport.TracksCsv(app.Tracks(sort: "duration"), app.Engine.Sites).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("artist,label,release,track,duration,year,released,posted,site,url", lines[0]);
        Assert.Equal("Guest Person,Deep Label,First,Long One,1:02:05,2025,2025-03-07,2025-03-05,Bandcamp,https://nightferry.bandcamp.com/track/long-one", lines[1]);
        Assert.Equal(7, lines.Length);
    }

    [Fact]
    public async Task DateRangeFilters()
    {
        using var app = await NightFerryAsync();
        // posted date, falling back to the release date when the site gives none
        Assert.Equal(new[] { "Demo", "Second" }, app.Feed(new FeedFilter { Since = "2026-09-01" }).Select(p => p.Title));
        Assert.Equal(new[] { "Second", "No Art" }, app.Feed(new FeedFilter { Since = "2026-08-01", DateByReleased = true, Until = "2026-09-30" }).Select(p => p.Title));
        Assert.Equal(new[] { "No Art" }, app.Feed(new FeedFilter { Until = "2020-12-31" }).Select(p => p.Title));
    }

    [Fact]
    public async Task HidingAndListenedTo()
    {
        using var app = await NightFerryAsync();
        var db = app.Engine.Db;
        var feed = app.Feed();
        var first = feed.Single(p => p.Title == "First");
        Assert.False(first.Hidden);
        Assert.False(first.Played);
        List<(string, string, bool, bool)> Rows(FeedFilter? f = null) => app.Tracks(f).Where(r => r.PostId == first.Id)
            .Select(r => (r.Release, r.Track, r.Played, r.Hidden)).ToList();

        var intro = app.Tracks().Single(r => r.Track == "Intro");
        Assert.Equal("1001", intro.TrackKey);
        db.SetPlayed(first.Id, intro.TrackKey);
        Assert.Equal(new[] { ("First", "Intro", true, false), ("First", "Long One", false, false) }, Rows());
        Assert.True(app.Feed().Single(p => p.Id == first.Id).Played);
        db.SetPlayed(first.Id, "");
        Assert.All(Rows(), r => Assert.True(r.Item3));
        await app.Engine.Refresher.RefreshAsync();                // survives a refresh re-reading the release
        Assert.All(Rows(), r => Assert.True(r.Item3));
        db.SetPlayed(first.Id, null, played: false);
        Assert.All(Rows(), r => Assert.False(r.Item3));

        Assert.True(db.SetHidden(first.Id, true));
        Assert.Equal(1, db.GetFacets().Hidden);
        Assert.DoesNotContain("First", app.Feed().Select(p => p.Title));
        Assert.Empty(Rows());
        Assert.Equal(new[] { ("First", "Intro", false, true), ("First", "Long One", false, true) }, Rows(new FeedFilter { ShowHidden = true }));
        await app.Engine.Refresher.RefreshAsync();                // a refresh doesn't bring it back
        Assert.DoesNotContain("First", app.Feed().Select(p => p.Title));
        db.SetHidden(first.Id, false);
        Assert.Equal(0, db.GetFacets().Hidden);
        Assert.Equal(feed.Select(p => p.Title), app.Feed().Select(p => p.Title));
        Assert.False(db.SetHidden(999999, true));
        Assert.False(db.SetPlayed(999999, ""));

        // one track of a release, hidden from the list on its own
        Assert.True(db.SetTrackHidden(first.Id, "1001", true));
        Assert.Equal(new[] { "Long One" }, Rows().Select(r => r.Item2));
        Assert.Contains("First", app.Feed().Select(p => p.Title));
        Assert.Equal(1, db.GetFacets().Hidden);
        db.SetTrackHidden(first.Id, "1001", false);
        Assert.Equal(2, Rows().Count);
    }

    [Fact]
    public async Task ReleaseTrackedThroughTwoPagesShowsOnce()
    {
        using var app = await NightFerryAsync();
        var before = app.Tracks().Select(r => (r.Release, r.Track, r.Label)).ToList();
        await app.AddAsync(("bandcamp", "deeplabel"));
        var label = app.Engine.Db.ListArtists().Single(a => a.Name == "Deep Label");
        Assert.Equal(1, label.PostCount);
        Assert.Equal(before, app.Tracks().Select(r => (r.Release, r.Track, r.Label)));            // no second copy
        Assert.Equal(1, app.Feed().Count(p => p.Title == "First"));
        Assert.Equal(new[] { "Intro", "Long One" }, app.Tracks(new FeedFilter { ArtistId = label.Id }).Select(r => r.Track));
        Assert.Equal(new[] { "First" }, app.Feed(new FeedFilter { ArtistId = label.Id }).Select(p => p.Title));
        app.Engine.Db.Execute("DELETE FROM artists WHERE id=?", label.Id);
        Assert.Equal(before, app.Tracks().Select(r => (r.Release, r.Track, r.Label)));            // the other copy is still there
    }

    [Fact]
    public async Task SingleReleaseAndErrors()
    {
        using var app = new TestApp();
        await app.AddAsync(("bandcamp", "solo"));
        var solo = app.Feed().Single();
        Assert.Equal(("Only One", "https://solo.bandcamp.com/album/only-one"), (solo.Title, solo.Url));
        await app.AddAsync(("soundcloud", "ghost"));
        var ghost = app.Engine.Db.ListArtists().Single(a => a.Accounts.Any(x => x.Handle == "ghost"));
        Assert.Contains("not found", ghost.Accounts[0].LastError);
    }

    [Fact]
    public async Task ExpiredSoundCloudKeyIsReplaced()
    {
        using var app = await NightFerryAsync();
        app.Engine.Db.KvSet("soundcloud_client_id", new string('b', 32));
        await app.Engine.Refresher.RefreshAsync();
        Assert.Equal(StandIn.Cid, app.Engine.Db.KvGet("soundcloud_client_id"));
        Assert.Null(app.Engine.Db.ListAccounts().Single(a => a.Source == "soundcloud").LastError);
    }

    [Fact]
    public async Task Table()
    {
        using var app = new TestApp();
        var table = app.Engine.Table;
        var added = table.Save(new[] { new TableRow { Cells = { ["bandcamp"] = "nightferry", ["soundcloud"] = "nightferry" } } });
        Assert.Equal(2, added.Count);
        await app.Engine.Refresher.RefreshAsync(added);
        await app.Engine.Refresher.RefreshAsync();
        var rows = table.Rows();
        Assert.Equal("Night Ferry", rows[0].Name);
        Assert.Equal(new Dictionary<string, string> { ["bandcamp"] = "nightferry", ["soundcloud"] = "nightferry", ["youtube"] = "" }, rows[0].Cells);
        var before = app.Feed(new FeedFilter { Source = "bandcamp" }).Count;
        var nf = rows[0].Id;

        // Invalid rows: nothing is saved, each problem is tied to its row.
        var bad = Assert.Throws<TableSaveException>(() => table.Save(new[]
        {
            new TableRow { Id = nf, Name = "Renamed", Cells = { ["bandcamp"] = "nightferry", ["soundcloud"] = "" } },
            new TableRow { Name = "Dup", Cells = { ["bandcamp"] = "https://nightferry.bandcamp.com" } },
            new TableRow { Name = "Bad", Cells = { ["soundcloud"] = "not a profile!" } },
        }));
        Assert.Equal(new[] { 1, 2 }, bad.Errors.Select(e => e.Row).OrderBy(x => x));
        Assert.Equal("Night Ferry", table.Rows().Single().Name);

        // Rename, drop one page, add a new artist with two pages in one cell, ignore a blank row.
        added = table.Save(new[]
        {
            new TableRow { Id = nf, Name = "NF", Cells = { ["bandcamp"] = "nightferry", ["soundcloud"] = "" } },
            new TableRow(),
            new TableRow { Cells = { ["bandcamp"] = "solo, nightferry2", ["soundcloud"] = "soundcloud.com/nightferry" } },
        });
        Assert.Equal(3, added.Count);
        await app.Engine.Refresher.RefreshAsync(added);
        rows = table.Rows();
        Assert.Equal(new[] { "NF", "Night Ferry" }, rows.Select(r => r.Name));
        Assert.Equal(new Dictionary<string, string> { ["bandcamp"] = "nightferry", ["soundcloud"] = "", ["youtube"] = "" }, rows[0].Cells);
        Assert.Equal(new Dictionary<string, string> { ["bandcamp"] = "solo, nightferry2", ["soundcloud"] = "nightferry", ["youtube"] = "" }, rows[1].Cells);
        Assert.Contains(rows[1].Problems, p => p.Contains("not found"));
        Assert.Equal(before, app.Feed(new FeedFilter { ArtistId = nf, Source = "bandcamp" }).Count);
        Assert.Empty(app.Feed(new FeedFilter { ArtistId = nf, Source = "soundcloud" }));

        var csv = table.ExportCsv().Split("\r\n");
        Assert.Equal("name,bandcamp,soundcloud,youtube", csv[0]);
        Assert.Equal("Night Ferry,\"solo, nightferry2\",nightferry,", csv[2]);

        // Two rows for one artist are refused until merged, or kept apart on purpose.
        var same = Assert.Throws<TableSaveException>(() => table.Save(new[]
        {
            new TableRow { Id = nf, Name = "NF", Cells = { ["bandcamp"] = "nightferry" } },
            new TableRow { Name = "nf", Cells = { ["soundcloud"] = "someoneelse" } },
        }));
        Assert.All(same.Errors, e => Assert.Equal("same_name", e.Kind));
        table.Save(new[]
        {
            new TableRow { Id = nf, Name = "NF", Cells = { ["bandcamp"] = "nightferry" } },
            new TableRow { Name = "nf", Cells = { ["soundcloud"] = "someoneelse" }, KeepSeparate = true },
        });
        Assert.All(table.Rows(), r => Assert.True(r.SameName));

        // Leaving a row out removes that artist; an empty table clears the list.
        table.Save(new[] { new TableRow { Id = nf, Name = "NF", Cells = { ["bandcamp"] = "nightferry" } } });
        Assert.Equal(new[] { "NF" }, table.Rows().Select(r => r.Name));
        table.Save(Array.Empty<TableRow>());
        Assert.Empty(table.Rows());
        Assert.Empty(app.Feed());
    }

    [Fact]
    public async Task FocusAndOtherNames()
    {
        using var app = await NightFerryAsync();
        await app.AddAsync(("bandcamp", "solo"));
        var rows = app.Engine.Table.Rows();
        var nf = rows.Single(r => r.Name == "Night Ferry");
        nf.Name = "Night Ferry, NF";
        nf.Focus = true;
        nf.YoutubeLookup = true;
        app.Engine.Table.Save(rows);
        Assert.Contains("night ferry", app.Engine.Sites.FocusNames());
        Assert.Equal(new[] { "night ferry", "nf" }.OrderBy(x => x), app.Engine.Sites.YoutubeNames().OrderBy(x => x));
        var focus = app.Engine.WithFocus(new FeedFilter(), true);
        Assert.Equal(new[] { "Demo", "Second", "First", "Third", "No Art" }, app.Feed(focus).Select(p => p.Title));
        Assert.Contains("Only One", app.Feed().Select(p => p.Title));
        var again = app.Engine.Table.Rows().Single(r => r.Focus);
        Assert.Equal("Night Ferry, NF", again.Name);
        Assert.Equal(true, again.YoutubeLookup);
    }

    [Fact]
    public async Task YouTubeChannelsAndSwitchingASiteOff()
    {
        using var app = new TestApp();
        var yt = app.Engine.YouTube;
        Assert.Equal(("@tubeband", "https://www.youtube.com/@tubeband"), yt.Normalize("https://www.youtube.com/@TubeBand/videos"));
        Assert.Equal("UC1234567890123456789012", yt.Normalize("youtube.com/channel/UC1234567890123456789012").Handle);
        Assert.Equal("c/oldname", yt.Normalize("https://youtube.com/c/oldname").Handle);
        Assert.Equal("c/SomeName", yt.Normalize("https://www.youtube.com/SomeName").Handle);
        Assert.Equal("@tubeband", yt.Normalize("tubeband").Handle);
        Assert.Throws<SourceException>(() => yt.Normalize("https://www.youtube.com/watch?v=abc"));

        await app.AddAsync(("youtube", "@tubeband"));
        var feed = app.Feed();
        Assert.Equal(new[] { "Tube Band - Glass River", "Studio Diary" }, feed.Select(p => p.Title));
        Assert.Equal("vid00000001", feed[0].Embed);
        Assert.Equal("https://i.ytimg.com/vi/vid00000002/hqdefault.jpg", feed[1].Image);
        Assert.Equal("Tube Band", app.Engine.Db.ListArtists().Single().Name);
        var acc = app.Engine.Db.ListAccounts().Single();
        Assert.Equal("UC1234567890123456789012", acc.Meta["channel_id"]!.GetValue<string>());

        app.Engine.Sites.SetOn("youtube", false);
        Assert.Empty(app.Feed());
        Assert.DoesNotContain("youtube", app.Engine.Table.Rows().Single().Cells.Keys);
        // saving the table while YouTube is off leaves the YouTube page alone
        app.Engine.Table.Save(app.Engine.Table.Rows());
        Assert.Single(app.Engine.Db.ListAccounts());
        app.Engine.Sites.SetOn("youtube", true);
        Assert.Equal(2, app.Feed().Count);
        app.Engine.Sites.SetOn("bandcamp", false);
        app.Engine.Sites.SetOn("soundcloud", false);
        Assert.Throws<InvalidOperationException>(() => app.Engine.Sites.SetOn("youtube", false));
    }

    [Fact]
    public async Task SoundCloudProfileWithAnEmptyTracksList()
    {
        using var app = new TestApp();
        app.Web.Extra = req =>
        {
            var u = req.RequestUri!;
            if (u.Host != "api-v2.soundcloud.com") return null;
            JsonNode? body = u.AbsolutePath switch
            {
                // a date-windowed list: an empty first page pointing on to an older one
                "/users/42/tracks" when !u.Query.Contains("offset=") => JsonNode.Parse(
                    "{\"collection\": [], \"next_href\": \"https://api-v2.soundcloud.com/users/42/tracks?offset=2024-01-01&limit=30\"}"),
                "/users/42/tracks" => JsonNode.Parse(
                    "{\"collection\": [{\"kind\":\"track\",\"id\":950,\"title\":\"Old One\",\"permalink_url\":\"https://soundcloud.com/nightferry/old-one\",\"created_at\":\"2023-05-01T00:00:00Z\",\"user\":{\"username\":\"Night Ferry\"}}, {\"kind\":\"track\",\"id\":951}]}"),
                "/tracks" => JsonNode.Parse(
                    "[{\"kind\":\"track\",\"id\":951,\"title\":\"Bare\",\"permalink_url\":\"https://soundcloud.com/nightferry/bare\",\"created_at\":\"2022-01-01T00:00:00Z\",\"user\":{\"username\":\"Night Ferry\"}}]"),
                "/users/42" => JsonNode.Parse("{\"track_count\": 2, \"permalink\": \"nightferry\"}"),
                _ => null,
            };
            return body == null ? null : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            };
        };
        await app.AddAsync(("soundcloud", "nightferry"));
        Assert.Equal(new[] { "Old One", "Bare" }, app.Feed().Select(p => p.Title));
        Assert.Null(app.Engine.Db.ListAccounts().Single().LastError);
    }

    [Fact]
    public async Task BandcampPauseHoldsTheFeedBack()
    {
        using var app = new TestApp();
        using (app.Engine.Pace.Foreground())          // a lookup is running: the feed stands aside
        {
            await app.AddAsync(("bandcamp", "nightferry"));
            Assert.Empty(app.Feed());
            Assert.Equal(1, app.Engine.Refresher.Held);
        }
        await app.Engine.Refresher.RefreshAsync();
        Assert.Equal(2, app.Feed().Count);
    }

    [Fact]
    public async Task ProgressEstimateAndRefreshQueue()
    {
        using var app = await NightFerryAsync();
        Assert.Null(app.Engine.Refresher.CurrentProgress());
        var summary = app.Engine.Refresher.LastResult!;
        Assert.Equal((2, 2, 0), (summary.Pages, summary.Checked, summary.Errors));
        Assert.Equal("started", app.Engine.Refresher.RequestRefresh());
        for (int i = 0; i < 200 && !app.Engine.Refresher.Refreshing; i++) await Task.Delay(5);
        for (int i = 0; i < 400 && app.Engine.Refresher.Refreshing; i++) await Task.Delay(10);
        Assert.False(app.Engine.Refresher.Refreshing);
    }

    [Fact]
    public void ImportingAndPasting()
    {
        using var app = new TestApp();
        var cols = app.Engine.Sites.All;
        var rows = TableImport.FromPaste("Name\tBandcamp\nNight Ferry\nhttps://somelabel.bandcamp.com\nAnother, https://soundcloud.com/another, anotherbc", cols);
        Assert.Equal(3, rows.Count);
        Assert.Equal("Night Ferry", rows[0].Name);
        Assert.Equal(("", "https://somelabel.bandcamp.com"), (rows[1].Name, rows[1].Cells["bandcamp"]));
        Assert.Equal(("Another", "https://soundcloud.com/another", "anotherbc"), (rows[2].Name, rows[2].Cells["soundcloud"], rows[2].Cells["bandcamp"]));
    }

    [Fact]
    public void OpensAWebAppDatabaseAndShowsTimes()
    {
        Assert.Equal("1:02:05", Show.Clock(3725));
        Assert.Equal("3:45", Show.Clock(225));
        Assert.Equal("", Show.Clock(null));
        var now = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        Assert.Equal("just now", Show.Ago("2026-10-20T11:59:30Z", now));
        Assert.Equal("5m ago", Show.Ago("2026-10-20T11:55:00Z", now));
        Assert.Equal("3h ago", Show.Ago("2026-10-20T09:00:00Z", now));
        Assert.Equal("2d ago", Show.Ago("2026-10-18T12:00:00Z", now));

        // An older database gains the columns added later, and opening it twice is safe.
        var folder = Path.Combine(Path.GetTempPath(), "af-old-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "feed.db");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE posts(id INTEGER PRIMARY KEY, account_id INTEGER, external_id TEXT, title TEXT," +
                              " url TEXT, image TEXT, kind TEXT, author TEXT, published_at TEXT, first_seen TEXT);" +
                              "INSERT INTO posts(title) VALUES('kept');";
            cmd.ExecuteNonQuery();
        }
        using (var db = new Db(path)) { }
        using (var db = new Db(path))
        {
            var row = db.Query("SELECT title, label, detailed FROM posts").Single();
            Assert.Equal(("kept", null, 0L), ((string)row["title"]!, row["label"], (long)row["detailed"]!));
        }
        Directory.Delete(folder, true);
    }

    /// <summary>herbalistekmusic.bandcamp.com/album/52-blue shows a release with no tracks and nothing to buy;
    /// its head links Observatory Records' page, which sells it.</summary>
    [Fact]
    public async Task AReleaseSoldFromAnotherPageLinksToThatPage()
    {
        const string realUrl = "https://observatory-records.bandcamp.com/album/52-blue";
        var by = new JsonObject { ["byArtist"] = new JsonObject { ["name"] = "Herbalistek" } };
        var shell = StandIn.Release("52 Blue", "20 Jan 2026 00:00:00 GMT", ldExtra: by, head: $"<link rel=\"canonical\" href=\"{realUrl}\">");
        var tralbum = new JsonObject
        {
            ["id"] = 777, ["item_type"] = "album", ["artist"] = "Herbalistek",
            ["trackinfo"] = new JsonArray(
                new JsonObject { ["title"] = "52 Blue", ["duration"] = 300, ["track_id"] = 7001 },
                new JsonObject { ["title"] = "Deep Water", ["duration"] = 280, ["track_id"] = 7002 }),
        }.ToJsonString();
        var real = StandIn.Release("52 Blue", "20 Jan 2026 00:00:00 GMT", ldExtra: new JsonObject
            {
                ["byArtist"] = new JsonObject { ["name"] = "Herbalistek" },
                ["publisher"] = new JsonObject { ["name"] = "Observatory Records" },
            })
            .Replace("</head>", "</head><body><script data-tralbum='" + tralbum + "'></script></body>")
            .Replace("<head>", "<head><meta name=\"bc-page-properties\" content='{\"item_type\":\"a\",\"item_id\":777}'>");
        const string grid = "<html><head><meta property=\"og:site_name\" content=\"Herbalistek\"></head><body><ol id=\"music-grid\">" +
                            "<li><a href=\"/album/52-blue\"><p class=\"title\">52 Blue</p></a></li></ol></body></html>";
        const string label = "<html><head><meta property=\"og:site_name\" content=\"Observatory Records\"></head><body><ol id=\"music-grid\">" +
                             "<li><a href=\"/album/52-blue\"><p class=\"title\">52 Blue</p></a></li></ol></body></html>";

        Assert.Equal(realUrl, Bandcamp.ParseRelease(shell, "https://herbalistekmusic.bandcamp.com/album/52-blue").SoldAt);
        Assert.Null(Bandcamp.ParseRelease(real, realUrl).SoldAt);                         // has tracks: it is the real page
        Assert.Null(Bandcamp.ParseRelease(StandIn.Release("Other", "01 Jan 2026 00:00:00 GMT",
            head: "<link rel=\"canonical\" href=\"https://someone.bandcamp.com/album/other\">"), "https://someone.bandcamp.com/album/other").SoldAt);

        using var app = new TestApp();
        app.Web.Extra = r => (r.RequestUri!.Host, r.RequestUri.AbsolutePath) switch
        {
            ("herbalistekmusic.bandcamp.com", "/music") => StandIn.Text(grid),
            ("herbalistekmusic.bandcamp.com", "/album/52-blue") => StandIn.Text(shell),
            ("observatory-records.bandcamp.com", "/music") => StandIn.Text(label),
            ("observatory-records.bandcamp.com", "/album/52-blue") => StandIn.Text(real),
            _ => null,
        };
        await app.AddAsync(("bandcamp", "herbalistekmusic"));
        var post = Assert.Single(app.Feed());
        Assert.Equal(realUrl, post.Url);
        Assert.Equal("album=777", post.Embed);
        Assert.Equal(new[] { "52 Blue", "Deep Water" }, app.Tracks().Select(t => t.Track));
        Assert.All(app.Tracks(), t => Assert.Equal("Observatory Records", t.Label));
        // tracking the label too: still shown once
        await app.AddAsync(("bandcamp", "observatory-records"));
        Assert.Single(app.Feed());
    }

    /// <summary>A page showing a release with no tracks, nothing to buy and no link to where it is sold:
    /// one Bandcamp search for it, and a note when that finds nothing.</summary>
    [Fact]
    public async Task AReleaseWithNothingToBuyAndNoLinkIsSearchedForOnceThenNoted()
    {
        var by = new JsonObject { ["byArtist"] = new JsonObject { ["name"] = "Herbalistek" } };
        const string zero = "<meta property=\"og:description\" content=\"0 track album\">";
        var foundShell = StandIn.Release("52 Blue", "20 Jan 2026 00:00:00 GMT", ldExtra: by, head: zero);
        var lostShell = StandIn.Release("Lost One", "20 Jan 2026 00:00:00 GMT", ldExtra: by, head: zero);
        var tralbum = new JsonObject
        {
            ["id"] = 778, ["item_type"] = "album",
            ["trackinfo"] = new JsonArray(new JsonObject { ["title"] = "52 Blue", ["duration"] = 300, ["track_id"] = 7101 }),
        }.ToJsonString();
        var real = StandIn.Release("52 Blue", "20 Jan 2026 00:00:00 GMT", ldExtra: new JsonObject
            {
                ["byArtist"] = new JsonObject { ["name"] = "Herbalistek" },
                ["publisher"] = new JsonObject { ["name"] = "Observatory Records" },
            })
            .Replace("</head>", "</head><body><script data-tralbum='" + tralbum + "'></script></body>");
        const string grid = "<html><head><meta property=\"og:site_name\" content=\"Herbalistek\"></head><body><ol id=\"music-grid\">" +
                            "<li><a href=\"/album/52-blue\"><p class=\"title\">52 Blue</p></a></li>" +
                            "<li><a href=\"/album/lost-one\"><p class=\"title\">Lost One</p></a></li></ol></body></html>";
        static string Result(string name, string url, string sub) =>
            $"<li class=\"searchresult data-search\"><div class=\"result-info\"><div class=\"itemtype\">ALBUM</div>" +
            $"<div class=\"heading\"><a href=\"{url}?from=search\">\n {name}\n</a></div><div class=\"subhead\">\n {sub}\n</div>" +
            $"<div class=\"itemurl\"><a href=\"{url}?from=search\">{url}</a></div></div></li>";
        var searches = new List<string>();

        Assert.True(Bandcamp.ParseRelease(foundShell, "https://herbalist.bandcamp.com/album/52-blue").Placeholder);
        Assert.False(Bandcamp.ParseRelease(real, "https://observatory.bandcamp.com/album/52-blue").Placeholder);
        Assert.False(Bandcamp.ParseRelease(StandIn.Release("No List", "20 Jan 2026 00:00:00 GMT"), "https://x.bandcamp.com/album/no-list").Placeholder);

        using var app = new TestApp();
        app.Web.Extra = r =>
        {
            var (host, path) = (r.RequestUri!.Host, r.RequestUri.AbsolutePath);
            if (host == "herbalist.bandcamp.com")
                return path switch
                {
                    "/music" => StandIn.Text(grid), "/album/52-blue" => StandIn.Text(foundShell),
                    "/album/lost-one" => StandIn.Text(lostShell), _ => null,
                };
            if (host == "bandcamp.com" && path == "/search")
            {
                var q = Uri.UnescapeDataString(r.RequestUri.Query.Split("q=")[1].Split('&')[0]);
                lock (searches) searches.Add(q);
                return StandIn.Text("<ul>" + (q.Contains("52 Blue")
                    ? Result("52 Blue", "https://herbalist.bandcamp.com/album/52-blue", "by Herbalistek") +
                      Result("52 Blue", "https://observatory.bandcamp.com/album/52-blue", "by Herbalistek")
                    : "") + "</ul>");
            }
            if (host == "observatory.bandcamp.com" && path == "/album/52-blue") return StandIn.Text(real);
            return null;
        };
        await app.AddAsync(("bandcamp", "herbalist"));
        Assert.Equal(new[] { "Herbalistek 52 Blue", "Herbalistek Lost One" }, searches);           // once each
        var feed = app.Feed();
        var blue = feed.Single(p => p.Title == "52 Blue");
        var lost = feed.Single(p => p.Title == "Lost One");
        Assert.Equal("https://observatory.bandcamp.com/album/52-blue", blue.Url);
        Assert.Null(blue.Note);
        Assert.Equal("Observatory Records", blue.Label);
        Assert.Equal("https://herbalist.bandcamp.com/album/lost-one", lost.Url);
        Assert.Equal(Bandcamp.NotForSale, lost.Note);
        Assert.Equal(Bandcamp.NotForSale, app.Tracks().Single(t => t.PostTitle == "Lost One").Note);
    }

    /// <summary>Find missing links: the same guesses as the web version's, on the same kind of pages.</summary>
    [Fact]
    public async Task MissingPagesAreSuggested()
    {
        using var app = new TestApp();
        static string Result(string name, string url, string kind) =>
            $"<li class=\"searchresult data-search\"><div class=\"result-info\"><div class=\"itemtype\">{kind}</div>" +
            $"<div class=\"heading\"><a href=\"{url}?from=search\">\n {name}\n</a></div><div class=\"subhead\">\n \n</div>" +
            $"<div class=\"itemurl\"><a href=\"{url}?from=search\">{url}</a></div></div></li>";
        JsonObject User(string name, string permalink, int followers) => new()
            { ["username"] = name, ["permalink"] = permalink, ["permalink_url"] = $"https://soundcloud.com/{permalink}", ["followers_count"] = followers };
        var users = new Dictionary<string, JsonArray>
        {
            ["River"] = new(User("River", "river", 900000), User("River", "riverirvine", 800)),
            ["Solo Guy"] = new(User("Solo Guy", "sologuy-official", 10)),
            ["Nightmare"] = new(),
        };
        int searches = 0;
        app.Web.Extra = r =>
        {
            var (host, path, q) = (r.RequestUri!.Host, r.RequestUri.AbsolutePath, StandIn.QueryOf(r.RequestUri));
            if (host == "bandcamp.com" && path == "/search")
            {
                searches++;
                return StandIn.Text("<ul>" + (q["q"] == "Nightmare" && q["item_type"] == "b"
                    ? Result("Nightmare", "https://nightmare.bandcamp.com", "ARTIST") + Result("Nightmare Tunes", "https://nmtunes.bandcamp.com", "LABEL") : "") + "</ul>");
            }
            if (host == "api-v2.soundcloud.com" && path == "/search/users")
                return StandIn.Json(new JsonObject { ["collection"] = users.GetValueOrDefault(q["q"])?.DeepClone() ?? new JsonArray() });
            if (host.EndsWith(".bandcamp.com") && host != "nightferry.bandcamp.com") return StandIn.Text("", System.Net.HttpStatusCode.NotFound);
            return null;
        };
        var none = new Dictionary<string, List<string>>();
        Task<Suggestion> Ask(string name, string bc = "", string sc = "", string[]? skip = null, Dictionary<string, List<string>>? avoid = null) =>
            app.Engine.Suggester.SuggestAsync(name, new Dictionary<string, string> { ["bandcamp"] = bc, ["soundcloud"] = sc }, skip ?? Array.Empty<string>(), avoid ?? none);

        var got = await Ask("Nightmare");
        Assert.Equal("nightmare", got.Cells.GetValueOrDefault("bandcamp"));
        Assert.Equal("", got.Name);
        await Ask("Nightmare");
        Assert.Equal(1, searches);                                     // Bandcamp's answer is kept
        got = await Ask("Solo Guy");
        Assert.Equal("sologuy-official", got.Cells["soundcloud"]);
        Assert.Contains("no Bandcamp page found", got.Note);
        // two profiles share the name: the one whose address matches the Bandcamp page given
        Assert.Equal(new Dictionary<string, string> { ["soundcloud"] = "riverirvine" }, (await Ask("River", bc: "riverirvine")).Cells);
        Assert.Equal("river", (await Ask("River")).Cells["soundcloud"]);                       // nothing to go on: the bigger one
        // only a link: the name comes from the page; cells already filled are left alone
        got = await Ask("", bc: "nightferry");
        Assert.Equal("Night Ferry", got.Name);
        Assert.False(got.Cells.ContainsKey("bandcamp"));
        Assert.Empty((await Ask("Nightmare", bc: "x", sc: "y")).Cells);
        // cleared on purpose, or turned down before
        Assert.False((await Ask("River", skip: new[] { "soundcloud" })).Cells.ContainsKey("soundcloud"));
        Assert.Equal("riverirvine", (await Ask("River", avoid: new() { ["soundcloud"] = new() { "river" } })).Cells["soundcloud"]);
        Assert.Equal("nmtunes", (await Ask("Nightmare", avoid: new() { ["bandcamp"] = new() { "https://nightmare.bandcamp.com" } })).Cells["bandcamp"]);
        // the web version's measure of likeness
        Assert.Equal(0.5, Names.Ratio("abcd", "abef"), 3);
        foreach (var (x, y, want) in new[] { ("nightmareandoni", "nightmareoni", 0.888889), ("riverirvine", "river", 0.625),
                                            ("sologuyofficial", "sologuy", 0.636364), ("abcabcab", "bcabacba", 0.75), ("anechoicchamber", "anecho", 0.571429) })
            Assert.Equal(want, Names.Ratio(x, y), 5);            // as Python's difflib gives them
        Assert.Equal(0.9, Names.NameSim("somah", "somah uk"), 3);
    }
}
