using System.Net;
using System.Text.Json.Nodes;
using ArtistFeed.Core;
using Xunit;

namespace ArtistFeed.Tests;

/// <summary>Link Finder, checked against the same stand-in pages and the same expectations as the web version's tests.</summary>
public sealed partial class LinkFinderTests
{
    /// <summary>A test app whose web answers are the web version's recorded stand-in pages.</summary>
    sealed class Lookups : IDisposable
    {
        public readonly TestApp App = new();
        public readonly WebReplay Replay = new();
        public readonly Queue<int> CollectionFail = new();
        public LinkFinder F => App.Engine.LinkFinder;

        public Lookups()
        {
            App.Web.Extra = req =>
            {
                if (req.RequestUri!.AbsolutePath == "/api/fancollection/1/collection_items" && CollectionFail.Count > 0)
                    return new HttpResponseMessage((HttpStatusCode)CollectionFail.Dequeue()) { Content = new StringContent("") };
                return Replay.Answer(req);
            };
            App.Engine.Db.KvSet("lookup_fallback", "bandcamp");
            F.DeepAuto = false;
            F.DeepExtraSeconds = 0;
        }

        /// <summary>Bandcamp searches asked for, like the web test's "searched" list.</summary>
        public int Searched
        {
            get { lock (Replay.Asked) return Replay.Asked.Count(k => k.StartsWith("GET https://bandcamp.com/search?")); }
        }

        public int Requests => App.Web.Requests;

        public async Task<LookupJob> RunAsync(string text, bool fresh = false)
        {
            var job = F.Start(Tracklist.PickTracks(Tracklist.ReadTable(text)), fresh);
            for (int i = 0; i < 1500 && !job.Finished; i++) await Task.Delay(20);
            Assert.True(job.Finished, "lookup never finished");
            return job;
        }

        public void Dispose() => App.Dispose();
    }

    static Dictionary<int, JsonObject> ByN(LookupJob job) => job.Rows.ToDictionary(r => (int)r.N("n")!.Value, r => r);

    [Fact]
    public void Reading_tracklists()
    {
        var rows = new[]
        {
            new[] { "Track #", "Artist", "Title", "Crate", "Category / Series", "Title", "Track", "Time" },
            new[] { "1", "Idpa & Saša Delimar", "State I", "245", "Fresh Produce", "Flummoxed", "1", "0:00" },
            new[] { "2", "Masot", "02", "245", "Fresh Produce", "Flummoxed", "2", "5:08" },
            new[] { "", "", "", "", "", "", "", "" },
        };
        var want = new[] { new WantedTrack(1, "Idpa & Saša Delimar", "State I"), new WantedTrack(2, "Masot", "02") };
        string AsText(string sep) => string.Join("\r\n", rows.Select(r => string.Join(sep, r)));
        Assert.Equal(want, Tracklist.PickTracks(Tracklist.ReadTable(AsText("\t"))));
        Assert.Equal(want, Tracklist.PickTracks(Tracklist.ReadTable("﻿" + AsText(","))));
        Assert.Equal(new[] { new WantedTrack(1, "Masot", "02"), new WantedTrack(2, "Idpa", "State I") },
                     Tracklist.PickTracks(Tracklist.ReadTable("1\tMasot\t02\n2\tIdpa\tState I")));
        Assert.Equal(new WantedTrack(1, "Masot", "02"), Tracklist.PickTracks(Tracklist.ReadTable("1. Masot – 02\nIdpa - State I"))[0]);
        foreach (var bad in new[] { "", "Artist\tAlbum\nMasot\tFlora" })
            Assert.Throws<TracklistException>(() => Tracklist.PickTracks(Tracklist.ReadTable(bad)));
        Assert.Equal("especies endemicas", Matcher.Norm("Especies Endémicas (Original Mix)"));
    }

    [Fact]
    public async Task Tracklist_lookup()
    {
        using var t = new Lookups();
        await t.App.AddAsync(("bandcamp", "nightferry"), ("soundcloud", "nightferry"));     // as in the web test: row 1 is already in the feed
        var listing = string.Join("\n", "#\tArtist\tTitle\tCrate\tTitle", "1\tNight Ferry\tintro\t245\tMix A",
            "2\tMasot\tEspecies Endémicas\t245\tMix A", "3\tIdpa & Saša Delimar\tState I\t245\tMix A",
            "4\tNobody Known\tZzz\t245\tMix A", "5\tGhost Person\tQqq Rrr\t245\tMix A",
            "6\tOther Guy\tEspecies Endémicas\t245\tMix A", "7\tNight Ferry\tUnreleased Thing\t245\tMix A");
        var job = await t.RunAsync(listing);
        Assert.Equal(7, job.Total);
        Assert.Equal(7, job.Done);
        Assert.Equal("", job.Warning);
        var r = ByN(job);
        Assert.Equal(("bandcamp", "https://nightferry.bandcamp.com/album/first", "2025", "First"), (r[1].T("status"), r[1].T("url"), r[1].T("year"), r[1].T("release")));
        Assert.DoesNotContain(t.Replay.Asked, k => k.StartsWith("GET https://bandcamp.com/search?") && k.Contains("intro", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(("bandcamp", "https://masot.bandcamp.com/album/flora", "2023", "Flora"), (r[2].T("status"), r[2].T("url"), r[2].T("year"), r[2].T("release")));
        Assert.Equal(("bandcamp", "https://somelabel.bandcamp.com/track/state-i", "2021"), (r[3].T("status"), r[3].T("url"), r[3].T("year")));
        Assert.Equal(("soundcloud", "https://soundcloud.com/nobodyknown", ""), (r[4].T("status"), r[4].T("url"), r[4].T("year")));
        Assert.Equal("soundcloud_search", r[5].T("status"));
        Assert.EndsWith("people?q=Ghost%20Person", r[5].T("url"));
        Assert.Equal("soundcloud_search", r[6].T("status"));
        Assert.Equal(("bandcamp_artist", "https://nightferry.bandcamp.com"), (r[7].T("status"), r[7].T("url")));
        Assert.All(job.Rows, row => Assert.NotEmpty(row["steps"]!.AsArray()));

        var lines = t.F.ToCsv(job).TrimStart('﻿').Split("\r\n");
        Assert.Equal("#,artist,title,year,link,needs review,artist (your spelling),title (your spelling),link note,site,release,result,found as,note,steps", lines[0]);
        Assert.StartsWith("2,Masot,Especies Endémicas,2023,https://masot.bandcamp.com/album/flora,,,,,Bandcamp,Flora,Bandcamp release,Masot — Especies Endémicas,Bandcamp search,", lines[2]);

        var count = t.Searched;
        var again = await t.RunAsync(listing);                            // remembered: no new searches
        Assert.Equal(count, t.Searched);
        Assert.Equal(job.Rows.Select(x => x.T("url")), again.Rows.Select(x => x.T("url")));
        await t.RunAsync(listing, fresh: true);
        Assert.True(t.Searched > count);
        var missing = string.Join("\n", t.Replay.Missing.Where(k => !k.Contains("nightferry")).Distinct());
        Assert.True(missing.Length == 0, missing);
    }

    [Fact]
    public async Task Lookup_uses_purchases_and_artist_pages()
    {
        using var t = new Lookups();
        async Task<JsonObject> ReadPurchases()
        {
            Assert.Equal("divided_eye", t.F.SetCollectionFan("https://bandcamp.com/divided_eye"));
            await t.F.SyncCollectionAsync();
            return t.F.CollectionStatus();
        }

        t.CollectionFail.Enqueue(500);                                    // an outage part-way: what was read is kept
        var status = await ReadPurchases();
        Assert.Equal((2L, false), ((long)status.N("items")!, status.B("complete")));
        Assert.Contains("2 of 4 purchases are saved", status.T("error"));
        t.CollectionFail.Enqueue(429);                                    // told to slow down: waits and retries
        t.CollectionFail.Enqueue(429);
        status = await ReadPurchases();
        Assert.Equal((4L, 4L, "", true), ((long)status.N("items")!, (long)status.N("tracks")!, status.T("error"), status.B("complete")));
        Assert.Empty(t.CollectionFail);

        Assert.Equal((4L, 0L), ((long)status.N("releases")!, (long)status.N("releases_read")!));
        await t.F.DeepReadAsync();
        status = t.F.CollectionStatus();
        Assert.Equal((4L, 4L), ((long)status.N("releases")!, (long)status.N("releases_read")!));
        var (made, askedBefore) = (t.Requests, t.Searched);
        var owned = await t.RunAsync("Artist\tTitle\nBlocklab\tGargantua\ncontactghost\tSpelling Mitsake", fresh: true);
        Assert.Equal(new[] { ("bandcamp", "In your purchases", "2025"), ("bandcamp", "In your purchases", "2026") },
                     owned.Rows.Select(x => (x.T("status"), x.T("note"), x.T("year"))));
        Assert.Equal((made, askedBefore), (t.Requests, t.Searched));      // nothing fetched, nothing searched
        await t.F.DeepReadAsync();                                        // nothing left to read
        Assert.Equal(made, t.Requests);

        var rows = new[]
        {
            ("Masot", "02"), ("Masot", "Especies Endémicas"), ("Blocklab", "Gargantua"), ("contactghost", "Spelling Mitsake"),
            ("Siadic", "Air"), ("Somah", "Gnosis"), ("Pugilist", "Asteroids"), ("Anecho", "UFO"),
            ("Allen Mock", "Dragon Language"), ("YDX", "What A World"), ("Aaaa & Bbbb Cccc", "Deep Thing"), ("Kercha", "Trigger Activation"),
        };
        var job = await t.RunAsync("Artist\tTitle\n" + string.Join("\n", rows.Select(x => $"{x.Item1}\t{x.Item2}")), fresh: true);
        Assert.Equal("", job.Warning);
        var r = job.Rows.ToDictionary(x => (x.T("artist"), x.T("title")));
        var want = new Dictionary<(string, string), (string, string, string)>
        {
            [("Masot", "02")] = ("bandcamp", "https://masot.bandcamp.com/album/archivos-pt-1", "2026"),
            [("Masot", "Especies Endémicas")] = ("bandcamp", "https://masot.bandcamp.com/album/especies-endemicas", "2026"),
            [("Blocklab", "Gargantua")] = ("bandcamp", "https://whitepeach.bandcamp.com/album/wpr091-phoenix", "2025"),
            [("contactghost", "Spelling Mitsake")] = ("bandcamp", "https://artikalmusic.bandcamp.com/album/half-empty", "2026"),
            [("Siadic", "Air")] = ("bandcamp", "https://siadic.bandcamp.com/album/traction-ep", "2024"),
            [("Somah", "Gnosis")] = ("bandcamp", "https://somahuk.bandcamp.com/album/gnosis-railroads", "2026"),
            [("Pugilist", "Asteroids")] = ("bandcamp_artist", "https://pugilist.bandcamp.com", ""),
            [("Anecho", "UFO")] = ("soundcloud", "https://soundcloud.com/anechoicchamber", ""),
            [("Allen Mock", "Dragon Language")] = ("bandcamp", "https://observatory-records.bandcamp.com/album/dragon-language", "2026"),
            [("YDX", "What A World")] = ("bandcamp", "https://dubdenrecords.bandcamp.com/album/dubs-from-the-den-vol-7", "2026"),
            [("Kercha", "Trigger Activation")] = ("bandcamp", "https://dnorecords.bandcamp.com/album/dno021-kercha-open-the-door-lp", "2026"),
            [("Aaaa & Bbbb Cccc", "Deep Thing")] = ("bandcamp", "https://ccc.bandcamp.com/album/conduit", "2026"),
        };
        var wrong = want.Where(w => (r[w.Key].T("status"), r[w.Key].T("url"), r[w.Key].T("year")) != w.Value)
            .Select(w => $"{w.Key}: got {r[w.Key].T("status")} {r[w.Key].T("url")} {r[w.Key].T("year")}; steps: {string.Join(" / ", r[w.Key]["steps"]!.AsArray())}").ToList();
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.Equal("In your purchases", r[("Masot", "02")].T("note"));
        Assert.Equal("Found on the artist's page", r[("Somah", "Gnosis")].T("note"));
        var somahSteps = string.Join(" ", r[("Somah", "Gnosis")]["steps"]!.AsArray());
        Assert.DoesNotContain("Other", somahSteps);
        Assert.Contains("somahuk", somahSteps);

        // Correcting a result by hand: saved, shown in lookups on screen, and kept even when searching again.
        var fix = await t.F.SetLinkAsync("Pugilist", "Asteroids", "pugilist.bandcamp.com/album/blue");
        Assert.Equal(("manual", "https://pugilist.bandcamp.com/album/blue", "2026", "Blue EP"), (fix.T("status"), fix.T("url"), fix.T("year"), fix.T("release")));
        Assert.Equal("manual", job.Rows.First(x => x.T("artist") == "Pugilist").T("status"));
        Assert.Contains("Link you set", t.F.ToCsv(job));
        async Task<JsonObject> One(bool fresh) => (await t.RunAsync("Artist\tTitle\npugilist\tASTEROIDS", fresh)).Rows[0];
        Assert.Equal("https://pugilist.bandcamp.com/album/blue", (await One(false)).T("url"));
        Assert.Equal("https://pugilist.bandcamp.com/album/blue", (await One(true)).T("url"));
        var other = await t.F.SetLinkAsync("Pugilist", "Asteroids", "https://example.org/x", "1999");
        Assert.Equal(("example.org", "1999"), (other.T("site"), other.T("year")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => t.F.SetLinkAsync("Pugilist", "Asteroids", "not a link"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => t.F.SetLinkAsync("Pugilist", "Asteroids", "x.com/a", "soon"));
        Assert.Equal("none", (await t.F.SetLinkAsync("Pugilist", "Asteroids", "")).T("status"));
        Assert.Equal("bandcamp_artist", (await One(false)).T("status"));   // with your link removed, the searched answer is back

        // Every track of a release that was opened is saved.
        var asked = t.Searched;
        var hit = (await t.RunAsync("Artist\tTitle\nAaaa\tDissolution")).Rows[0];
        Assert.Equal(("bandcamp", "https://ccc.bandcamp.com/album/conduit", "On a page read earlier"), (hit.T("status"), hit.T("url"), hit.T("note")));
        Assert.Equal(asked, t.Searched);

        // Answerable from purchases or pages already read: no requests at all, even when searching again.
        made = t.Requests;
        var quick = await t.RunAsync("Artist\tTitle\nMasot\t02\nBlocklab\tGargantua\nMasot\tEspecies Endémicas", fresh: true);
        Assert.Equal(new[] { "bandcamp", "bandcamp", "bandcamp" }, quick.Rows.Select(x => x.T("status")));
        Assert.Equal(new[] { "2026", "2025", "2026" }, quick.Rows.Select(x => x.T("year")));
        Assert.Equal(made, t.Requests);

        // Which artist link to give when the track isn't found is a setting; switching it re-points answers.
        t.F.CachePut("artistpage", Matcher.Norm("Anecho"), JsonNode.Parse("""[["https://anecho.bandcamp.com", "Anecho"]]"""));
        t.F.CachePut("grid", "https://anecho.bandcamp.com", new JsonObject { ["name"] = "Anecho", ["items"] = new JsonArray() });
        async Task<(LookupJob Job, List<(string, string)> Rows)> Both(bool fresh = false)
        {
            var j = await t.RunAsync("Artist\tTitle\nAnecho\tUFO\nPugilist\tAsteroids", fresh);
            return (j, j.Rows.Select(x => (x.T("status"), x.T("url"))).ToList());
        }
        Assert.Equal("bandcamp", t.F.FallbackPref());
        var (bothJob, bothRows) = await Both(fresh: true);
        Assert.Equal(new[] { ("bandcamp_artist", "https://anecho.bandcamp.com"), ("bandcamp_artist", "https://pugilist.bandcamp.com") }, bothRows);
        var before = t.Requests;
        t.F.SetFallback("soundcloud");
        var onScreen = bothJob.Rows.Select(x => (x.T("status"), x.T("url"))).ToList();
        Assert.Equal(new[] { ("soundcloud", "https://soundcloud.com/anechoicchamber"), ("bandcamp_artist", "https://pugilist.bandcamp.com") }, onScreen);
        Assert.Equal(onScreen, (await Both()).Rows);
        Assert.Equal(before, t.Requests);
        Assert.ThrowsAny<ArgumentException>(() => t.F.SetFallback("spotify"));
        t.F.SetFallback("bandcamp");
        Assert.Equal(bothRows, (await Both()).Rows);
    }
}
