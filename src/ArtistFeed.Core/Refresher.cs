using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArtistFeed.Core;

/// <summary>Reads tracked pages for new posts: on a timer, and on demand.</summary>
public sealed class Refresher(Db db, Sites sites, Pace pace, Func<HttpClient> makeClient)
{
    public TimeSpan Every { get; set; } = TimeSpan.FromMinutes(60);
    /// <summary>Pages whose back catalogue is still loading are visited again this often, between full refreshes.</summary>
    public TimeSpan CatchUp { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Pages read at the same time, per site. Each site has its own queue, so the quick sites (SoundCloud,
    /// YouTube) never wait behind Bandcamp, whose requests are spaced out (see Pace).</summary>
    public int Concurrency { get; set; } = 3;

    /// <summary>Quiet Bandcamp pages rest between checks: a page whose newest release is older than QuietDays is checked
    /// at most every QuietHours, instead of on every refresh. Pages with a recent release, pages never read, and pages
    /// with older releases still to load are checked every time. QuietHours 0 = off.</summary>
    public double QuietDays { get; set; } = 90;
    public double QuietHours { get; set; } = 6;

    /// <summary>Resting: quiet Bandcamp pages left for a later check. Sites: pages read on each site.</summary>
    public sealed record Summary(int Pages, int Checked, int New, int Errors, int Held, bool LookupRunning, string At,
                                 int Resting = 0, IReadOnlyDictionary<string, int>? Sites = null);
    /// <summary>New: posts saved so far by the refreshes running now, which the window offers to show.</summary>
    public sealed record Progress(int Pages, int Done, int? SecondsLeft, int New = 0,
                                  IReadOnlyDictionary<string, (int Pages, int Done)>? By = null);

    readonly object _gate = new();
    int _refreshing;
    bool _queued;
    readonly List<Run> _runs = new();

    public bool Refreshing => Volatile.Read(ref _refreshing) > 0;
    public bool Queued => _queued;
    public string? LastRefresh { get; private set; } = db.KvGet("last_refresh");
    public Summary? LastResult { get; private set; }
    public int Held { get; private set; }

    /// <summary>Raised (on a worker thread) whenever a refresh starts, reads a page, or ends.</summary>
    public event Action? Changed;

    sealed class Site
    {
        public int Pages, Done;
        public double? Finished;
    }

    sealed class Run
    {
        public int Pages, Done;
        public Tally Tally = new();
        public readonly Stopwatch Started = Stopwatch.StartNew();
        public readonly Dictionary<string, Site> By = new();
    }

    Dictionary<string, double> SavedPace()
    {
        try
        {
            if (Html.ParseJson(db.KvGet("refresh_pace") ?? "{}") is JsonObject o)
                return o.Where(kv => Html.AsNumber(kv.Value) != null).ToDictionary(kv => kv.Key, kv => Html.AsNumber(kv.Value)!.Value);
        }
        catch (JsonException) { }
        return new();
    }

    /// <summary>Pages checked so far in the refreshes running now, and a rough estimate of the time left.
    /// Each site is timed on its own; the refresh ends when the slowest site does.</summary>
    public Progress? CurrentProgress()
    {
        lock (_gate)
        {
            if (_runs.Count == 0) return null;
            var saved = SavedPace();
            int pages = _runs.Sum(r => r.Pages), done = _runs.Sum(r => r.Done);
            double left = 0;
            bool known = false;
            foreach (var run in _runs)
            {
                var elapsed = Math.Max(run.Started.Elapsed.TotalSeconds, 0.001);
                foreach (var (key, site) in run.By)
                {
                    var remaining = site.Pages - site.Done;
                    if (remaining <= 0) continue;
                    double each;
                    if (site.Done >= 5 || (site.Done > 0 && !saved.ContainsKey(key))) each = elapsed / site.Done;
                    else if (saved.TryGetValue(key, out var s) && s > 0) each = s;
                    else continue;
                    known = true;
                    left = Math.Max(left, remaining * each);
                }
            }
            var by = new Dictionary<string, (int Pages, int Done)>();          // pages per site, for "Bandcamp 40 of 110"
            foreach (var run in _runs)
                foreach (var (key, site) in run.By)
                {
                    var (p, d) = by.GetValueOrDefault(key);
                    by[key] = (p + site.Pages, d + site.Done);
                }
            return new Progress(pages, done, known ? (int)Math.Round(left) : null, _runs.Sum(r => Volatile.Read(ref r.Tally.New)), by);
        }
    }

    sealed class Tally
    {
        public int Checked, New, Errors, Held;
    }

    async Task ReadOneAsync(HttpClient client, Account account, Tally tally, Run run, CancellationToken ct)
    {
        try
        {
            await ReadAsync(client, account, tally, ct);
        }
        finally
        {
            lock (_gate)
            {
                run.Done++;
                var site = run.By[account.Source];
                site.Done++;
                if (site.Done == site.Pages) site.Finished = run.Started.Elapsed.TotalSeconds;
            }
            Changed?.Invoke();
        }
    }

    async Task ReadAsync(HttpClient client, Account account, Tally tally, CancellationToken ct)
    {
        var source = sites.Get(account.Source);
        if (source == null || !sites.IsOn(source.Key)) return;      // a site switched off isn't checked
        string? problem = null;
        try
        {
            var known = db.KnownIds(account.Id);
            var result = await source.FetchAsync(client, account, known, ct);
            db.SaveResult(account, result);
            Interlocked.Add(ref tally.New, result.Posts.Count(p => !known.Contains(p.ExternalId)));
            Interlocked.Increment(ref tally.Checked);
        }
        catch (Pace.BusyException)
        {
            Held++;                                             // not an error: tried again on the next refresh
            Interlocked.Increment(ref tally.Held);
        }
        catch (SourceException e) { problem = e.Message; }
        catch (Http.StatusException e) { problem = $"{source.Label} answered with error {(int)e.Code}."; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            problem = $"Couldn't reach {source.Label} ({e.GetType().Name}).";
        }
        catch (Exception e)                                      // a site changed its layout, most likely
        {
            problem = $"Unexpected problem reading this page: {e.Message}";
        }
        if (problem != null)
        {
            db.SaveError(account.Id, problem);
            Interlocked.Increment(ref tally.Errors);
        }
    }

    /// <summary>The quiet Bandcamp pages not due for a check this time: no release in QuietDays, and checked less
    /// than QuietHours ago.</summary>
    public HashSet<long> Resting(IEnumerable<Account> accounts) => QuietPages(accounts, onlyResting: true);

    /// <summary>Bandcamp pages with no release in QuietDays (read before, nothing still to load). onlyResting: just the
    /// ones checked less than QuietHours ago, which a normal refresh leaves out.</summary>
    public HashSet<long> QuietPages(IEnumerable<Account> accounts, bool onlyResting = false)
    {
        var resting = new HashSet<long>();
        if (onlyResting && QuietHours <= 0) return resting;
        var newest = db.Query("SELECT account_id, MAX(COALESCE(published_at, posted_at)) AS newest FROM posts GROUP BY account_id")
            .Where(r => r["newest"] is string).ToDictionary(r => (long)r["account_id"]!, r => (string)r["newest"]!);
        var now = DateTime.UtcNow;
        var quietBefore = now.AddDays(-QuietDays).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var checkedAfter = now.AddHours(-QuietHours).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var a in accounts)
        {
            if (a.Source != "bandcamp" || string.IsNullOrEmpty(a.LastChecked) || (a.LastError ?? "").Contains("still to load")) continue;
            if (newest.TryGetValue(a.Id, out var last) && string.CompareOrdinal(last, quietBefore) < 0
                && (!onlyResting || string.CompareOrdinal(a.LastChecked, checkedAfter) > 0))
                resting.Add(a.Id);
        }
        return resting;
    }

    /// <summary>Read every page (or just these), several at a time. A full refresh leaves out quiet Bandcamp pages not
    /// due yet (see QuietDays) unless everything is asked for; pages asked for by id (new pages, Check now) are always read.</summary>
    public async Task RefreshAsync(IReadOnlyCollection<long>? accountIds = null, CancellationToken ct = default, bool everything = false)
    {
        var accounts = db.ListAccounts(accountIds).Where(a => sites.IsOn(a.Source)).ToList();
        var resting = accountIds == null && !everything ? Resting(accounts) : new HashSet<long>();
        accounts = accounts.Where(a => !resting.Contains(a.Id)).ToList();
        if (accounts.Count == 0) return;
        Interlocked.Increment(ref _refreshing);
        Held = 0;
        var lookupRunning = pace.OnHold;
        var run = new Run { Pages = accounts.Count };
        var tally = run.Tally;
        foreach (var a in accounts)
        {
            if (!run.By.TryGetValue(a.Source, out var s)) run.By[a.Source] = s = new Site();
            s.Pages++;
        }
        lock (_gate) _runs.Add(run);
        Changed?.Invoke();
        try
        {
            // One queue per site, read in order by a few workers each. Quiet Bandcamp pages that are due go last, so pages
            // with recent releases (and new pages) are read first: after the app has been closed for a while, every quiet
            // page is due again, and new releases shouldn't wait behind them.
            var quiet = QuietPages(accounts);
            var queues = accounts.GroupBy(a => a.Source)
                .Select(g => new System.Collections.Concurrent.ConcurrentQueue<Account>(g.OrderBy(a => quiet.Contains(a.Id) ? 1 : 0)))
                .ToList();
            using var client = makeClient();
            async Task Worker(System.Collections.Concurrent.ConcurrentQueue<Account> queue)
            {
                while (queue.TryDequeue(out var a)) await ReadOneAsync(client, a, tally, run, ct);
            }
            await Task.WhenAll(queues.SelectMany(q => Enumerable.Range(0, Math.Min(Math.Max(1, Concurrency), q.Count)).Select(_ => Worker(q))));
            if (accountIds == null)
            {
                var now = SavedPace();                           // each site's pace, remembered for the next estimate
                lock (_gate)
                    foreach (var (key, site) in run.By)
                        if (site.Pages >= 5 && site.Finished != null)
                            now[key] = Math.Round(site.Finished.Value / site.Pages, 3);
                db.KvSet("refresh_pace", JsonSerializer.Serialize(now));
                LastRefresh = Db.Now();
                db.KvSet("last_refresh", LastRefresh);
                Dictionary<string, int> perSite;
                lock (_gate) perSite = run.By.ToDictionary(kv => kv.Key, kv => kv.Value.Pages);
                LastResult = new Summary(accounts.Count, tally.Checked, tally.New, tally.Errors, tally.Held, lookupRunning, LastRefresh,
                                         resting.Count, perSite);
            }
        }
        finally
        {
            lock (_gate) _runs.Remove(run);
            Interlocked.Decrement(ref _refreshing);
            Changed?.Invoke();
        }
    }

    bool _queuedEverything;

    /// <summary>The Refresh Feed button: "started", or "queued" behind a refresh already running.
    /// everything: the quiet Bandcamp pages too (Check them now).</summary>
    public string RequestRefresh(bool everything = false)
    {
        lock (_gate)
        {
            if (everything && Refreshing) _queuedEverything = true;
            if (_queued) return "queued";
            if (Refreshing)
            {
                _queued = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (Refreshing) await Task.Delay(1000);
                        bool all;
                        lock (_gate)
                        {
                            all = _queuedEverything;
                            _queuedEverything = false;
                        }
                        await RefreshAsync(everything: all);
                    }
                    catch (Exception) { }
                    finally { _queued = false; Changed?.Invoke(); }
                });
                Changed?.Invoke();
                return "queued";
            }
        }
        RefreshInBackground(everything: everything);
        return "started";
    }

    public void RefreshInBackground(IReadOnlyCollection<long>? accountIds = null, bool everything = false) =>
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(accountIds, everything: everything); }
            catch (Exception) { }
        });

    /// <summary>Pages with older releases still to load.</summary>
    public List<long> BacklogAccounts() =>
        db.ListAccounts().Where(a => (a.LastError ?? "").Contains("still to load")).Select(a => a.Id).ToList();

    /// <summary>A full refresh every Every; in between, pages with a back catalogue still to load get
    /// short catch-up visits, so a long catalogue takes hours rather than days.</summary>
    public async Task RunScheduleAsync(CancellationToken ct)
    {
        LastRefresh = db.KvGet("last_refresh");
        var every = TimeSpan.FromMinutes(Math.Max(Every.TotalMinutes, 5));
        var step = TimeSpan.FromMinutes(Math.Max(CatchUp.TotalMinutes, 2));
        while (!ct.IsCancellationRequested)
        {
            try { await RefreshAsync(ct: ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { }
            var waited = TimeSpan.Zero;
            while (waited < every)
            {
                var nap = step < every - waited ? step : every - waited;
                try { await Task.Delay(nap, ct); }
                catch (OperationCanceledException) { return; }
                waited += nap;
                if (waited >= every) break;
                try
                {
                    var behind = BacklogAccounts();
                    if (behind.Count > 0 && !pace.OnHold && !Refreshing) await RefreshAsync(behind, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { }
            }
        }
    }
}
