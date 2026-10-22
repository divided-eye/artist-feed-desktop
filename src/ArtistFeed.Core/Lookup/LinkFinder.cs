using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArtistFeed.Core;

/// <summary>A site is refusing requests; stop asking it for the rest of this lookup.</summary>
public sealed class Blocked(string message) : Exception(message);

/// <summary>An answer read whole: status, text, and the address it finally came from.</summary>
public sealed record Resp(int Status, string Text, string Url)
{
    public JsonNode? Json => Html.ParseJson(Text);
}

/// <summary>
/// Requests for Link Finder, taken at the app-wide Bandcamp pace. A 429 answer is waited out and
/// retried, several times, before it is passed back to the caller.
/// </summary>
public sealed class Net(Pace pace, HttpClient client, Action<string>? onWait = null)
{
    public const int Tries = 6;
    public HttpClient Client { get; } = client;
    public int Count;
    public bool Fresh { get; set; }                         // an explicit "search again": don't reuse saved searches

    public async Task<Resp> RequestAsync(Func<HttpRequestMessage> make, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            await pace.WaitAsync(background: false, ct);
            Interlocked.Increment(ref Count);
            using var r = await Client.SendAsync(make(), ct);
            if ((int)r.StatusCode != 429 || attempt == Tries - 1)
            {
                onWait?.Invoke("");
                return new Resp((int)r.StatusCode, await r.Content.ReadAsStringAsync(ct), r.RequestMessage?.RequestUri?.ToString() ?? "");
            }
            var seconds = pace.PauseFor(r, attempt);
            pace.SlowDown(seconds);
            onWait?.Invoke($"Bandcamp asked for a pause; carrying on in {(int)seconds}s");
        }
    }

    public Task<Resp> GetAsync(string url, CancellationToken ct = default) =>
        RequestAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);

    public Task<Resp> GetAsync(string url, IDictionary<string, string> query, CancellationToken ct = default) =>
        GetAsync(Http.Query(url, query), ct);

    public Task<Resp> PostJsonAsync(string url, JsonNode body, CancellationToken ct = default) =>
        RequestAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") }, ct);
}

/// <summary>
/// Link Finder, as in the web version (lookup.py): for each "artist / title" row, the Bandcamp release
/// the track is on; failing that, the artist's page on Bandcamp or SoundCloud. Everything read is kept
/// in the shared database, in the web version's shapes, so the two versions can share one.
/// </summary>
public sealed partial class LinkFinder
{
    readonly Engine _engine;
    Db Db => _engine.Db;
    Pace Pace => _engine.Pace;
    public Matcher M { get; }

    public int MaxRows { get; set; } = 500;
    public int ScanReleases { get; set; } = 40;       // releases opened per artist page, per row
    public int OwnedReleases { get; set; } = 15;      // purchases by the artist opened per row
    public double SearchDays { get; set; } = 7;       // how long Bandcamp's search answers are reused
    public double CollectionMaxAgeHours { get; set; } = 24;
    public const string SearchUrl = "https://bandcamp.com/search";
    public const string SearchApi = "https://bandcamp.com/api/bcsearch_public_api/1/autocomplete_elastic";
    public const string CollectionApi = "https://bandcamp.com/api/fancollection/1/collection_items";
    public const string CacheVersion = "v2";
    public const int EngineVersion = 12;              // the web version's ENGINE

    public LinkFinder(Engine engine)
    {
        _engine = engine;
        M = new Matcher(engine.Db);
    }

    public Net MakeNet(HttpClient client, Action<string>? onWait = null) => new(Pace, client, onWait);

    // ---------- small stores ----------

    public JsonNode? CacheGet(string kind, string key, double maxAgeDays)
    {
        var rows = Db.Query("SELECT data, at FROM web_cache WHERE kind=? AND key=?", kind, key);
        if (rows.Count == 0) return null;
        if (!DateTime.TryParse((string)rows[0]["at"]!, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) return null;
        if (DateTime.UtcNow - at > TimeSpan.FromDays(maxAgeDays)) return null;
        return Html.ParseJson((string)rows[0]["data"]!);
    }

    public void CachePut(string kind, string key, JsonNode? data) =>
        Db.Execute("INSERT INTO web_cache(kind, key, data, at) VALUES(?,?,?,?) " +
                   "ON CONFLICT(kind, key) DO UPDATE SET data=excluded.data, at=excluded.at",
                   kind, key, J.Dump(data), Db.Now());

    public static string RootOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}" : "://";

    // ---------- Bandcamp: pages ----------

    // Every track on every release page the lookup has ever opened, by tidied title.
    Dictionary<string, List<Cand>>? _pageIndex;
    readonly object _indexLock = new();

    void IndexRelease(Dictionary<string, List<Cand>> index, JsonObject? rel)
    {
        if (rel == null || rel.Objs("tracks").Count == 0) return;
        var title = rel.T("title");
        var extra = new List<string?> { rel.S("artist"), title.Contains(" - ") ? title[..title.IndexOf(" - ", StringComparison.Ordinal)] : "" };
        foreach (var t in rel.Objs("tracks"))
        {
            var name = t.T("title");
            var entry = new Cand { Name = name, Artists = new List<string?> { t.S("artist") }.Concat(extra).ToList(),
                                   Context = Matcher.ReleaseContext(rel), Data = rel };
            Add(index, Matcher.Norm(name), entry);
            var dash = name.IndexOf(" - ", StringComparison.Ordinal);
            if (dash >= 0 && name[(dash + 3)..].Length > 0)     // "Artist - Title" is also findable by its title
                Add(index, Matcher.Norm(name[(dash + 3)..]), entry);
        }
    }

    static void Add<T>(Dictionary<string, List<T>> index, string key, T entry)
    {
        if (!index.TryGetValue(key, out var list)) index[key] = list = new List<T>();
        list.Add(entry);
    }

    public Dictionary<string, List<Cand>> PageIndex()
    {
        lock (_indexLock)
        {
            if (_pageIndex != null) return _pageIndex;
            var index = new Dictionary<string, List<Cand>>();
            foreach (var row in Db.Query("SELECT data FROM web_cache WHERE kind='release'"))
                IndexRelease(index, J.Parse((string?)row["data"]));
            return _pageIndex = index;
        }
    }

    public const int PageFormat = 5;   // 2: track names only written out in the description; 3: a track page's album
                                       // from the player data; 4: sold from another page; 5: placeholders marked

    /// <summary>Saved by an older version that may have missed something: read the page again.</summary>
    public static bool NeedsRereading(JsonObject? saved, string url)
    {
        if (saved == null || saved.Count == 0) return false;
        var v = (int)(saved.N("v") ?? 1);
        if (v < 2 && url.Contains("/album/") && saved.Objs("tracks").Count <= 1) return true;
        if (v < 3 && url.Contains("/track/") && !saved.B("release_url")) return true;
        return v < 5 && saved.Objs("tracks").Count == 0;
    }

    /// <summary>The link to give for a page: the album when the page is a single track on one.</summary>
    public static string AlbumOf(JsonObject? rel, string url) => rel.S("release_url") is { Length: > 0 } a ? a : url;

    /// <summary>A release or track page, reduced to what matching needs. Remembered for six months.
    /// A page that only shows a release sold from another page (a label's) gives that page.</summary>
    public async Task<JsonObject?> GetReleaseAsync(Net net, string url, bool follow = true, CancellationToken ct = default)
    {
        var saved = CacheGet("release", url, 180);
        if (saved != null && !NeedsRereading(saved as JsonObject, url))
            return saved is JsonObject o && o.Count > 0 ? o : null;
        var r = await net.GetAsync(url, ct);
        if (r.Status == 429) throw new Blocked("Bandcamp is rate-limiting requests");
        if (r.Status != 200)
        {
            if (r.Status == 404) CachePut("release", url, new JsonObject());
            return null;
        }
        var rel = Bandcamp.ParseRelease(r.Text, url);
        if (rel.SoldAt != null && follow)
        {
            var real = await GetReleaseAsync(net, rel.SoldAt, false, ct);
            if (real != null)
            {
                var data = real.With(("asked", url));
                CachePut("release", url, data);
                return data;
            }
        }
        var tracks = new JsonArray();
        foreach (var t in rel.Tracks ?? new List<Track>())
            tracks.Add(new JsonObject { ["title"] = t.Title, ["artist"] = t.Artist ?? "" });
        foreach (var t in rel.ListedTracks)
            tracks.Add(new JsonObject { ["title"] = t, ["artist"] = "", ["listed"] = true });
        var outp = new JsonObject
        {
            ["url"] = rel.Url, ["asked"] = url, ["title"] = rel.Title ?? "", ["artist"] = rel.Author ?? "",
            ["year"] = (rel.PublishedAt ?? "").Length >= 4 ? rel.PublishedAt![..4] : rel.PublishedAt ?? "",
            ["release"] = rel.ReleaseName ?? "", ["release_url"] = rel.ReleaseUrl ?? "", ["label"] = rel.Label ?? "",
            ["placeholder"] = rel.Placeholder, ["tracks"] = tracks,
            ["v"] = rel.SoldAt == null ? PageFormat : PageFormat - 1,     // the other page unread: try again next time
        };
        CachePut("release", url, outp);
        lock (_indexLock) if (_pageIndex != null) IndexRelease(_pageIndex, outp);
        return outp;
    }

    /// <summary>An artist or label page: its name and the releases listed on it. Remembered for three days.</summary>
    public async Task<JsonObject?> GetGridAsync(Net net, string baseUrl, CancellationToken ct = default)
    {
        var saved = CacheGet("grid", baseUrl, 3);
        if (saved != null) return saved is JsonObject o && o.Count > 0 ? o : null;
        var r = await net.GetAsync(baseUrl + "/music", ct);
        if (r.Status == 429) throw new Blocked("Bandcamp is rate-limiting requests");
        if (r.Status != 200)
        {
            if (r.Status == 404) CachePut("grid", baseUrl, new JsonObject());
            return null;
        }
        var items = new JsonArray();
        foreach (var i in Bandcamp.ParseMusicGrid(r.Text, baseUrl))
            items.Add(new JsonObject { ["title"] = i.Title, ["url"] = i.Url, ["artist"] = i.Author ?? "" });
        if (items.Count == 0)                                // a page with one release shows it directly
        {
            var rel = Bandcamp.ParseRelease(r.Text, r.Url.Length > 0 ? r.Url : baseUrl + "/music");
            if (rel.IsRelease) items.Add(new JsonObject { ["title"] = rel.Title ?? "", ["url"] = rel.Url, ["artist"] = rel.Author ?? "" });
        }
        var data = new JsonObject { ["name"] = Bandcamp.ParseProfile(r.Text).Name ?? "", ["items"] = items };
        CachePut("grid", baseUrl, data);
        return data;
    }

    /// <summary>Is the wanted track on this release? (verdict, matched track title)</summary>
    public (string Verdict, string Name) MatchInRelease(string artist, string title, JsonObject rel)
    {
        var relTitle = rel.T("title");
        var extra = new List<string?> { rel.S("artist"), relTitle.Contains(" - ") ? relTitle[..relTitle.IndexOf(" - ", StringComparison.Ordinal)] : "" };
        var around = Matcher.ReleaseContext(rel);
        var cands = rel.Objs("tracks").Select(t => new Cand
            { Name = t.T("title"), Artists = new List<string?> { t.S("artist") }.Concat(extra).ToList(), Context = around }).ToList();
        if (cands.Count == 0 && relTitle.Length > 0)        // no track list on the page: go by its title
            cands.Add(new Cand { Name = relTitle, Artists = extra, Context = around });
        var (verdict, cand) = M.BestCandidate(artist, title, cands);
        if (verdict == "bandcamp" && rel.B("placeholder")) verdict = "bandcamp_check";   // nothing to buy there
        return (verdict, cand?.Name ?? "");
    }

    /// <summary>The link to give for a matched page: the album when the page is a track on one.</summary>
    public static JsonObject ReleaseResult(JsonObject rel, string verdict, string found, string source) => new()
    {
        ["status"] = verdict, ["site"] = "Bandcamp", ["url"] = rel.S("release_url") is { Length: > 0 } a ? a : rel.T("url"),
        ["release"] = rel.S("release") is { Length: > 0 } r ? r : rel.T("title"), ["year"] = rel.T("year"),
        ["found"] = found, ["note"] = source + (rel.B("placeholder") ? "; nothing to buy on this page" : ""),
    };

    // ---------- Bandcamp: search ----------

    /// <summary>kind: "t" tracks, "a" albums, "b" artists and labels. Answers are kept SearchDays.</summary>
    public async Task<List<JsonObject>> BandcampSearchAsync(Net net, string text, string kind, CancellationToken ct = default)
    {
        var key = $"{kind}|{Matcher.Plain(text)}";
        if (!net.Fresh && CacheGet("search", key, SearchDays) is JsonArray saved)
            return saved.OfType<JsonObject>().ToList();
        var r = await net.GetAsync(SearchUrl, new Dictionary<string, string> { ["q"] = text, ["item_type"] = kind }, ct);
        if (r.Status == 200)
        {
            var found = new JsonArray(Bandcamp.ParseSearch(r.Text).Select(c => (JsonNode?)new JsonObject
            {
                ["type"] = c.Type, ["name"] = c.Name, ["url"] = c.Url, ["artist"] = c.Artist, ["album"] = c.Album, ["released"] = c.Released,
            }).ToArray());
            CachePut("search", key, found);
            return found.OfType<JsonObject>().ToList();
        }
        // Search page refused: try the search box's JSON endpoint before giving up.
        try
        {
            var j = await net.PostJsonAsync(SearchApi, new JsonObject
                { ["search_text"] = text, ["search_filter"] = kind, ["full_page"] = false, ["fan_id"] = null }, ct);
            if (j.Status == 200)
            {
                var names = new Dictionary<string, string> { ["t"] = "track", ["a"] = "album", ["b"] = "artist" };
                var results = (j.Json?["auto"]?["results"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();
                var found = new JsonArray(results
                    .Where(x => x.S("type") == kind && (x.B("item_url_path") || x.B("item_url_root")))
                    .Select(x => (JsonNode?)new JsonObject
                    {
                        ["type"] = names.GetValueOrDefault(x.T("type")) ?? "", ["name"] = x.T("name"), ["artist"] = x.T("band_name"),
                        ["album"] = x.T("album_name"), ["url"] = x.S("item_url_path") is { Length: > 0 } p ? p : x.T("item_url_root"),
                        ["released"] = "",
                    }).ToArray());
                CachePut("search", key, found);
                return found.OfType<JsonObject>().ToList();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { }
        throw new Blocked($"Bandcamp search answered {r.Status}");
    }

    public static string YearIn(string? text)
    {
        var m = Regex.Match(text ?? "", @"\b(19|20)\d{2}\b");
        return m.Success ? m.Value : "";
    }

    static Cand SearchCand(JsonObject c, List<string?> context) =>
        new() { Name = c.T("name"), Artists = new List<string?> { c.S("artist") }, Context = context, Data = c };

    /// <summary>Bandcamp search, several ways. Returns a result or null.</summary>
    public async Task<JsonObject?> SearchStepAsync(Net net, string artist, string title, List<string> steps, CancellationToken ct = default)
    {
        var parts = M.ArtistParts(artist);
        var queries = new List<(string Kind, string Text)> { ("t", $"{artist} {title}".Trim()) };
        var credits = M.RemixCredits(title);
        if (credits.Count > 0) queries.Add(("t", $"{credits[0]} {Matcher.BaseTitle(title)}"));     // as the remixer's own upload
        if (parts.Count > 1) queries.AddRange(parts.Skip(1).Take(2).Select(p => ("t", $"{p} {title}")));
        if (Matcher.Norm(title).Length >= 4) queries.Add(("t", title));                            // title alone; artist must still match
        queries.Add(("a", $"{artist} {title}".Trim()));                                            // a release named after the track
        if (Matcher.Distinctive(title)) queries.Add(("a", title));
        var seen = new HashSet<string>();
        var verified = new HashSet<string>();
        JsonObject? check = null;
        foreach (var (kind, text) in queries.Distinct())
        {
            var found = (await BandcampSearchAsync(net, text, kind, ct)).Where(c => !seen.Contains(c.T("url"))).ToList();
            foreach (var c in found) seen.Add(c.T("url"));
            var want = kind == "t" ? "track" : "album";
            var cands = found.Where(c => c.T("type") == want || c.T("type") == "")
                .Select(c => SearchCand(c, new List<string?> { c.S("album"), Matcher.SlugWords(c.S("url")) })).ToList();
            var (verdict, cand) = M.BestCandidate(artist, title, cands);
            var hit = cand?.Data as JsonObject;
            steps.Add($"search {want}s \"{text}\": {cands.Count} result(s)"
                      + (cand != null ? $", {(verdict == "bandcamp" ? "match" : "possible")}: {hit.S("artist")} — {cand.Name}" : ""));
            if (cand == null)
            {
                // Right title, artist not named in the result (a label credits the release to itself):
                // open the closest few and check the artist against the page and the album's address.
                var close = cands.Where(c => M.Score(artist, title, c).T >= 0.88 && !verified.Contains(((JsonObject)c.Data!).T("url"))).Take(3).ToList();
                foreach (var c in close)
                {
                    var co = (JsonObject)c.Data!;
                    verified.Add(co.T("url"));
                    var rel = await SafeReleaseAsync(net, co.T("url"), ct);
                    var (v, name) = rel != null ? MatchInRelease(artist, title, rel) : ("", "");
                    if (v == "bandcamp")
                    {
                        steps.Add($"opened \"{co.S("name")}\" by {co.S("artist")}: the release is \"{(rel.S("release") is { Length: > 0 } rr ? rr : rel.S("title"))}\", artist confirmed");
                        return ReleaseResult(rel!, v, $"{co.S("artist")} — {name}", "Bandcamp search");
                    }
                }
                continue;
            }
            var outp = new JsonObject
            {
                ["status"] = verdict, ["site"] = "Bandcamp", ["url"] = hit.T("url"),
                ["release"] = hit.S("album") is { Length: > 0 } al ? al : kind == "a" ? cand.Name : "",
                ["found"] = string.Join(" — ", new[] { hit.S("artist"), cand.Name }.Where(x => !string.IsNullOrEmpty(x))),
                ["year"] = YearIn(hit.S("released")), ["note"] = "Bandcamp search",
            };
            try     // the page itself gives the album a track is on, and the release date
            {
                var rel = await GetReleaseAsync(net, hit.T("url"), ct: ct);
                if (rel != null)
                {
                    outp["url"] = rel.S("release_url") is { Length: > 0 } ru ? ru : rel.S("url") is { Length: > 0 } u ? u : outp.T("url");
                    outp["release"] = rel.S("release") is { Length: > 0 } rr ? rr : outp.T("release");
                    outp["year"] = rel.S("year") is { Length: > 0 } y ? y : outp.T("year");
                    if (rel.B("placeholder"))                // nothing to buy there: keep looking
                    {
                        outp["note"] = outp.T("note") + "; nothing to buy on this page";
                        if (verdict == "bandcamp") { verdict = "bandcamp_check"; outp["status"] = verdict; }
                    }
                }
            }
            catch (Blocked) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { }
            if (verdict == "bandcamp") return outp;
            check ??= outp;
        }
        if (check != null) return check;
        // Nothing by title. Search for the artist alone and open the releases that name them.
        var names = new List<string> { artist }.Concat(M.ArtistParts(artist).Skip(1).Take(2)).Concat(credits.Take(1));
        foreach (var who in names.Where(n => n.Length > 0).Distinct())
        {
            var found = (await BandcampSearchAsync(net, who, "a", ct)).Where(c => !verified.Contains(c.T("url"))).ToList();
            var naming = found.Where(c => (c.T("type") == "album" || c.T("type") == "")
                && M.Score(who, c.T("name"), SearchCand(c, new List<string?> { c.S("name"), Matcher.SlugWords(c.S("url")) })).A >= 0.85).ToList();
            steps.Add($"search releases naming \"{who}\": {naming.Count} of {found.Count} result(s)");
            foreach (var c in naming.Take(8))
            {
                verified.Add(c.T("url"));
                var rel = await SafeReleaseAsync(net, c.T("url"), ct);
                var (v, name) = rel != null ? MatchInRelease(artist, title, rel) : ("", "");
                if (v == "bandcamp")
                {
                    steps.Add($"opened \"{c.S("name")}\": \"{name}\" is on it");
                    return ReleaseResult(rel!, v, $"{c.S("artist")} — {name}", "Bandcamp search");
                }
            }
        }
        return null;
    }

    public async Task<JsonObject?> SafeReleaseAsync(Net net, string url, CancellationToken ct = default)
    {
        try { return await GetReleaseAsync(net, url, ct: ct); }
        catch (Blocked) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>How Bandcamp turns a title into the last part of an address.</summary>
    public static string Slugify(string text) => Regex.Replace(Names.Fold(text), "[^a-z0-9]+", "-").Trim('-');

    /// <summary>Every Bandcamp page in your world: the ones you track and the ones you have bought from.</summary>
    public List<string> KnownHosts()
    {
        var hosts = Db.Query("SELECT DISTINCT url FROM collection_items").Select(r => RootOf((string)r["url"]!)).ToHashSet();
        foreach (var r in Db.Query("SELECT url FROM accounts WHERE source='bandcamp'")) hosts.Add(((string)r["url"]!).TrimEnd('/'));
        return hosts.Where(h => h.StartsWith("http")).OrderBy(h => h, StringComparer.Ordinal).ToList();
    }

    /// <summary>Look on the pages of labels and artists you already know. Returns a result or null.</summary>
    public async Task<JsonObject?> CatalogueStepAsync(Net net, Library lib, string artist, string title, List<string> steps, CancellationToken ct = default)
    {
        if (lib.Catalogue.Count == 0) return null;
        var names = M.ArtistParts(artist).Concat(M.RemixerNames(title)).Where(n => n.Length >= 3).ToList();
        var want = Matcher.Norm(title);
        var first = want.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var hits = new List<(int Rank, double T, CatalogueItem It)>();
        var hosts = new Dictionary<string, int>();
        foreach (var it in lib.Catalogue)
        {
            var naming = names.Any(n => it.Blob.Contains($" {n} "));
            if (naming) hosts[it.Host] = hosts.GetValueOrDefault(it.Host) + 1;
            else if (!(first.Length > 0 && it.Blob.Contains($" {first} ") && Matcher.Distinctive(title))) continue;
            var t = Math.Max(Matcher.Ratio(want, Matcher.Norm(it.Title)), Math.Max(Matcher.Ratio(want, Matcher.Norm(After(it.Title))),
                Matcher.Ratio(want, Matcher.Norm(AfterLast(it.Title)))));
            if (naming || t >= 0.88) hits.Add(((t >= 0.88 ? 1 : 0) + (naming ? 1 : 0), t, it));
        }
        hits = hits.OrderBy(h => -h.Rank).ThenBy(h => -h.T).ToList();
        int budget = 8, opened = 0;
        foreach (var (_, _, it) in hits)
        {
            if (CacheGet("release", it.Url, 180) == null)
            {
                if (budget <= 0) continue;
                budget--;
            }
            var rel = await SafeReleaseAsync(net, it.Url, ct);
            opened++;
            var (v, name) = rel != null ? MatchInRelease(artist, title, rel) : ("", "");
            if (v == "bandcamp")
            {
                steps.Add($"pages you know: \"{name}\" is on \"{rel.S("title")}\" at {it.Host.Replace("https://", "")}");
                return ReleaseResult(rel!, v, $"{(rel.S("artist") is { Length: > 0 } a ? a : it.Label)} — {name}", "On a label page you know");
            }
        }
        // Not listed. On the pages that carry this artist, try the address the track would have.
        foreach (var own in lib.OwnedFor(artist, title))
        {
            var h = RootOf(own.Url);
            hosts[h] = hosts.GetValueOrDefault(h) + 1;
        }
        int tried = 0;
        foreach (var host in hosts.Keys.OrderBy(h => -hosts[h]).Take(4).ToList())
        {
            var slugs = new[] { Slugify($"{artist} {title}"), Slugify($"{artist} {Matcher.BaseTitle(title)}"), Slugify(title), Slugify(Matcher.BaseTitle(title)) };
            foreach (var slug in slugs.Distinct())
            {
                if (slug.Length == 0) continue;
                tried++;
                var rel = await SafeReleaseAsync(net, $"{host}/track/{slug}", ct);
                var (v, name) = rel != null ? MatchInRelease(artist, title, rel) : ("", "");
                if (v == "bandcamp")
                {
                    steps.Add($"pages you know: found at the expected address on {host.Replace("https://", "")} (not in its public list)");
                    return ReleaseResult(rel!, v, $"{rel.S("artist")} — {name}", "On a label page you know; not publicly listed");
                }
            }
        }
        steps.Add($"pages you know: no match ({hits.Count} release(s) naming the artist or title, {opened} opened, " +
                  $"{tried} address(es) tried on {Math.Min(hosts.Count, 4)} page(s))");
        return null;
    }

    /// <summary>What comes after the first " - " (Python's partition), or "".</summary>
    public static string After(string text)
    {
        var i = text.IndexOf(" - ", StringComparison.Ordinal);
        return i >= 0 ? text[(i + 3)..] : "";
    }

    /// <summary>What comes after the last " - " (Python's rpartition), or the whole text.</summary>
    public static string AfterLast(string text)
    {
        var i = text.LastIndexOf(" - ", StringComparison.Ordinal);
        return i >= 0 ? text[(i + 3)..] : text;
    }

    /// <summary>What comes before the first " - ", or "" when there is none.</summary>
    public static string Before(string text)
    {
        var i = text.IndexOf(" - ", StringComparison.Ordinal);
        return i >= 0 ? text[..i] : "";
    }

    // ---------- Bandcamp: the artist's own page ----------

    /// <summary>Bandcamp pages that belong to the artist(s): [(root url, page name)].</summary>
    public async Task<List<(string Url, string Name)>> ArtistPagesAsync(Net net, Library lib, string artist, List<string> steps, bool searchOk, CancellationToken ct = default)
    {
        if (CacheGet("artistpage", Matcher.Norm(artist), 30) is JsonArray saved)
            return saved.OfType<JsonArray>().Select(x => (Html.Str(x[0]) ?? "", Html.Str(x[1]) ?? "")).ToList();
        var pages = new List<(string Url, string Name)>();
        void SetDefault(string url, string name) { if (!pages.Any(p => p.Url == url)) pages.Add((url, name)); }
        var parts = M.ArtistParts(artist);
        var names = parts.Count > 1 ? parts.Skip(1).Take(2).ToList() : parts.Take(1).ToList();
        foreach (var part in names)
        {
            if (searchOk)
                foreach (var c in await BandcampSearchAsync(net, part, "b", ct))
                    if ((c.T("type") is "artist" or "label" or "") && Matcher.NameSim(part, Matcher.Norm(c.S("name"))) >= 0.85)
                        SetDefault(RootOf(c.T("url")), c.T("name"));
            // An obvious address that search didn't offer; kept only if the page's own name matches.
            var guess = $"https://{part.Replace(" ", "")}.bandcamp.com";
            if (!pages.Any(p => p.Url == guess) && Regex.IsMatch(part.Replace(" ", ""), "^[a-z0-9]{2,}$"))
            {
                JsonObject? grid;
                try { grid = await GetGridAsync(net, guess, ct); }
                catch (Blocked) { throw; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { grid = null; }
                if (grid != null && Matcher.NameSim(part, Matcher.Norm(grid.S("name"))) >= 0.85) SetDefault(guess, grid.T("name"));
            }
        }
        var outp = pages.Take(4).ToList();
        steps.Add("artist page: " + (outp.Count > 0 ? string.Join(", ", outp.Select(p => p.Url.Replace("https://", ""))) : "none found"));
        if (searchOk) CachePut("artistpage", Matcher.Norm(artist), new JsonArray(outp.Select(p => (JsonNode?)J.Arr(new[] { p.Url, p.Name })).ToArray()));
        return outp;
    }

    /// <summary>Open the releases on the artist's page(s) and look for the title. Returns a result or null.</summary>
    public async Task<JsonObject?> ScanStepAsync(Net net, string artist, string title, List<(string Url, string Name)> pages, List<string> steps, CancellationToken ct = default)
    {
        int budget = ScanReleases;
        JsonObject? check = null;
        foreach (var (baseUrl, _) in pages)
        {
            var grid = await GetGridAsync(net, baseUrl, ct);
            var items = grid.Objs("items");
            // A release named after the track is opened first; then everything else, in page order.
            var want = Matcher.Norm(title);
            items = items.OrderBy(i => -Math.Max(Matcher.Ratio(want, Matcher.Norm(i.S("title"))), Matcher.Ratio(want, Matcher.Norm(After(i.T("title")))))).ToList();
            int opened = 0;
            foreach (var item in items)
            {
                var wasSaved = CacheGet("release", item.T("url"), 180) != null;
                if (!wasSaved)
                {
                    if (budget <= 0) break;
                    budget--;
                }
                var rel = await SafeReleaseAsync(net, item.T("url"), ct);
                opened++;
                var (v, name) = rel != null ? MatchInRelease(artist, title, rel) : ("", "");
                if (v.Length > 0)
                {
                    steps.Add($"{baseUrl.Replace("https://", "")}: found \"{name}\" on \"{rel.S("title")}\" ({opened} of {items.Count} releases read)");
                    var outp = ReleaseResult(rel!, v, $"{rel.S("artist")} — {name}", "Found on the artist's page");
                    if (v == "bandcamp") return outp;
                    check ??= outp;
                }
            }
            steps.Add($"{baseUrl.Replace("https://", "")}: title not on any of {opened} release(s) read"
                      + (opened < items.Count ? $" ({items.Count - opened} not read)" : ""));
        }
        return check;
    }
}
