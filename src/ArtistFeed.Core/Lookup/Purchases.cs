using System.Globalization;
using System.Text.Json.Nodes;

namespace ArtistFeed.Core;

/// <summary>One purchase in your Bandcamp collection.</summary>
public sealed class OwnedItem
{
    public string Key = "", Title = "", Band = "", Url = "", Kind = "";
    public string Around = "";                   // its title and address as plain words, for artists only named there
}

/// <summary>A release listed on a Bandcamp page you know (tracked, or bought from).</summary>
public sealed record CatalogueItem(string Host, string Label, string Url, string Title, string Blob);

/// <summary>Tracks already known without searching: the feed, and your purchases.</summary>
public sealed class Library
{
    public Dictionary<string, List<Cand>> Feed { get; } = new();
    public Dictionary<string, List<Cand>> OwnedTracks { get; } = new();
    public List<OwnedItem> OwnedItems { get; } = new();
    public HashSet<string> OwnedUrls { get; } = new();
    public List<CatalogueItem> Catalogue { get; } = new();
    readonly Dictionary<string, List<OwnedItem>> _byName = new();
    readonly Dictionary<string, List<OwnedItem>> _byArtist = new();
    readonly Matcher _m;

    public Library(Db db, Matcher m)
    {
        _m = m;
        foreach (var r in db.Query(
                     "SELECT t.title AS name, COALESCE(NULLIF(t.artist,''), NULLIF(p.author,''), ar.name) AS artist," +
                     " p.url, COALESCE(NULLIF(p.release,''), p.title) AS release, p.published_at " +
                     "FROM tracks t JOIN posts p ON p.id=t.post_id JOIN accounts a ON a.id=p.account_id " +
                     "JOIN artists ar ON ar.id=a.artist_id WHERE a.source='bandcamp'"))
        {
            var row = new JsonObject
            {
                ["name"] = r.S("name"), ["artist"] = r.S("artist"), ["url"] = r.S("url"), ["release"] = r.S("release"),
                ["published_at"] = r.S("published_at"),
            };
            AddTo(Feed, r.S("name") ?? "", new Cand { Name = r.S("name") ?? "", Artists = new() { r.S("artist") }, Data = row });
        }
        foreach (var r in db.Query("SELECT key, title, band, url, kind FROM collection_items ORDER BY rowid"))
            OwnedItems.Add(new OwnedItem { Key = r.S("key") ?? "", Title = r.S("title") ?? "", Band = r.S("band") ?? "", Url = r.S("url") ?? "", Kind = r.S("kind") ?? "" });
        foreach (var i in OwnedItems) OwnedUrls.Add(i.Url);
        foreach (var row in db.Query("SELECT key, data FROM web_cache WHERE kind='grid'"))
        {
            var page = J.Parse(row.S("data"));
            foreach (var it in page.Objs("items"))
                if (it.B("url") && it.B("title"))
                    Catalogue.Add(new CatalogueItem(row.S("key") ?? "", page.T("name"), it.T("url"), it.T("title"),
                        $" {Matcher.Plain(it.T("title"))} {Matcher.Plain(it.T("artist"))} "));
        }
        var byKey = OwnedItems.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.Last());
        foreach (var item in OwnedItems)
        {
            var credit = item.Title.Contains(" - ") ? LinkFinder.Before(item.Title) : "";
            foreach (var name in m.ArtistParts(item.Band).Concat(m.ArtistParts(credit)).Distinct())
            {
                if (!_byName.TryGetValue(name, out var list)) _byName[name] = list = new();
                list.Add(item);
            }
            item.Around = " " + Matcher.Norm(item.Title) + " " + Matcher.Norm(Matcher.SlugWords(item.Url)) + " ";
        }
        foreach (var t in db.Query("SELECT item_key, title, artist FROM collection_tracks"))
        {
            if (!byKey.TryGetValue(t.S("item_key") ?? "", out var item)) continue;
            var title = t.S("title") ?? "";
            AddTo(OwnedTracks, title, new Cand
            {
                Name = title, Artists = new() { t.S("artist"), item.Band }, Data = item,
                Context = new() { item.Title, Matcher.SlugWords(item.Url) },
            });
        }
    }

    static void AddTo(Dictionary<string, List<Cand>> index, string title, Cand entry)
    {
        void Put(string key)
        {
            if (!index.TryGetValue(key, out var list)) index[key] = list = new();
            list.Add(entry);
        }
        Put(Matcher.Norm(title));
        var rest = LinkFinder.After(title);
        if (rest.Length > 0) Put(Matcher.Norm(rest));           // "Artist - Title" is also findable by its title
    }

    /// <summary>Is this saved page (or the album it belongs to) one of your purchases?</summary>
    public bool Owns(JsonObject rel) =>
        new[] { "asked", "url", "release_url" }.Any(k => rel.S(k) is { Length: > 0 } u && OwnedUrls.Contains(u));

    /// <summary>Purchases credited to the artist, or to whoever made this version of the track.</summary>
    public List<OwnedItem> OwnedFor(string artist, string title)
    {
        var outp = new Dictionary<string, OwnedItem>();
        foreach (var i in OwnedBy(artist)) outp.TryAdd(i.Key, i);
        foreach (var credit in _m.RemixCredits(title))
            foreach (var i in OwnedBy(credit)) outp.TryAdd(i.Key, i);
        return outp.Values.ToList();
    }

    /// <summary>Purchases credited to this artist (or titled "Artist - ...").</summary>
    public List<OwnedItem> OwnedBy(string artist)
    {
        if (_byArtist.TryGetValue(artist, out var known)) return known;
        var wanted = _m.ArtistParts(artist);
        var outp = new Dictionary<string, OwnedItem>();
        foreach (var (name, items) in _byName)
            if (wanted.Any(w => Matcher.NameSim(w, name) >= 0.85))
                foreach (var item in items) outp.TryAdd(item.Key, item);
        var named = wanted.Where(w => w.Length >= 2).ToList();
        foreach (var item in OwnedItems)                // "DNO021 - Kercha - Open The Door LP" by DNO Records
            if (!outp.ContainsKey(item.Key) && named.Any(w => item.Around.Contains($" {w} ")))
                outp[item.Key] = item;
        return _byArtist[artist] = outp.Values.ToList();
    }
}

public sealed partial class LinkFinder
{
    // ---------- Bandcamp: your purchases ----------

    public sealed class CollectionState { public bool Syncing; public string Error = "", Progress = ""; }
    public CollectionState Collection { get; } = new();
    public sealed class DeepState { public bool Running; public string Note = ""; }
    public DeepState Deep { get; } = new();
    public double DeepExtraSeconds { get; set; } = 1;     // added to the normal gap: the full read is in no hurry
    public bool DeepAuto { get; set; } = true;

    static string N0(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public string CollectionFan() => (Db.KvGet("bandcamp_fan") ?? Environment.GetEnvironmentVariable("BANDCAMP_FAN") ?? "").Trim();

    /// <summary>Set the Bandcamp username whose purchases Link Finder checks first ("" to stop).</summary>
    public string SetCollectionFan(string text)
    {
        var fan = (text ?? "").Trim().TrimEnd('/');
        if (fan.Contains("bandcamp.com/", StringComparison.OrdinalIgnoreCase))
            fan = new Uri(fan.Contains("://") ? fan : "https://" + fan).AbsolutePath.Trim('/').Split('/')[0];
        if (fan.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(fan, "^[A-Za-z0-9_.-]{1,100}$"))
            throw new ArgumentException("That doesn't look like a Bandcamp username.");
        Db.KvSet("bandcamp_fan", fan);
        return fan;
    }

    (int Labels, int LabelsRead) LabelCounts()
    {
        var hosts = KnownHosts().ToHashSet();
        var cutoff = DateTime.UtcNow.AddDays(-14).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var fresh = Db.Query("SELECT key FROM web_cache WHERE kind='grid' AND at>=?", cutoff).Select(r => r.S("key")).ToHashSet();
        return (hosts.Count, hosts.Count(fresh.Contains));
    }

    /// <summary>What the purchases panel shows: how much has been read.</summary>
    public JsonObject CollectionStatus()
    {
        var c = Db.Query("SELECT (SELECT COUNT(*) FROM collection_items) AS items," +
                         " (SELECT COUNT(*) FROM collection_tracks) AS tracks," +
                         " (SELECT COUNT(DISTINCT url) FROM collection_items) AS releases," +
                         " (SELECT COUNT(DISTINCT c.url) FROM collection_items c JOIN web_cache w" +
                         "   ON w.kind='release' AND w.key=c.url) AS releases_read")[0];
        var (labels, labelsRead) = LabelCounts();
        return new JsonObject
        {
            ["fan"] = CollectionFan(), ["items"] = c.L("items"), ["tracks"] = c.L("tracks"),
            ["synced_at"] = Db.KvGet("collection_synced_at"), ["complete"] = Db.KvGet("collection_complete") == "1",
            ["paused"] = Pace.PausedSeconds, ["releases"] = c.L("releases"), ["releases_read"] = c.L("releases_read"),
            ["labels"] = labels, ["labels_read"] = labelsRead, ["deep_running"] = Deep.Running, ["deep_note"] = Deep.Note,
            ["syncing"] = Collection.Syncing, ["error"] = Collection.Error, ["progress"] = Collection.Progress,
        };
    }

    /// <summary>Save one batch. Returns how many of the items were new.</summary>
    int StoreCollection(string fan, IEnumerable<JsonObject> items, JsonObject? tracklists, bool alert)
    {
        var byKey = new Dictionary<string, JsonObject>();
        var byFeatured = new Dictionary<string, string>();
        foreach (var it in items)
        {
            var kind = it.S("tralbum_type") is { Length: > 0 } tt ? tt : it.S("item_type") == "track" ? "t" : "a";
            var ident = it.S("tralbum_id") is { Length: > 0 } ti ? ti : it.S("item_id");
            if (string.IsNullOrEmpty(ident) || !it.B("item_url")) continue;
            var key = $"{kind}{ident}";
            byKey[key] = it;
            if (it.B("featured_track")) byFeatured[it.T("featured_track")] = key;
        }
        return Db.InTransaction(tx =>
        {
            int fresh = 0;
            foreach (var (key, it) in byKey)
            {
                var pre = it.B("is_preorder") ? 1 : 0;
                var was = tx.Query("SELECT preorder FROM collection_items WHERE key=?", key);
                if (was.Count == 0)
                {
                    fresh++;
                    if (alert && pre == 0) Alert(tx, key, "new");
                }
                else if (was[0].L("preorder") != 0 && pre == 0) Alert(tx, key, "released");
                tx.Execute("INSERT INTO collection_items(key, fan, title, band, url, kind, preorder) VALUES(?,?,?,?,?,?,?) " +
                           "ON CONFLICT(key) DO UPDATE SET title=excluded.title, band=excluded.band, url=excluded.url," +
                           " preorder=excluded.preorder",
                           key, fan, it.T("item_title"), it.T("band_name"), it.T("item_url").Split('?')[0], key[..1], pre);
            }
            foreach (var (listKey, node) in tracklists ?? new JsonObject())
            {
                var tracks = (node as JsonArray)?.OfType<JsonObject>().Where(t => t.B("title")).ToList() ?? new();
                var key = byKey.ContainsKey(listKey) ? listKey
                    : tracks.Select(t => t.S("id")).Where(id => id != null && byFeatured.ContainsKey(id)).Select(id => byFeatured[id!]).FirstOrDefault();
                if (key == null || tracks.Count == 0) continue;
                tx.Execute("DELETE FROM collection_tracks WHERE item_key=?", key);
                for (int i = 0; i < tracks.Count; i++)
                {
                    var t = tracks[i];
                    tx.Execute("INSERT INTO collection_tracks(item_key, position, title, artist) VALUES(?,?,?,?)",
                               key, t.N("track_number") is { } n && n != 0 ? (long)n : i + 1, t.T("title"), t.T("artist"));
                }
            }
            return fresh;
        });
    }

    /// <summary>Read the public collection page of the Bandcamp account set in Link Finder, newest purchases first.
    /// The first read works back through every purchase, carrying on where it stopped if interrupted;
    /// later reads only fetch purchases made since. full: start a complete re-read.</summary>
    public async Task SyncCollectionAsync(bool full = false, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var fan = CollectionFan();
        lock (Collection)
        {
            if (fan.Length == 0 || Collection.Syncing) return;
            Collection.Syncing = true;
            Collection.Error = "";
            Collection.Progress = "Reading your Bandcamp purchases…";
        }
        var hold = Pace.Foreground();
        void Say(string text)
        {
            Collection.Progress = text;
            onProgress?.Invoke(text);
        }
        long total = 0;
        try
        {
            if (Db.Query("SELECT 1 FROM collection_items WHERE fan<>? LIMIT 1", fan).Count > 0)
            {
                Db.Execute("DELETE FROM collection_tracks");          // a different account: start over
                Db.Execute("DELETE FROM collection_items");
                Db.Execute("DELETE FROM download_alerts");
                Db.KvSet("collection_complete", "");
                Db.KvSet("collection_token", "");
            }
            var complete = Db.KvGet("collection_complete") == "1";
            if (full && complete)
            {
                complete = false;
                Db.KvSet("collection_complete", "");
                Db.KvSet("collection_token", "");
            }
            using var client = _engine.MakeClient();
            var net = MakeNet(client, text => { if (text.Length > 0) Say(text); });
            var r = await net.GetAsync($"https://bandcamp.com/{Uri.EscapeDataString(fan)}", ct);
            if (r.Status == 404) throw new ArgumentException($"No Bandcamp fan page called \"{fan}\".");
            if (r.Status != 200) throw new ArgumentException($"Bandcamp answered {r.Status} for your collection page.");
            var blob = J.Parse(Html.Parse(r.Text).QuerySelector("#pagedata[data-blob]")?.GetAttribute("data-blob")) ?? new JsonObject();
            var fanId = blob.O("fan_data")?["fan_id"];
            var info = blob.O("collection_data") ?? new JsonObject();
            if (!Html.Truthy(fanId)) throw new ArgumentException("Couldn't read that collection page (is the collection public?).");
            var cache = blob.O("item_cache").O("collection") ?? new JsonObject();
            var order = info.Strs("sequence");
            if (order.Count == 0) order = cache.Select(kv => kv.Key).ToList();
            var fresh = StoreCollection(fan, order.Where(cache.ContainsKey).Select(k => cache[k]).OfType<JsonObject>(),
                                        blob.O("tracklists").O("collection"), complete);
            total = (long)(info.N("item_count") ?? 0);
            // Not finished yet: carry on from the saved place. Finished: only what is newer.
            var token = !complete ? (Db.KvGet("collection_token") is { Length: > 0 } saved ? saved : info.S("last_token")) : info.S("last_token");
            var asked = new HashSet<string>();
            while (!string.IsNullOrEmpty(token) && asked.Add(token) && (!complete || fresh > 0))
            {
                var done = Db.Query("SELECT COUNT(*) AS n FROM collection_items")[0].L("n");
                Say(total > 0 ? $"Reading your Bandcamp purchases: {N0(done)} of {N0(total)}" : $"Reading your Bandcamp purchases: {N0(done)}");
                var j = await net.PostJsonAsync(CollectionApi, new JsonObject { ["fan_id"] = fanId!.DeepClone(), ["older_than_token"] = token, ["count"] = 100 }, ct);
                if (j.Status != 200) throw new ArgumentException($"Bandcamp answered {j.Status} while reading your purchases.");
                var data = j.Json as JsonObject ?? new JsonObject();
                var items = data.Objs("items");
                fresh = StoreCollection(fan, items, data.O("tracklists"), complete);
                token = data.B("more_available") && items.Count > 0 ? data.S("last_token") : null;
                if (!complete) Db.KvSet("collection_token", token ?? "");   // where to carry on if interrupted
            }
            if (!complete) Db.KvSet("collection_complete", "1");
            Db.KvSet("collection_synced_at", Db.Now());
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var have = Db.Query("SELECT COUNT(*) AS n FROM collection_items")[0].L("n");
            var reason = e is ArgumentException ? e.Message : $"Couldn't reach Bandcamp ({e.GetType().Name}).";
            if (Db.KvGet("collection_complete") != "1" && have > 0)
                reason += $" {N0(have)}" + (total > 0 ? $" of {N0(total)}" : "") + " purchases are saved so far; the next read carries on from there.";
            Collection.Error = reason;
        }
        finally
        {
            hold.Dispose();
            Collection.Syncing = false;
            Collection.Progress = "";
        }
        if (DeepAuto) DeepReadInBackground();
    }

    public bool CollectionIsStale()
    {
        var at = Db.KvGet("collection_synced_at");
        if (string.IsNullOrEmpty(at) || Db.KvGet("collection_complete") != "1") return true;
        if (!DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)) return true;
        return DateTime.UtcNow - when > TimeSpan.FromHours(CollectionMaxAgeHours);
    }

    // ---------- Bandcamp: purchases ready to download ----------

    /// <summary>The Downloads button: off until switched on.</summary>
    public bool DownloadsOn() => Db.KvGet("downloads_on") == "1";

    /// <summary>Switched off, purchases aren't watched and the waiting list is dropped.</summary>
    public void SetDownloadsOn(bool on)
    {
        Db.KvSet("downloads_on", on ? "1" : "0");
        if (!on) ClearDownloads();
    }

    void Alert(Db.Tx tx, string key, string reason)
    {
        if (!DownloadsOn()) return;
        tx.Execute("INSERT OR IGNORE INTO download_alerts(key, reason, at) VALUES(?,?,?)", key, reason, Db.Now());
    }

    /// <summary>The purchases to download that haven't been shown yet, newest first.</summary>
    public JsonObject Downloads()
    {
        var fan = CollectionFan();
        var rows = fan.Length > 0
            ? Db.Query("SELECT i.title, i.band, i.url, a.reason, a.at FROM download_alerts a " +
                       "JOIN collection_items i ON i.key=a.key ORDER BY a.at DESC, a.rowid DESC")
            : new();
        return new JsonObject
        {
            ["on"] = DownloadsOn(), ["fan"] = fan, ["url"] = fan.Length > 0 ? $"https://bandcamp.com/{Uri.EscapeDataString(fan)}" : "",
            ["count"] = rows.Count,
            ["items"] = new JsonArray(rows.Select(r => (JsonNode?)new JsonObject
                { ["title"] = r.S("title"), ["band"] = r.S("band"), ["url"] = r.S("url"), ["reason"] = r.S("reason"), ["at"] = r.S("at") }).ToArray()),
        };
    }

    public void ClearDownloads() => Db.Execute("DELETE FROM download_alerts");

    /// <summary>Pre-orders whose release date has come are ready to download.</summary>
    async Task CheckPreordersAsync(Net net, CancellationToken ct)
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        foreach (var row in Db.Query("SELECT key, url, release_date, checked_at FROM collection_items WHERE preorder=1"))
        {
            var date = row.S("release_date") ?? "";
            if (date.Length == 0 && string.CompareOrdinal(row.S("checked_at") ?? "", yesterday) < 0)
            {
                var r = await net.GetAsync(row.S("url") ?? "", ct);
                if (r.Status == 429) throw new Blocked("Bandcamp is rate-limiting requests");
                if (r.Status == 200)
                {
                    var published = Bandcamp.ParseRelease(r.Text, row.S("url") ?? "").PublishedAt ?? "";
                    date = published.Length >= 10 ? published[..10] : published;
                }
                Db.Execute("UPDATE collection_items SET release_date=?, checked_at=? WHERE key=?", date, Db.Now(), row.S("key"));
            }
            if (date.Length > 0 && string.CompareOrdinal(date, today) <= 0)
                Db.InTransaction(tx =>
                {
                    tx.Execute("UPDATE collection_items SET preorder=0 WHERE key=?", row.S("key"));
                    Alert(tx, row.S("key")!, "released");
                    return 0;
                });
        }
    }

    /// <summary>Look for new purchases and pre-orders that have come out.</summary>
    public async Task CheckPurchasesAsync(CancellationToken ct = default)
    {
        if (CollectionFan().Length == 0 || !DownloadsOn()) return;
        await SyncCollectionAsync(ct: ct);
        using var client = _engine.MakeClient();
        await CheckPreordersAsync(MakeNet(client), ct);
    }

    /// <summary>Open every purchased release not read yet and save its whole track list (Bandcamp's collection
    /// names one track each); then the release list of every page you know, every two weeks.</summary>
    public async Task DeepReadAsync(CancellationToken ct = default)
    {
        lock (Deep)
        {
            if (Deep.Running || Collection.Syncing) return;
            Deep.Running = true;
            Deep.Note = "";
        }
        try
        {
            var todo = Db.Query("SELECT url, MIN(rowid) AS first FROM collection_items WHERE url NOT IN " +
                                "(SELECT key FROM web_cache WHERE kind='release') GROUP BY url ORDER BY first")
                .Select(r => r.S("url")!).ToList();
            foreach (var r in Db.Query("SELECT w.key, w.data FROM web_cache w WHERE w.kind='release' AND w.key IN (SELECT url FROM collection_items)"))
                if (NeedsRereading(J.Parse(r.S("data")), r.S("key") ?? "")) todo.Add(r.S("key")!);
            var pages = KnownHosts().Where(h => CacheGet("grid", h, 14) == null).ToList();
            if (todo.Count == 0 && pages.Count == 0) return;
            using var client = _engine.MakeClient();
            var net = MakeNet(client);
            foreach (var (kind, url) in todo.Select(u => ("release", u)).Concat(pages.Select(h => ("grid", h))))
            {
                while (JobsRunning())
                {
                    Deep.Note = "waiting for the lookup to finish";
                    await Task.Delay(2000, ct);               // lookups go first
                }
                Deep.Note = kind == "release" ? "" : "label pages";
                try
                {
                    if (kind == "release") await GetReleaseAsync(net, url, ct: ct);
                    else
                    {
                        Db.Execute("DELETE FROM web_cache WHERE kind='grid' AND key=?", url);       // refresh it
                        if (await GetGridAsync(net, url, ct) == null && CacheGet("grid", url, 14) == null)
                            CachePut("grid", url, new JsonObject());   // unreadable just now: don't retry for two weeks
                    }
                }
                catch (Blocked)
                {
                    Deep.Note = "Bandcamp asked for a long pause; carrying on in 15 minutes";
                    await Task.Delay(TimeSpan.FromMinutes(15), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { }                          // left unread; picked up on the next run
                if (DeepExtraSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(DeepExtraSeconds), ct);
            }
        }
        finally
        {
            Deep.Running = false;
            Deep.Note = "";
        }
    }

    public void DeepReadInBackground()
    {
        if (Deep.Running) return;
        _ = Task.Run(async () =>
        {
            try { await DeepReadAsync(_stop.Token); }
            catch (Exception) { }
        });
    }

    public void SyncInBackground(bool full = false) => _ = Task.Run(async () =>
    {
        try { await SyncCollectionAsync(full, ct: _stop.Token); }
        catch (Exception) { }
    });

    readonly CancellationTokenSource _stop = new();

    /// <summary>Started with the app: the full read of purchases after a restart, and new purchases to download.</summary>
    public void StartBackground(TimeSpan refreshEvery)
    {
        var ct = _stop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                while (!ct.IsCancellationRequested)
                {
                    try { if (CollectionFan().Length > 0 && !Collection.Syncing && DeepAuto) await DeepReadAsync(ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { }
                    await Task.Delay(TimeSpan.FromMinutes(30), ct);
                }
            }
            catch (OperationCanceledException) { }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                while (!ct.IsCancellationRequested)
                {
                    try { await CheckPurchasesAsync(ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { }
                    await Task.Delay(refreshEvery < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : refreshEvery, ct);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public void Stop() => _stop.Cancel();

    // ---------- your purchases, as a step of a lookup ----------

    /// <summary>Look in your purchases. Returns (result or null, a weaker "check" result or null).</summary>
    public async Task<(JsonObject? Result, JsonObject? Check)> OwnedStepAsync(Net net, Library lib, string artist, string title, List<string> steps, CancellationToken ct = default)
    {
        if (lib.OwnedItems.Count == 0) return (null, null);
        JsonObject? check = null;
        // a. track lists that came with the collection
        var cands = lib.OwnedTracks.GetValueOrDefault(Matcher.Norm(title)) ?? new();
        var (verdict, cand) = M.BestCandidate(artist, title, cands);
        if (cand != null)
        {
            var item = (OwnedItem)cand.Data!;
            var rel = await SafeReleaseAsync(net, item.Url, ct);
            var outp = new JsonObject
            {
                ["status"] = verdict, ["site"] = "Bandcamp", ["url"] = AlbumOf(rel, item.Url),
                ["release"] = rel.S("release") is { Length: > 0 } r ? r : item.Title, ["year"] = rel.T("year"),
                ["note"] = "In your purchases", ["found"] = $"{item.Band} — {cand.Name}",
            };
            steps.Add($"purchases: track \"{cand.Name}\" on \"{item.Title}\"" + (verdict == "bandcamp" ? "" : " (artist only partly matches)"));
            if (verdict == "bandcamp") return (outp, null);
            check = outp;
        }
        else if (cands.Count > 0 && Matcher.Distinctive(title))
        {
            // Same title, artist not recognisable from the purchase list: open the release to check.
            foreach (var c in cands.Take(3))
            {
                var rel = await SafeReleaseAsync(net, ((OwnedItem)c.Data!).Url, ct);
                var (v, name) = rel != null ? MatchInRelease(artist, title, rel) : ("", "");
                if (v == "bandcamp")
                {
                    steps.Add($"purchases: \"{name}\" on \"{rel.S("title")}\", artist confirmed on its page");
                    return (ReleaseResult(rel!, v, $"{rel.S("artist")} — {name}", "In your purchases"), null);
                }
            }
        }
        // b. releases you own by this artist: a purchase named after the track, or open them and read their track lists
        var owned = lib.OwnedFor(artist, title);
        foreach (var item in owned)
        {
            if (M.Versioned(title)) break;        // the release is named after the track, but is this version on it? open it
            if (Matcher.TitleSim(Matcher.Norm(title), Matcher.Norm(item.Title)) >= 0.88 ||
                Matcher.TitleSim(Matcher.Norm(title), Matcher.Norm(After(item.Title))) >= 0.88)
            {
                var rel = await SafeReleaseAsync(net, item.Url, ct);
                steps.Add($"purchases: you own \"{item.Title}\" by {item.Band}");
                return (new JsonObject
                {
                    ["status"] = "bandcamp", ["site"] = "Bandcamp", ["url"] = AlbumOf(rel, item.Url),
                    ["release"] = rel.S("release") is { Length: > 0 } r ? r : item.Title, ["year"] = rel.T("year"),
                    ["found"] = $"{item.Band} — {item.Title}", ["note"] = "In your purchases",
                }, null);
            }
        }
        int opened = 0;
        foreach (var item in owned.Take(OwnedReleases))
        {
            var rel = await SafeReleaseAsync(net, item.Url, ct);
            opened++;
            var (v, name) = rel != null ? MatchInRelease(artist, title, rel) : ("", "");
            if (v.Length > 0)
            {
                steps.Add($"purchases: opened {opened} release(s) by this artist, found \"{name}\" on \"{rel.S("title")}\"");
                var outp = ReleaseResult(rel!, v, $"{(rel.S("artist") is { Length: > 0 } a ? a : item.Band)} — {name}", "In your purchases");
                if (v == "bandcamp") return (outp, null);
                check ??= outp;
            }
        }
        steps.Add($"purchases: no match ({cands.Count} same-title track(s), {owned.Count} release(s) by this artist"
                  + (opened > 0 ? $", {opened} opened" : "") + ")");
        return (null, check);
    }
}
