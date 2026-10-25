using System.Collections.Concurrent;
using ArtistFeed.Core;
using Xunit;

namespace ArtistFeed.Tests;

/// <summary>The faster refresh: a queue per site, newest releases first on a page's first read,
/// and the new posts counted while a refresh runs (the web version's tests).</summary>
public class RefreshSpeedTests
{
    sealed class Fake(string key, int millis, Refresher? watch = null, ConcurrentQueue<string>? finished = null, List<int>? seenNew = null) : ISource
    {
        public Refresher? Watch { get; set; } = watch;
        public string Key => key;
        public string Label => key;
        public string Color => "#888888";
        public string Placeholder => "";
        public (string Handle, string Url) Normalize(string text) => (text, "https://x/" + text);

        public async Task<FetchResult> FetchAsync(HttpClient client, Account account, HashSet<string> knownIds, CancellationToken ct = default)
        {
            await Task.Delay(millis, ct);
            finished!.Enqueue(key);
            if (key == "bandcamp") lock (seenNew!) seenNew.Add(Watch!.CurrentProgress()!.New);    // Bandcamp page done: SoundCloud's are counted already
            var r = new FetchResult();
            r.Posts.Add(new Post { ExternalId = "x" + account.Id, Title = "t", Url = "https://x/t" + account.Id, Kind = "track" });
            return r;
        }
    }

    [Fact]
    public async Task Quick_sites_do_not_wait_behind_bandcamp()
    {
        var folder = Path.Combine(Path.GetTempPath(), "artistfeed-speed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var finished = new ConcurrentQueue<string>();
        var seenNew = new List<int>();
        var bc = new Fake("bandcamp", 300, finished: finished, seenNew: seenNew);
        var sc = new Fake("soundcloud", 10, finished: finished, seenNew: seenNew);
        using var db = new Db(Path.Combine(folder, "feed.db"));
        var sites = new Sites(db, new ISource[] { bc, sc });
        var refresher = new Refresher(db, sites, new Pace(0, 0, 0), () => new HttpClient());
        sc.Watch = bc.Watch = refresher;
        var ids = new List<long>();
        for (int i = 0; i < 7; i++)
        {
            var artist = db.AddArtist("Artist " + i);
            var site = i < 4 ? "bandcamp" : "soundcloud";
            ids.Add(db.AddAccount(artist, site, "a" + i, $"https://x/{site}/{i}"));
        }
        await refresher.RefreshAsync(ids);
        Assert.Equal(new[] { "soundcloud", "soundcloud", "soundcloud" }, finished.Take(3));   // not held up by the slow Bandcamp pages
        Assert.True(seenNew.Min() >= 3, string.Join(",", seenNew));   // the new posts so far, counted while Bandcamp still runs
        db.Dispose();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_new_bandcamp_page_reads_its_newest_releases_first()
    {
        using var app = new TestApp();
        app.Engine.Bandcamp.FirstRead = 1;
        var account = new Account { Id = 99999, Source = "bandcamp", Handle = "nightferry", Url = "https://nightferry.bandcamp.com" };
        using var client = app.Engine.MakeClient();
        var first = await app.Engine.Bandcamp.FetchAsync(client, account, new HashSet<string>());
        Assert.Equal(new[] { "first" }, first.Posts.Select(p => p.ExternalId.Split('/').Last()));
        Assert.Contains("still to load", first.Warning);
        var later = await app.Engine.Bandcamp.FetchAsync(client, account, first.Posts.Select(p => p.ExternalId).ToHashSet());
        Assert.Equal(2, later.Posts.Count);                // then MaxNew (2 in the tests) at a time
    }

    static string Ago(TimeSpan t) => (DateTime.UtcNow - t).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Quiet_bandcamp_pages_rest_between_checks()
    {
        using var app = new TestApp();
        var db = app.Engine.Db;
        var r = app.Engine.Refresher;
        long Page(string site, string? newest, TimeSpan? checkedAgo, string? error = null)
        {
            var artist = db.AddArtist("A" + Guid.NewGuid().ToString("N")[..6]);
            var id = db.AddAccount(artist, site, "h" + artist, $"https://x/{site}/{artist}");
            db.Execute("UPDATE accounts SET last_checked=?, last_error=? WHERE id=?", checkedAgo is { } c ? Ago(c) : null, error, id);
            if (newest != null)
                db.Execute("INSERT INTO posts(account_id, external_id, title, url, published_at, first_seen) VALUES(?,?,?,?,?,?)",
                           id, "p" + id, "t", "https://x/t" + id, newest, Ago(TimeSpan.Zero));
            return id;
        }
        var old = Ago(TimeSpan.FromDays(400));
        var quiet = Page("bandcamp", old, TimeSpan.FromHours(1));                              // rests
        var recent = Page("bandcamp", Ago(TimeSpan.FromDays(10)), TimeSpan.FromHours(1));      // released lately: checked
        var due = Page("bandcamp", old, TimeSpan.FromHours(7));                                // quiet, but due again
        var never = Page("bandcamp", old, null);                                               // never read
        var loading = Page("bandcamp", old, TimeSpan.FromHours(1), "3 older release(s) still to load");
        var sc = Page("soundcloud", old, TimeSpan.FromHours(1));                               // other sites: always
        var empty = Page("bandcamp", null, TimeSpan.FromHours(1));                             // nothing saved yet
        Assert.Equal(new HashSet<long> { quiet }, r.Resting(db.ListAccounts()));
        r.QuietHours = 0;
        Assert.Empty(r.Resting(db.ListAccounts()));                                            // switched off
        _ = (recent, due, never, loading, sc, empty);
    }

    [Fact]
    public async Task Check_them_now_reads_the_quiet_pages_too()
    {
        var folder = Path.Combine(Path.GetTempPath(), "artistfeed-quiet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var finished = new ConcurrentQueue<string>();
        var seen = new List<int>();
        var bc = new Fake("bandcamp", 0, finished: finished, seenNew: seen);
        var sc = new Fake("soundcloud", 0, finished: finished, seenNew: seen);
        using (var db = new Db(Path.Combine(folder, "feed.db")))
        {
            var refresher = new Refresher(db, new Sites(db, new ISource[] { bc, sc }), new Pace(0, 0, 0), () => new HttpClient());
            bc.Watch = sc.Watch = refresher;
            var quiet = db.AddAccount(db.AddArtist("Quiet"), "bandcamp", "q", "https://x/q");
            db.AddAccount(db.AddArtist("Busy"), "bandcamp", "b", "https://x/b");
            db.AddAccount(db.AddArtist("Sc"), "soundcloud", "s", "https://x/s");
            db.Execute("UPDATE accounts SET last_checked=? WHERE id=?", Ago(TimeSpan.FromHours(1)), quiet);
            db.Execute("INSERT INTO posts(account_id, external_id, title, url, published_at, first_seen) VALUES(?,?,?,?,?,?)",
                       quiet, "old", "t", "https://x/old", Ago(TimeSpan.FromDays(400)), Ago(TimeSpan.Zero));
            await refresher.RefreshAsync();
            Assert.Equal(2, finished.Count);
            Assert.Equal(1, refresher.LastResult!.Resting);
            Assert.Equal(new Dictionary<string, int> { ["bandcamp"] = 1, ["soundcloud"] = 1 }, refresher.LastResult.Sites);
            finished.Clear();
            db.Execute("UPDATE accounts SET last_checked=? WHERE id=?", Ago(TimeSpan.FromHours(1)), quiet);
            await refresher.RefreshAsync(everything: true);
            Assert.Equal(3, finished.Count);
            Assert.Equal(0, refresher.LastResult!.Resting);
            Assert.Equal(new Dictionary<string, int> { ["bandcamp"] = 2, ["soundcloud"] = 1 }, refresher.LastResult.Sites);
        }
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }
}
