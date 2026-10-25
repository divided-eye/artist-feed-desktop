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
}
