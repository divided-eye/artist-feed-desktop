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

    public sealed record Summary(int Pages, int Checked, int New, int Errors, int Held, bool LookupRunning, string At);
    /// <summary>New: posts saved so far by the refreshes running now, which the window offers to show.</summary>
    public sealed record Progress(int Pages, int Done, int? SecondsLeft, int New = 0);

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
            return new Progress(pages, done, known ? (int)Math.Round(left) : null, _runs.Sum(r => Volatile.Read(ref r.Tally.New)));
        }
    }

    sealed class Tally
    {
        public int Checked, New, Errors, Held;
    }

    async Task ReadOneAsync(HttpClient client, SemaphoreSlim sem, Account account, Tally tally, Run run, CancellationToken ct)
    {
        try
        {
            await sem.WaitAsync(ct);
            try
            {
                await ReadAsync(client, account, tally, ct);
            }
            finally
            {
                sem.Release();
            }
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

    /// <summary>Read every page (or just these), several at a time.</summary>
    public async Task RefreshAsync(IReadOnlyCollection<long>? accountIds = null, CancellationToken ct = default)
    {
        var accounts = db.ListAccounts(accountIds).Where(a => sites.IsOn(a.Source)).ToList();
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
            var sems = run.By.Keys.ToDictionary(k => k, _ => new SemaphoreSlim(Concurrency));     // one queue per site
            using var client = makeClient();
            try
            {
                await Task.WhenAll(accounts.Select(a => ReadOneAsync(client, sems[a.Source], a, tally, run, ct)));
            }
            finally
            {
                foreach (var sem in sems.Values) sem.Dispose();
            }
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
                LastResult = new Summary(accounts.Count, tally.Checked, tally.New, tally.Errors, tally.Held, lookupRunning, LastRefresh);
            }
        }
        finally
        {
            lock (_gate) _runs.Remove(run);
            Interlocked.Decrement(ref _refreshing);
            Changed?.Invoke();
        }
    }

    /// <summary>The Refresh Feed button: "started", or "queued" behind a refresh already running.</summary>
    public string RequestRefresh()
    {
        lock (_gate)
        {
            if (_queued) return "queued";
            if (Refreshing)
            {
                _queued = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (Refreshing) await Task.Delay(1000);
                        await RefreshAsync();
                    }
                    catch (Exception) { }
                    finally { _queued = false; Changed?.Invoke(); }
                });
                Changed?.Invoke();
                return "queued";
            }
        }
        RefreshInBackground();
        return "started";
    }

    public void RefreshInBackground(IReadOnlyCollection<long>? accountIds = null) =>
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(accountIds); }
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
