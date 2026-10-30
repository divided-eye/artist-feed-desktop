using System.Text;
using System.Text.Json.Nodes;
using ArtistFeed.Core;
using Xunit;

namespace ArtistFeed.Tests;

/// <summary>Remixes, saved answers, your own list, YouTube, Beatport and Discogs, and downloads: the web version's tests.</summary>
public sealed partial class LinkFinderTests
{
    static Dictionary<(string, string), JsonObject> ByTrack(LookupJob job) => job.Rows.ToDictionary(x => (x.T("artist"), x.T("title")));

    static string Rows(params (string Artist, string Title)[] rows) => "Artist\tTitle\n" + string.Join("\n", rows.Select(r => $"{r.Artist}\t{r.Title}"));

    static async Task ReadPurchasesAsync(Lookups t)
    {
        t.F.SetCollectionFan("divided_eye");
        await t.F.SyncCollectionAsync();
        await t.F.DeepReadAsync();
    }

    static JsonObject ImportList(Lookups t, string sheet, Encoding? encoding = null) =>
        t.F.ImportKnown(Tracklist.ReadKnown(Tracklist.ReadTable((encoding ?? new UTF8Encoding(false)).GetBytes(sheet))));

    static void Put(Lookups t, string key, string at, JsonObject result) =>
        t.App.Engine.Db.Execute("INSERT OR REPLACE INTO lookup_cache(key, result, at) VALUES(?,?,?)", key, result.ToJsonString(), at);

    [Fact]
    public async Task Edits_remixes_and_bootlegs_belong_to_whoever_made_them()
    {
        using var t = new Lookups();
        var m = t.F.M;
        Assert.Equal(new[] { "River", "River Dub" }, m.RemixCredits("Temperature (River Dub Edit)"));
        Assert.Equal(new[] { "Jah Beer", "Jah Beer Dub" }, m.RemixCredits("Exotermic Reaction (Jah Beer Dub Refix)"));
        Assert.Equal(new[] { "Someone", "Someone UKG Halftime" }, m.RemixCredits("Tune (Someone UKG Halftime Flip)"));
        Assert.Equal(new[] { "Nightmare & Oni" }, m.RemixCredits("You Owe Me (Nightmare & Oni 140 Bootleg)"));
        Assert.Equal(new[] { "IOWLA" }, m.RemixCredits("Outside The Box [IOWLA flip]"));
        foreach (var plain in new[] { "State I (Original Mix)", "Blur (VIP)", "Endgame (Blame)", "X (Extended Dub Mix)", "Y (Instrumental)", "Z" })
            Assert.Empty(m.RemixCredits(plain));
        Assert.Equal(new[] { "nightmare oni", "nightmare", "oni" }, m.RemixerNames("You Owe Me (Nightmare & Oni 140 Bootleg)"));
        string Verdict(string artist, string title, string name, string by)
        {
            var (s, a) = m.Score(artist, title, new Cand { Name = name, Artists = new() { by } });
            return Matcher.Judge(s, a);
        }
        var edit = "Temperature (River Dub Edit)";
        Assert.Equal("", Verdict("Sean Paul", edit, "Temperature", "Sean Paul"));
        Assert.Equal("", Verdict("Sean Paul", edit, "Temperature (Someone Else Remix)", "Sean Paul"));
        Assert.Equal("bandcamp", Verdict("Sean Paul", edit, "Temperature (River Dub Edit)", "River Dub"));
        Assert.Equal("bandcamp", Verdict("Sean Paul", edit, "Sean Paul - Temperature [River Dub edit]", "Some Label"));
        Assert.Equal("bandcamp_check", Verdict("Dayzero", "Blur", "Blur (Someone Remix)", "Dayzero"));
        Assert.Equal("bandcamp", Verdict("Dayzero", "Blur", "Blur (Original Mix)", "Dayzero"));
        // The page words it differently: "bootleg" outside the brackets, and the original artist restated in them
        var oblivion = "Oblivion bootleg (by Astor Piazzolla as performed by Miguel Fajardo)";
        string OnPage(string title)
        {
            var (s, a) = m.Score("Astor Piazzolla", title, new Cand { Name = oblivion, Artists = new() { "Sentient" }, Context = new() { oblivion } });
            return Matcher.Judge(s, a);
        }
        Assert.Equal("bandcamp", OnPage("Oblivion (as performed by Miguel Fajardo) (sentient bootleg)"));
        Assert.Equal("bandcamp_check", OnPage("Oblivion (as performed by Miguel Fajardo)"));              // someone's version
        Assert.Equal("", OnPage("Oblivion (as performed by Miguel Fajardo) (Other Guy bootleg)"));

        await ReadPurchasesAsync(t);                                           // as in the web test: you own the original "02"
        var rows = new[] { ("Sean Paul", "Temperature (River Dub Edit)"), ("Nas", "You Owe Me (Nightmare & Oni 140 Bootleg)"),
                           ("Omega Nebula", "Outside The Box (IOWLA Flip)"), ("Masot", "02 (Somebody Edit)") };
        t.F.SetFallback("soundcloud");
        var r = ByTrack(await t.RunAsync(Rows(rows), fresh: true));
        var sean = r[rows[0]];
        Assert.Equal(("soundcloud_track", "https://soundcloud.com/riverirvine/temperature-river-dub-edit", "2026"), (sean.T("status"), sean.T("url"), sean.T("year")));
        Assert.Equal("Free download on the remixer's SoundCloud", sean.T("note"));
        Assert.StartsWith("River — Sean Paul - Temperature", sean.T("found"));
        Assert.Equal(("bandcamp", "https://nightmare.bandcamp.com/album/bootlegs"), (r[rows[1]].T("status"), r[rows[1]].T("url")));
        var omega = r[rows[2]];
        Assert.Equal(("soundcloud", "https://soundcloud.com/omeganebula"), (omega.T("status"), omega.T("url")));
        Assert.Contains(omega["steps"]!.AsArray(), s => s!.GetValue<string>().Contains("no page found for \"IOWLA\""));
        Assert.DoesNotContain(r[rows[3]].T("status"), new[] { "bandcamp", "bandcamp_check" });

        t.F.SetFallback("bandcamp");                                           // the remixer still comes before the artist
        var again = ByTrack(await t.RunAsync(Rows(rows[0]), fresh: true))[rows[0]];
        Assert.Equal(("soundcloud_track", "https://soundcloud.com/riverirvine/temperature-river-dub-edit"), (again.T("status"), again.T("url")));

        t.F.SetFallback("soundcloud");
        var more = ByTrack(await t.RunAsync(Rows(("Nas", "Unfindable (Nightmare & Oni 140 Bootleg)"), ("Common Name", "Some Tune"), ("Solo Guy", "Whatever")), fresh: true));
        var duo = more[("Nas", "Unfindable (Nightmare & Oni 140 Bootleg)")];
        Assert.Equal(("soundcloud", "https://soundcloud.com/nightmareandoni"), (duo.T("status"), duo.T("url")));
        Assert.Contains("matched by name only", duo.T("note"));
        var tune = more[("Common Name", "Some Tune")];
        Assert.Equal(("soundcloud_track", "https://soundcloud.com/the-real-common-name/some-tune", "2024", "On the artist's SoundCloud"),
                     (tune.T("status"), tune.T("url"), tune.T("year"), tune.T("note")));
        Assert.Equal("Matched by name only", more[("Solo Guy", "Whatever")].T("note"));
    }

    [Fact]
    public async Task Label_releases_and_unlisted_tracks()
    {
        using var t = new Lookups();
        Assert.Equal("hannibal-rex-manyothers", LinkFinder.Slugify("Hannibal Rex Manyothers"));
        Assert.Equal("especies-endemicas-live", LinkFinder.Slugify("Especies Endémicas (Live!)"));
        t.F.CachePut("grid", "https://dungeonbeats.bandcamp.com", JsonNode.Parse("""
            {"name": "Dungeon Beats", "items": [
              {"title": "Hannibal Rex - An Older One", "url": "https://dungeonbeats.bandcamp.com/track/hannibal-rex-an-older-one", "artist": ""},
              {"title": "Someone Else - Thing", "url": "https://dungeonbeats.bandcamp.com/track/someone-else-thing", "artist": ""}]}
            """));
        t.F.CachePut("grid", "https://labelx.bandcamp.com", JsonNode.Parse("""
            {"name": "Label X", "items": [{"title": "LX001", "url": "https://labelx.bandcamp.com/album/lx001", "artist": "Somebody"}]}
            """));
        var rows = new[] { ("Howman", "Strip Search"), ("Hannibal Rex", "Manyothers"), ("Somebody", "Catalogue Tune"), ("Hannibal Rex", "Never Existed") };
        var r = ByTrack(await t.RunAsync(Rows(rows), fresh: true));
        var howman = r[rows[0]];
        Assert.Equal(("bandcamp", "https://dnorecords.bandcamp.com/album/dno022-howman-trap-eastwood-ep", "2026"), (howman.T("status"), howman.T("url"), howman.T("year")));
        var hannibal = r[rows[1]];
        Assert.Equal(("bandcamp", "https://dungeonbeats.bandcamp.com/track/hannibal-rex-manyothers", "2026"), (hannibal.T("status"), hannibal.T("url"), hannibal.T("year")));
        Assert.Contains("not publicly listed", hannibal.T("note"));
        var known = r[rows[2]];
        Assert.Equal(("bandcamp", "https://labelx.bandcamp.com/album/lx001", "On a label page you know"), (known.T("status"), known.T("url"), known.T("note")));
        Assert.DoesNotContain(r[rows[3]].T("status"), new[] { "bandcamp", "bandcamp_check" });
        var status = t.F.CollectionStatus();
        Assert.True(status.N("labels_read") >= 0 && status.N("labels_read") <= status.N("labels"));    // (the web test also has pages left by earlier tests)
    }

    [Fact]
    public async Task Vip_is_not_the_original()
    {
        using var t = new Lookups();
        var db = t.App.Engine.Db;
        var url = "https://toomuchcollective.bandcamp.com/album/re-use-reduce-ep";
        db.Execute("INSERT INTO collection_items(key, fan, title, band, url, kind) VALUES('a997','x','Re-use, Reduce EP','Anecho',?,'a')", url);
        db.Execute("INSERT INTO collection_tracks(item_key, position, title, artist) VALUES('a997',1,'Re-use, Reduce','Anecho')");
        t.F.CachePut("release", url, new JsonObject
        {
            ["url"] = url, ["asked"] = url, ["title"] = "Re-use, Reduce EP", ["artist"] = "Anecho", ["year"] = "2025", ["release"] = "",
            ["release_url"] = "", ["label"] = "", ["v"] = 3,
            ["tracks"] = JsonNode.Parse("""[{"title": "Re-use, Reduce", "artist": ""}, {"title": "Other One", "artist": ""}]"""),
        });
        var job = await t.RunAsync("Artist\tTitle\nAnecho\tRe-use, Reduce (Continental VIP)\nAnecho\tRe-use, Reduce", fresh: true);
        var (vip, original) = (job.Rows[0], job.Rows[1]);
        Assert.Equal(("bandcamp", url), (original.T("status"), original.T("url")));
        Assert.Contains(vip.T("status"), LinkFinder.FallbackStatuses);
        Assert.NotEqual(url, vip.T("url"));
        Assert.Equal("https://soundcloud.com/anechoicchamber", vip["options"]?["soundcloud"]?["url"]?.GetValue<string>());
    }

    [Fact]
    public async Task Saved_answers_review_flags_and_searching_one_row_again()
    {
        using var t = new Lookups();
        t.App.Engine.Db.KvSet("soundcloud_profiles", "{}");
        var job = await t.RunAsync(Rows(("River", "Unheard Of Tune")), fresh: true);
        var row = t.F.View(job)[0];
        var option = row["options"]?["soundcloud"] as JsonObject;
        Assert.True(row.T("review").Contains("name alone") || (option != null && LinkFinder.ReviewReason(row.With(option.Select(kv => (kv.Key, kv.Value)).ToArray())).Contains("name alone")));
        var asked = t.Searched;
        t.App.Engine.Db.Execute("DELETE FROM lookup_cache WHERE key=?", t.F.CacheKey("River", "Unheard Of Tune"));
        await t.RunAsync(Rows(("River", "Unheard Of Tune")));                 // from Bandcamp's saved search answers
        Assert.Equal(asked, t.Searched);
        var retry = t.F.Start(Tracklist.PickTracks(Tracklist.ReadTable(Rows(("River", "Unheard Of Tune")))), retryMissing: true);
        for (int i = 0; i < 1500 && !retry.Finished; i++) await Task.Delay(20);
        Assert.True(t.Searched > asked);
        asked = t.Searched;
        t.F.RetryRow(job, 1);
        Assert.False(job.Finished);
        Assert.Equal("pending", job.Rows[0].T("status"));
        Assert.Throws<InvalidOperationException>(() => t.F.RetryRow(job, 1));      // not while it is still running
        for (int i = 0; i < 1500 && !job.Finished; i++) await Task.Delay(20);
        Assert.Throws<InvalidOperationException>(() => t.F.RetryRow(job, 99));
        Assert.NotEqual("pending", job.Rows[0].T("status"));
        Assert.True(t.Searched > asked);
        Assert.Equal(job.Total, job.Done);
        Assert.NotEqual("", LinkFinder.ReviewReason(new JsonObject { ["status"] = "bandcamp_check" }));
        Assert.Equal("", LinkFinder.ReviewReason(new JsonObject { ["status"] = "manual" }));
        Assert.Contains("needs review", t.F.ToCsv(job).Split("\r\n")[0]);
    }

    [Fact]
    public void Each_version_of_a_title_has_its_own_saved_answer()
    {
        using var t = new Lookups();
        string K(string a, string b) => t.F.CacheKey(a, b);
        Assert.NotEqual(K("Sean Paul", "Temperature"), K("Sean Paul", "Temperature (River Dub Edit)"));
        Assert.NotEqual(K("River", "Boogaloo"), K("River", "Boogaloo VIP"));
        Assert.Equal(K("River", "Boogaloo VIP"), K("River", "Boogaloo (VIP)"));
        Assert.NotEqual(K("Anecho", "Re-use, Reduce (Continental VIP)"), K("Anecho", "Re-use, Reduce VIP"));
        Assert.Equal("v2|deapoh|voyage", K("Deapoh", "Voyage (Digital Download)"));
        Assert.Equal("v2|deapoh|voyage", K("deapoh", "VOYAGE"));
        Put(t, "manual|old one|tune", Db.Now(), new JsonObject { ["status"] = "manual", ["url"] = "https://x.bandcamp.com/track/tune-remix" });
        Assert.EndsWith("tune-remix", t.F.ManualFor("Old One", "Tune (Someone Remix)").T("url"));
        Assert.Equal("nocase", Matcher.Norm("Nøcase"));
        Assert.Equal("kode 1", Matcher.Norm("KØDE-1"));
        Assert.Equal("lodz", Matcher.Norm("Łódź"));
        Put(t, "manual|n case|old tune", Db.Now(), new JsonObject { ["status"] = "manual", ["url"] = "https://x.bandcamp.com/track/old-tune" });
        Assert.Equal("https://x.bandcamp.com/track/old-tune", t.F.ManualFor("Nøcase", "Old Tune").T("url"));
        Assert.Null(t.F.ManualFor("Nocase", "Old Tune"));
    }

    [Fact]
    public async Task Your_list_spellings_links_and_notes()
    {
        using var t = new Lookups();
        Assert.Equal(("https://x.bandcamp.com/track/y", "limited time only"), Tuple(Tracklist.SplitLinkCell("https://x.bandcamp.com/track/y (limited time only)")));
        Assert.Equal(("", "Patreon"), Tuple(Tracklist.SplitLinkCell("(Patreon)")));
        Assert.Equal(("https://soundcloud.com/someone/tune", "free DL"), Tuple(Tracklist.SplitLinkCell("soundcloud.com/someone/tune - free DL")));
        var sheet = string.Join("\n", "Artist\tTitle\tLink\tNotes",
            "MASOT\tEspecies Endémicas\thttps://old.example.com/dead-link (outdated)\t",
            "Nobody Known\tListed Tune\thttps://nobodyknown.bandcamp.com/track/listed-tune (limited time only)\tvinyl only",
            "Nobody Known\tSC Only Tune\tsoundcloud.com/nobodyknown/sc-only-tune (Patreon)\t",
            "NOBODY KNOWN\tListed Tune\t\t",
            "aNeCHo\t\t\t");
        await ReadPurchasesAsync(t);                                           // as the web test has by then
        var got = ImportList(t, sheet);
        Assert.Equal((3L, 3L, 3L, 2L), ((long)got.N("tracks")!, (long)got.N("artists")!, (long)got.N("with_link")!, (long)got.N("clashes")!));
        var asked = t.Searched;
        var job = await t.RunAsync("Artist\tTitle\nmasot\tespecies endemicas\nnobody known\tlisted tune\nNobody Known\tSC Only Tune\nAnecho\tUFO", fresh: true);
        var view = t.F.View(job);
        var (found, listed, scOnly, other) = (view[0], view[1], view[2], view[3]);
        Assert.True(found.T("status") == "bandcamp", found.ToJsonString());
        Assert.Contains("masot.bandcamp.com", found.T("url"));
        Assert.Equal(("MASOT", "Especies Endémicas", "outdated"), (found.T("list_artist"), found.T("list_title"), found.T("list_note")));
        Assert.Equal(("imported", "https://nobodyknown.bandcamp.com/track/listed-tune"), (listed.T("status"), listed.T("url")));
        Assert.Contains("limited time only", listed.T("note"));
        Assert.Contains("vinyl only", listed.T("list_note"));
        Assert.Equal(("imported", "https://soundcloud.com/nobodyknown/sc-only-tune"), (scOnly.T("status"), scOnly.T("url")));
        Assert.Equal("Patreon", scOnly.T("list_note"));
        Assert.True(t.Searched > asked);
        Assert.Equal(("aNeCHo", ""), (other.T("list_artist"), other.T("list_title")));
        Assert.Contains("MASOT,Especies Endémicas,outdated", t.F.ToCsv(job).TrimStart('﻿').Split("\r\n")[1]);

        var all = t.F.SavedListing(limit: 1000);
        var byKey = all.Rows.ToDictionary(x => x.T("key"));
        Assert.Equal("Especies Endémicas", byKey["masot|especies endemicas"].T("list_title"));
        Assert.Equal("imported", byKey["nobody known|listed tune"].T("status"));
        Assert.Equal(3, all.Counts["list"]);
        var only = t.F.SavedListing("nobody listed");
        Assert.Equal(1, only.Total);
        Assert.Equal("limited time only; vinyl only", only.Rows[0].T("list_note"));
        Assert.Contains("artist (your spelling)", t.F.SavedCsv().Split("\r\n")[0]);
        t.F.ForgetSaved("masot|especies endemicas");                         // (answered from purchases here, so nothing was saved)
        Assert.Contains("masot|especies endemicas", t.F.SavedListing(which: "unsearched", limit: 1000).Rows.Select(x => x.T("key")));
        var cleared = t.F.ClearKnown();
        Assert.Equal((0L, 0L), ((long)cleared.N("known_tracks")!, (long)cleared.N("known_artists")!));
    }

    static (string, string) Tuple((List<string> Links, string Other) x) => (string.Join(" ", x.Links), x.Other);

    [Fact]
    public async Task Your_list_years_repeated_rows_versions_and_stray_columns()
    {
        using var t = new Lookups();
        var sheet = string.Join("\n",
            "Artist,Track,Year,Link,,,,",
            "ØZ,AK-47,2022,https://soundcloud.com/ozdub,,,,",
            "ØZ,AK-47,2022,https://ozdub.bandcamp.com/album/d-blk002,,,,",
            "Øz,AK-47,,,,,,",
            "baron,Eski,2021,https://baron.bandcamp.com/album/spear,,,,https://elsewhere.example.com/not-this-row",
            "baron,Eski [alternate version],2024,https://widdfam.bandcamp.com/album/cosmic (limited time),,,,",
            "Töki,Zimmy (forth. DoD),2026,forthcoming https://soundcloud.com/toki/zimmy,,,,",
            "Darkai,011 Dub,2024,Label Patreon exclusive (eventually available at https://www.patreon.com/label/shop),,,,",
            "Somebody,Gone Tune,2019,sold out USB001; no link yet,,,,",
            "Hippoflip,???,2020,https://soundcloud.com/hippoflip,,,,");
        var got = ImportList(t, sheet, Encoding.Latin1);                       // as Excel saves a plain CSV
        Assert.Equal((6L, 5L, 3L, 1L), ((long)got.N("tracks")!, (long)got.N("with_link")!, (long)got.N("release_links")!, (long)got.N("skipped")!));
        Assert.Equal((1L, 2L), ((long)got.N("link_clashes")!, (long)got.N("spelling_clashes")!));
        var oz = t.F.KnownFor("oz", "ak 47");
        Assert.Equal(("ØZ", "AK-47", "2022", "https://ozdub.bandcamp.com/album/d-blk002"), (oz.Artist, oz.Title, oz.Year, oz.Url));
        var (plainOne, other) = (t.F.KnownFor("Baron", "Eski"), t.F.KnownFor("Baron", "eski (alternate version)"));
        Assert.Equal(("https://baron.bandcamp.com/album/spear", "", "2021"), (plainOne.Url, plainOne.Others, plainOne.Year));
        Assert.Equal(("Eski [alternate version]", "https://widdfam.bandcamp.com/album/cosmic", "limited time", "2024"), (other.Title, other.Url, other.Note, other.Year));
        var zimmy = t.F.KnownFor("Toki", "Zimmy");
        Assert.Equal(("Zimmy (forth. DoD)", "https://soundcloud.com/toki/zimmy", "forthcoming"), (zimmy.Title, zimmy.Url, zimmy.Note));
        Assert.Equal("Label Patreon exclusive; eventually available at", t.F.KnownFor("darkai", "011 dub").Note);
        var gone = t.F.KnownFor("somebody", "gone tune");
        Assert.Equal(("", "sold out USB001; no link yet"), (gone.Url, gone.Note));
        var job = await t.RunAsync("Artist\tTitle\noz\tak-47\nbaron\teski [alternate version]");
        var view = t.F.View(job);
        Assert.Equal(("imported", "2022", "ØZ"), (view[0].T("status"), view[0].T("year"), view[0].T("list_artist")));
        Assert.Equal(("https://widdfam.bandcamp.com/album/cosmic", "2024"), (view[1].T("url"), view[1].T("year")));
        Assert.Contains(",2022,https://ozdub.bandcamp.com/album/d-blk002,", t.F.ToCsv(job));
        Assert.Equal(new[] { "Eski", "Eski [alternate version]" }, t.F.SavedListing("baron eski", limit: 50).Rows.Select(x => x.T("list_title")).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Answers_saved_under_old_keys_are_moved_and_can_be_edited()
    {
        using var t = new Lookups();
        var now = Db.Now();
        Put(t, "v2|river|oldaloo vip", now, new JsonObject { ["status"] = "soundcloud", ["url"] = "https://soundcloud.com/wrong-river", ["found"] = "wrong-river" });
        Put(t, "v2|emalkay|when i look at old", now, new JsonObject { ["status"] = "soundcloud_track", ["url"] = "https://soundcloud.com/kwizma/old",
            ["found"] = "Kwizma — Emalkay - When I Look At Old (Kwizma Bootleg)" });
        Put(t, "v2|plainman|plain old tune", now, new JsonObject { ["status"] = "bandcamp", ["url"] = "https://plainman.bandcamp.com/track/plain-old-tune", ["found"] = "Plainman — Plain Old Tune" });
        Assert.Equal(2, t.F.RekeyOldAnswers());
        Assert.Equal(0, t.F.RekeyOldAnswers());
        var keys = t.App.Engine.Db.Query("SELECT key FROM lookup_cache").Select(x => x.S("key")).ToHashSet();
        Assert.Subset(keys, new HashSet<string?> { "v2|river|oldaloo#vip", "v2|emalkay|when i look at old#kwizma", "v2|plainman|plain old tune" });
        Assert.Equal("https://soundcloud.com/wrong-river", t.F.SavedFor("River", "Oldaloo VIP").T("url"));
        var row = t.F.SavedListing("river oldaloo").Rows[0];
        Assert.Equal(("river|oldaloo#vip", "vip", ""), (row.T("key"), row.T("version"), row.T("title")));
        Assert.Equal("manual", (await t.F.SetLinkAsync("river", "oldaloo (VIP)", "https://soundcloud.com/riverirvine", key: row.T("key"))).T("status"));
        var rows = t.F.SavedListing("river oldaloo").Rows;
        Assert.Single(rows);
        Assert.Equal(("manual", "https://soundcloud.com/riverirvine"), (rows[0].T("status"), rows[0].T("url")));
        Assert.Equal("https://soundcloud.com/riverirvine", t.F.ManualFor("River", "Oldaloo VIP").T("url"));
        HashSet<string> Names(string which) => t.F.SavedListing(which: which, limit: 1000).Rows.Select(x => x.T("key")).ToHashSet();
        Assert.Contains("emalkay|when i look at old#kwizma", Names("found"));
        Assert.DoesNotContain("emalkay|when i look at old#kwizma", Names("missing"));
        Put(t, "v2|nobody|only a profile", now, new JsonObject { ["status"] = "soundcloud", ["url"] = "https://soundcloud.com/nobody", ["title"] = "Only A Profile", ["artist"] = "Nobody" });
        Assert.Contains("nobody|only a profile", Names("missing"));
        Assert.DoesNotContain("nobody|only a profile", Names("found"));
    }

    [Fact]
    public async Task Remix_typed_without_brackets_falls_back_to_the_remixers_soundcloud()
    {
        using var t = new Lookups();
        Assert.Equal(new[] { "Outside The Box (IOWLA Flip)" }, t.F.BareRemixReadings("Outside The Box IOWLA Flip"));
        Assert.Equal(new[] { "Tune (Kahn & Neek Remix)" }, t.F.BareRemixReadings("Tune Kahn & Neek Remix"));
        foreach (var plain in new[] { "Radio Edit", "Tune Radio Edit", "Coin Flip", "The Final Edit", "Thing (X Remix)", "Boogaloo VIP" })
            Assert.Empty(t.F.BareRemixReadings(plain));
        ImportList(t, "Artist,Track,Link\nNobody Known,,\nGhost Person,Unfindable Zzz (Nobody Known Flip),\n");
        Assert.Equal("Unfindable Zzz (Nobody Known Flip)", t.F.BareRemixReadings("Unfindable Zzz Nobody Known Flip")[0]);
        Assert.Equal("Unfindable Zzz (Nobody Known Flip)", t.F.KnownFor("ghost person", "unfindable zzz nobody known flip").Title);
        var job = await t.RunAsync(Rows(("Ghost Person", "Unfindable Zzz Nobody Known Flip")), fresh: true);
        var row = t.F.View(job)[0];
        Assert.Equal(("soundcloud", "https://soundcloud.com/nobodyknown"), (row.T("status"), row.T("url")));
        Assert.StartsWith("Remixer's profile", row.T("note"));
        Assert.Contains(row["steps"]!.AsArray(), s => s!.GetValue<string>().Contains("read as"));
        Assert.Equal("Unfindable Zzz (Nobody Known Flip)", row.T("list_title"));
    }

    [Fact]
    public void Saved_results_can_be_sorted()
    {
        using var t = new Lookups();
        Put(t, "v2|sortb|zebra", "2026-01-03T00:00:00+00:00", new JsonObject { ["status"] = "bandcamp", ["url"] = "https://b.bandcamp.com/track/zebra", ["year"] = "2021", ["artist"] = "SortB", ["title"] = "Zebra" });
        Put(t, "v2|sorta|mango", "2026-01-01T00:00:00+00:00", new JsonObject { ["status"] = "soundcloud", ["url"] = "https://soundcloud.com/a", ["year"] = "", ["artist"] = "SortA", ["title"] = "Mango" });
        Put(t, "v2|sortc|apple", "2026-01-02T00:00:00+00:00", new JsonObject { ["status"] = "bandcamp", ["url"] = "https://a.bandcamp.com/track/apple", ["year"] = "2024", ["artist"] = "SortC", ["title"] = "Apple" });
        string[] Order(string sort = "", bool desc = false) => t.F.SavedListing("sort", limit: 50, sort: sort, desc: desc).Rows.Select(x => x.T("tidy_title")).ToArray();
        Assert.Equal(new[] { "zebra", "apple", "mango" }, Order());
        Assert.Equal(new[] { "mango", "zebra", "apple" }, Order("artist"));
        Assert.Equal(new[] { "zebra", "mango", "apple" }, Order("title", true));
        Assert.Equal(new[] { "apple", "zebra", "mango" }, Order("year", true));
        Assert.Equal(new[] { "zebra", "apple", "mango" }, Order("year"));
        Assert.Equal(new[] { "apple", "zebra", "mango" }, Order("link"));
        Assert.Equal(new[] { "mango", "apple", "zebra" }, Order("saved"));
        var lines = t.F.SavedCsv("sort", sort: "artist").TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "sorta", "sortb", "sortc" }, lines.Skip(1).Select(l => l.Split(',')[0]));
    }

    [Fact]
    public void Symbols_written_into_a_name_are_kept()
    {
        using var t = new Lookups();
        Assert.Equal(new[] { "∑1", "myca$h", "mor£", "$uicideboy$" }, new[] { "∑1", "Myca$h", "Mor£ (Instrumental)", "$uicideboy$" }.Select(Matcher.Norm));
        Assert.Equal(new[] { "tune", "tune", "a b", "100 dub", "who s there", "1 tune" },
                     new[] { "★ Tune ★", "Tune™", "A + B", "100% Dub", "Who's There", "1 $ Tune" }.Select(Matcher.Norm));
        Assert.Equal("∑1|wake up lisa", t.F.TrackKey("∑1", "Wake Up Lisa"));
        Assert.NotEqual(t.F.TrackKey("1", "Wake Up Lisa"), t.F.TrackKey("∑1", "Wake Up Lisa"));
        var db = t.App.Engine.Db;
        Put(t, "manual|myca h|old tune", Db.Now(), new JsonObject { ["status"] = "manual", ["url"] = "https://x.bandcamp.com/track/m" });
        Assert.Equal("https://x.bandcamp.com/track/m", t.F.ManualFor("Myca$h", "Old Tune").T("url"));
        db.Execute("INSERT OR REPLACE INTO known_tracks(key, variant, artist, title, at) VALUES(?,?,?,?,?)", "1|wake up lisa", "wake up lisa", "∑1", "Wake Up Lisa", Db.Now());
        db.Execute("INSERT OR REPLACE INTO known_artists(key, name, at) VALUES(?,?,?)", "1", "∑1", Db.Now());
        Assert.Equal(2, t.F.RekeyKnown());
        Assert.Equal(0, t.F.RekeyKnown());
        Assert.Equal("Wake Up Lisa", t.F.KnownFor("∑1", "wake up lisa").Title);
        Assert.Equal("", t.F.KnownFor("1", "wake up lisa").Title);
    }

    [Fact]
    public void Saved_results_closest_list_entry_and_editing_a_spelling()
    {
        using var t = new Lookups();
        var db = t.App.Engine.Db;
        ImportList(t, "Artist,Track,Link\nNaz,You Owe Me (Nitemare & Onee 140 Bootleg),https://soundcloud.com/no\nShaun Pal,Warmth (Rivver Dub Edit),https://soundcloud.com/rivver\n");
        Put(t, "manual|naz|you owe me nitemare onee 140 bootleg", Db.Now(), new JsonObject { ["status"] = "manual", ["url"] = "https://soundcloud.com/no" });
        Put(t, "manual|shaun pal|warmth", Db.Now(), new JsonObject { ["status"] = "manual", ["url"] = "https://soundcloud.com/rivver" });
        Put(t, "manual|nobody|unlisted", Db.Now(), new JsonObject { ["status"] = "manual", ["url"] = "https://soundcloud.com/x" });
        JsonObject Row(string q, string key) => t.F.SavedListing(q, limit: 1000).Rows.First(x => x.T("key") == key);
        Assert.Equal("You Owe Me (Nitemare & Onee 140 Bootleg)", Row("naz", "naz|you owe me nitemare onee 140 bootleg").T("list_title"));
        var guess = Row("shaun", "shaun pal|warmth");
        Assert.Equal("Warmth (Rivver Dub Edit)", guess.T("list_title"));
        Assert.True(guess.B("list_guess"));
        Assert.Equal("", Row("nobody", "nobody|unlisted").T("list_title"));
        t.F.SetSpelling(guess.T("key"), "Shaun Pal", "Warmth (Rivver Dub Edit)", like: (guess.T("list_key"), guess.T("list_variant")));
        var mine = Row("shaun", "shaun pal|warmth");
        Assert.False(mine.B("list_guess"));
        Assert.Equal("https://soundcloud.com/rivver", mine.T("list_link"));
        t.F.SetSpelling("nobody|unlisted", "NoBody", "UnListed");
        var row = Row("nobody", "nobody|unlisted");
        Assert.Equal(("NoBody", "UnListed", false), (row.T("list_artist"), row.T("list_title"), row.B("list_guess")));
        t.F.SetSpelling("nobody|unlisted", "NoBody", "Unlisted!", row.T("list_variant"));
        Assert.Equal(new[] { "Unlisted!" }, db.Query("SELECT title FROM known_tracks WHERE key='nobody|unlisted'").Select(x => x.S("title")));
        t.F.SetSpelling("nobody|unlisted", "", "", "unlisted");
        Assert.Empty(db.Query("SELECT * FROM known_tracks WHERE key='nobody|unlisted'"));

        ImportList(t, "Artist,Track,Link\nListOnly,Never Searched,https://soundcloud.com/lo\nListOnly,Other Tune,\n");
        var listOnly = t.F.SavedListing("listonly", limit: 1000).Rows.First(x => x.T("list_title") == "Never Searched");
        Assert.False(listOnly.B("searched"));
        t.F.ForgetEntry(listOnly.T("list_key"), listOnly.T("list_variant"));
        Assert.Equal(new[] { "Other Tune" }, t.F.SavedListing("listonly", limit: 1000).Rows.Select(x => x.T("list_title")));
    }

    [Fact]
    public async Task Link_finder_youtube_is_off_until_ticked_and_only_for_ticked_artists()
    {
        using var t = new Lookups();
        var e = t.App.Engine;
        e.Table.Save(new List<TableRow> { new() { Name = "Tube Band", Cells = { ["youtube"] = "@TubeBand" } } });
        Assert.Equal(new[] { "bandcamp", "soundcloud" }, e.Sites.LookupSites());               // YouTube off by default
        bool AskedYoutube() { lock (t.Replay.Asked) return t.Replay.Asked.Any(k => k.Contains("youtube.com/results")); }
        Assert.NotEqual("youtube", (await t.RunAsync(Rows(("Tube Band", "Hidden Lake")), fresh: true)).Rows[0].T("status"));
        Assert.False(AskedYoutube());
        e.Sites.SetLookupSites(new[] { "bandcamp", "soundcloud", "youtube" });
        var table = e.Table.Rows();
        foreach (var row in table) row.YoutubeLookup = row.Name == "Tube Band";
        e.Table.Save(table);
        Assert.Equal(new HashSet<string> { "tube band" }, e.Sites.YoutubeNames());
        Assert.True(e.Table.Rows().First(x => x.Name == "Tube Band").YoutubeLookup);
        await e.Refresher.RefreshAsync();                       // its channel's videos in the feed
        lock (t.Replay.Asked) t.Replay.Asked.Clear();
        var onChannel = (await t.RunAsync(Rows(("Tube Band", "Glass River")), fresh: true)).Rows[0];
        Assert.Equal(("youtube", "https://www.youtube.com/watch?v=vid00000001"), (onChannel.T("status"), onChannel.T("url")));
        Assert.False(AskedYoutube());
        var searched = (await t.RunAsync(Rows(("Tube Band", "Hidden Lake")), fresh: true)).Rows[0];
        Assert.Equal("youtube", searched.T("status"));
        Assert.EndsWith("vid00000010", searched.T("url"));
        lock (t.Replay.Asked) t.Replay.Asked.Clear();
        Assert.NotEqual("youtube", (await t.RunAsync(Rows(("Unticked Artist", "Lost Song")), fresh: true)).Rows[0].T("status"));
        Assert.False(AskedYoutube());
        Assert.Equal("soundcloud_track", (await t.RunAsync(Rows(("Sean Paul", "Temperature (River Dub Edit)")), fresh: true)).Rows[0].T("status"));
        await t.RunAsync(Rows(("Tube Band", "Hidden Lake"), ("Tube Band", "Glass River")), fresh: true);
        Assert.Equal(new HashSet<string> { "tube band|hidden lake", "tube band|glass river" },
                     t.F.SavedListing(which: "lastrun", limit: 1000).Rows.Select(x => x.T("key")).ToHashSet());
        await t.RunAsync(Rows(("Tube Band", "Glass River")));
        Assert.Empty(t.F.SavedListing(which: "lastrun").Rows);
        e.Sites.SetLookupSites(new[] { "bandcamp", "soundcloud" });
        t.F.SitesChanged();
        Assert.NotEqual("youtube", (await t.RunAsync(Rows(("Tube Band", "Hidden Lake")))).Rows[0].T("status"));

        // a saved miss only gets YouTube added when YouTube is switched on
        var miss = new JsonObject
        {
            ["status"] = "none", ["note"] = "", ["steps"] = J.Arr(new[] { "searched Bandcamp" }), ["options"] = new JsonObject(),
            ["sites"] = J.Arr(new[] { "bandcamp", "soundcloud" }), ["engine"] = LinkFinder.EngineVersion,
        };
        t.F.Remember(t.F.CacheKey("Tube Band", "Hidden Lake"), miss, "Tube Band", "Hidden Lake");
        e.Sites.SetLookupSites(new[] { "bandcamp", "soundcloud", "youtube" });
        var before = t.Searched;
        var added = (await t.RunAsync(Rows(("Tube Band", "Hidden Lake")))).Rows[0];
        Assert.Equal("youtube", added.T("status"));
        Assert.Equal(before, t.Searched);
        Assert.Contains("youtube", J.Strs(t.F.SavedFor("Tube Band", "Hidden Lake"), "sites"));
    }

    [Fact]
    public async Task Beatport_and_discogs_in_link_finder()
    {
        using var t = new Lookups();
        var e = t.App.Engine;
        t.F.BeatportGap = t.F.DiscogsGap = 0;
        Assert.Equal(new[] { "bandcamp", "beatport", "soundcloud", "youtube", "discogs" }, Sites.LookupOrder);
        Assert.DoesNotContain("beatport", e.Sites.LookupSites());
        Assert.False(e.Sites.IsOn("beatport") || e.Sites.IsOn("discogs"));                   // off by default
        Assert.DoesNotContain("beatport", e.Sites.Enabled());                                 // no feed, no chip
        e.Sites.SetLookupSites(new[] { "bandcamp", "soundcloud", "beatport" });
        Assert.DoesNotContain("beatport", e.Sites.LookupSites());                             // ticked, but switched off
        e.Sites.SetOn("beatport", true);
        e.Sites.SetOn("discogs", true);
        Assert.Contains("beatport", e.Sites.LookupSites());
        e.Db.Execute("DELETE FROM kv WHERE key = 'extra_sites_on'");
        e.Sites.SetLookupSites(new[] { "bandcamp", "soundcloud", "discogs" });
        e.Sites.Upgrade();                                                                     // from v76: ticked means switched on
        Assert.True(e.Sites.IsOn("discogs"));
        Assert.False(e.Sites.IsOn("beatport"));
        e.Sites.SetOn("beatport", true);

        e.Sites.SetLookupSites(new[] { "bandcamp", "soundcloud", "beatport", "discogs" });
        var rows = (await t.RunAsync(Rows(("Beat Artist", "Deep Groove"), ("Wax Only", "Pressed Tune")), fresh: true)).Rows.ToDictionary(x => x.T("title"));
        var bp = rows["Deep Groove"];
        Assert.Equal(("beatport", "Beatport", "https://www.beatport.com/track/deep-groove/501", "Deep Groove EP", "2024"),
                     (bp.T("status"), bp.T("site"), bp.T("url"), bp.T("release"), bp.T("year")));
        Assert.Equal("soundcloud_search", rows["Pressed Tune"].T("status"));
        Assert.Contains(rows["Pressed Tune"]["steps"]!.AsArray(), s => s!.GetValue<string>().Contains("add a Discogs access token"));
        t.F.SetDiscogsToken("dg-secret");
        var dg = (await t.RunAsync(Rows(("Wax Only", "Pressed Tune")), fresh: true)).Rows[0];
        Assert.Equal(("discogs", "https://www.discogs.com/master/700", "Pressed EP", "2019"), (dg.T("status"), dg.T("url"), dg.T("release"), dg.T("year")));
        Assert.Contains("Vinyl", dg.T("note"));
        e.Sites.SetLookupSites(new[] { "bandcamp", "soundcloud" });                           // unticked: falls back, no new search
        var before = t.Requests;
        Assert.Equal("soundcloud_search", (await t.RunAsync(Rows(("Wax Only", "Pressed Tune")))).Rows[0].T("status"));
        Assert.Equal(before, t.Requests);
        Assert.Equal("soundcloud_search", (await t.RunAsync(Rows(("Beat Artist", "Late Tune")))).Rows[0].T("status"));
        e.Sites.SetLookupSites(new[] { "bandcamp", "soundcloud", "beatport" });               // ticked later: Beatport alone
        var late = (await t.RunAsync(Rows(("Beat Artist", "Late Tune")))).Rows[0];
        Assert.Equal(("beatport", "https://www.beatport.com/track/late-tune/502"), (late.T("status"), late.T("url")));
    }

    [Fact]
    public async Task New_purchases_to_download()
    {
        using var t = new Lookups();
        var db = t.App.Engine.Db;
        Assert.False(t.F.DownloadsOn());                                                       // off until switched on
        t.F.SetDownloadsOn(true);
        await ReadPurchasesAsync(t);
        t.F.ClearDownloads();
        var d = t.F.Downloads();
        Assert.Equal((true, "divided_eye", "https://bandcamp.com/divided_eye", 0L), (d.B("on"), d.T("fan"), d.T("url"), (long)d.N("count")!));
        t.Replay.Use["GET https://bandcamp.com/divided_eye?"] = 1;                             // a new purchase and a pre-order
        await t.F.CheckPurchasesAsync();
        d = t.F.Downloads();
        Assert.Equal(new[] { ("Fresh One", "new") }, d["items"]!.AsArray().OfType<JsonObject>().Select(x => (x.T("title"), x.T("reason"))));
        t.F.ClearDownloads();
        await t.F.CheckPurchasesAsync();
        Assert.Equal(0L, (long)t.F.Downloads().N("count")!);                                   // shown once only
        db.Execute("UPDATE collection_items SET release_date='2020-01-01' WHERE title='Coming Soon'");
        await t.F.CheckPurchasesAsync();
        Assert.Equal(new[] { ("Coming Soon", "released") }, t.F.Downloads()["items"]!.AsArray().OfType<JsonObject>().Select(x => (x.T("title"), x.T("reason"))));
        t.F.ClearDownloads();
        t.Replay.Use["GET https://bandcamp.com/divided_eye?"] = 2;                             // Bandcamp shows the pre-order as out
        db.Execute("UPDATE collection_items SET preorder=1 WHERE title='Coming Soon'");
        await t.F.CheckPurchasesAsync();
        Assert.Equal(new[] { "Coming Soon" }, t.F.Downloads()["items"]!.AsArray().OfType<JsonObject>().Select(x => x.T("title")));
        t.F.SetDownloadsOn(false);                                                             // switched off: dropped, nothing watched
        Assert.Equal(0L, (long)t.F.Downloads().N("count")!);
        db.Execute("UPDATE collection_items SET preorder=1, release_date='2020-01-01' WHERE title='Coming Soon'");
        await t.F.CheckPurchasesAsync();
        Assert.Equal(1L, db.Query("SELECT preorder FROM collection_items WHERE title='Coming Soon'")[0].L("preorder"));
    }
}
