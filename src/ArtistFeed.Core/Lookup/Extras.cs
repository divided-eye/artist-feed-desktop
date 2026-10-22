using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArtistFeed.Core;

public sealed partial class LinkFinder
{
    // ---------- YouTube: only for the artists and labels ticked for it ----------

    public const string YtSearch = "https://www.youtube.com/results";
    static readonly Regex YtData = new(@"(?:var ytInitialData|window\[""ytInitialData""\])\s*=\s*(\{.*?\})\s*;\s*</script>", RegexOptions.Singleline);

    /// <summary>Tracked Artists rows ticked for YouTube: [(id, every tidied name they go by)].</summary>
    List<(long Id, HashSet<string> Names)> YoutubeRows()
    {
        var allowed = _engine.Sites.YoutubeNames();
        var outp = new List<(long, HashSet<string>)>();
        foreach (var r in Db.Query("SELECT id, name, aliases FROM artists"))
        {
            var names = new[] { r.S("name") ?? "" }.Concat(Matcher.SplitNames(r.S("aliases"))).Select(Matcher.Norm).Where(n => n.Length > 0).ToHashSet();
            if (names.Overlaps(allowed)) outp.Add((r.L("id"), names));
        }
        return outp;
    }

    /// <summary>The tidied names in this row (artist, and whoever made this version) ticked for YouTube.</summary>
    List<string> YoutubeNamesFor(string artist, string title)
    {
        var allowed = _engine.Sites.YoutubeNames();
        var names = M.ArtistParts(artist).Concat(M.RemixCredits(title).SelectMany(M.ArtistParts));
        return names.Distinct().Where(allowed.Contains).ToList();
    }

    /// <summary>Would YouTube be searched for this row?</summary>
    public bool YoutubeWanted(string artist, string title)
    {
        if (!_engine.Sites.LookupSites().Contains("youtube") || title.Length == 0) return false;
        if (YoutubeNamesFor(artist, title).Count > 0) return true;
        var ids = YoutubeRows().Select(r => r.Id).ToList();
        return ids.Count > 0 && Db.Query($"SELECT 1 FROM accounts WHERE source='youtube' AND artist_id IN ({string.Join(",", ids.Select(_ => "?"))}) LIMIT 1",
            ids.Cast<object?>().ToArray()).Count > 0;
    }

    /// <summary>Depth-first, in document order, as the web version walks the page's data.</summary>
    static IEnumerable<JsonObject> Walk(JsonNode? root)
    {
        var stack = new Stack<JsonNode?>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is JsonObject o)
            {
                yield return o;
                foreach (var v in o.Select(kv => kv.Value).Reverse()) stack.Push(v);
            }
            else if (node is JsonArray a)
                foreach (var v in a.Reverse()) stack.Push(v);
        }
    }

    /// <summary>The first of each id, in order (Python's dict trick keeps the last one seen from the end).</summary>
    static List<JsonObject> FirstOfEach(List<JsonObject> found)
    {
        var seen = new HashSet<string>();
        var outp = new List<JsonObject>();
        foreach (var x in found)
            if (seen.Add(x.T("id"))) outp.Add(x);
        return outp;
    }

    /// <summary>[{"id", "title", "channel", "length"}] from a YouTube search results page.</summary>
    public static List<JsonObject> ParseYoutubeSearch(string html)
    {
        var m = YtData.Match(html ?? "");
        if (!m.Success) return new();
        var data = Html.ParseJson(m.Groups[1].Value);
        if (data == null) return new();
        var outp = new List<JsonObject>();
        foreach (var node in Walk(data))
        {
            if (node["videoRenderer"] is not JsonObject v || !v.B("videoId")) continue;
            string Text(string k)
            {
                var runs = v.O(k).Objs("runs");
                var joined = string.Concat(runs.Select(r => r.T("text")));
                return joined.Length > 0 ? joined : v.O(k).T("simpleText");
            }
            var channel = Text("ownerText");
            outp.Add(new JsonObject
            {
                ["id"] = v.T("videoId"), ["title"] = Text("title"), ["channel"] = channel.Length > 0 ? channel : Text("longBylineText"),
                ["length"] = v.O("lengthText").T("simpleText"),
            });
        }
        return FirstOfEach(outp);
    }

    async Task<List<JsonObject>> YoutubeSearchAsync(Net net, string text, CancellationToken ct)
    {
        if (CacheGet("yt_search", text, SearchDays) is JsonArray saved) return saved.OfType<JsonObject>().ToList();
        using var req = new HttpRequestMessage(HttpMethod.Get, Http.Query(YtSearch, new Dictionary<string, string> { ["search_query"] = text, ["sp"] = "EgIQAQ==" }));
        req.Headers.TryAddWithoutValidation("Cookie", YouTube.Cookies);
        using var r = await net.Client.SendAsync(req, ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"YouTube search answered {(int)r.StatusCode}");
        var found = ParseYoutubeSearch(await r.Content.ReadAsStringAsync(ct)).Take(20).ToList();
        CachePut("yt_search", text, J.Arr(found));
        return found;
    }

    static JsonObject Video(string url, string? channel, string name, string? year, string note) => new()
    {
        ["status"] = "youtube", ["site"] = "YouTube", ["url"] = url, ["release"] = "", ["year"] = year ?? "",
        ["found"] = !string.IsNullOrEmpty(channel) ? $"{channel} — {name}" : name, ["note"] = note,
    };

    /// <summary>The track on YouTube: among the uploads of channels you track for artists ticked for YouTube,
    /// then (for an artist ticked for it) a YouTube search. (video result, channel page option)</summary>
    public async Task<(JsonObject? Video, JsonObject? Channel)> YoutubeStepAsync(Net net, string artist, string title, List<string> steps, CancellationToken ct = default)
    {
        if (!_engine.Sites.LookupSites().Contains("youtube") || title.Length == 0) return (null, null);
        var ids = YoutubeRows().Select(r => r.Id).ToList();
        if (ids.Count > 0)
        {
            var cands = Db.Query("SELECT p.title, p.url, p.author, p.published_at, ar.name FROM posts p " +
                                 "JOIN accounts a ON a.id=p.account_id JOIN artists ar ON ar.id=a.artist_id " +
                                 $"WHERE a.source='youtube' AND ar.id IN ({string.Join(",", ids.Select(_ => "?"))})", ids.Cast<object?>().ToArray())
                .Select(p => new Cand
                {
                    Name = p.S("title") ?? "", Artists = new() { p.S("author"), p.S("name") },
                    Data = new JsonObject
                    {
                        ["url"] = p.S("url"), ["channel"] = p.S("author") is { Length: > 0 } a ? a : p.S("name"),
                        ["year"] = (p.S("published_at") ?? "").Length >= 4 ? p.S("published_at")![..4] : p.S("published_at") ?? "",
                    },
                }).ToList();
            var (verdict, hit) = M.BestCandidate(artist, title, cands);
            if (verdict == "bandcamp")
            {
                var d = (JsonObject)hit!.Data!;
                steps.Add($"YouTube: \"{hit.Name}\" on a channel you track");
                return (Video(d.T("url"), d.S("channel"), hit.Name, d.S("year"), "On a YouTube channel you track"), null);
            }
        }
        var names = YoutubeNamesFor(artist, title);
        if (names.Count == 0)
        {
            steps.Add("YouTube: not searched (this artist isn't ticked for YouTube in Tracked Artists)");
            return (null, null);
        }
        var page = TrackedPage("youtube", names);
        var channelOpt = page == null ? null : new JsonObject
        {
            ["status"] = "youtube_channel", ["site"] = "YouTube", ["url"] = page.Value.Url, ["found"] = page.Value.Name, ["release"] = "", ["year"] = "",
            ["note"] = "A YouTube channel you track; the track wasn't found there",
        };
        List<JsonObject> found;
        try { found = await YoutubeSearchAsync(net, $"{artist} {title}".Trim(), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            steps.Add(Cut($"YouTube search unavailable: {e.Message}", 200));
            return (null, channelOpt);
        }
        var vids = found.Select(v => new Cand { Name = v.T("title"), Artists = new() { v.S("channel") }, Data = v }).ToList();
        var (vv, vhit) = M.BestCandidate(artist, title, vids);
        if (vv == "bandcamp")
        {
            var v = (JsonObject)vhit!.Data!;
            steps.Add($"YouTube search: \"{v.S("title")}\" by {v.S("channel")}");
            return (Video($"https://www.youtube.com/watch?v={v.S("id")}", v.S("channel"), v.T("title"), "", "Found by YouTube search"), channelOpt);
        }
        steps.Add($"YouTube search: nothing matching among {found.Count} videos");
        return (null, channelOpt);
    }

    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    // ---------- Beatport and Discogs: Link Finder only, off until switched on ----------

    public const string BeatportSearch = "https://www.beatport.com/search/tracks";
    public double BeatportGap { get; set; } = 1.5;
    public const string DiscogsApi = "https://api.discogs.com";
    public double DiscogsGap { get; set; } = 1.1;
    const int DiscogsReleases = 3;
    static readonly Regex NextData = new(@"<script[^>]*id=""__NEXT_DATA__""[^>]*>(.*?)</script>", RegexOptions.Singleline);
    readonly Dictionary<string, (SemaphoreSlim Lock, DateTime Last)> _polite = new();

    /// <summary>At most one request to this site every `gap` seconds, however many lookups are running.</summary>
    async Task PoliteAsync(string site, double gap, CancellationToken ct)
    {
        SemaphoreSlim gate;
        lock (_polite)
        {
            if (!_polite.TryGetValue(site, out var entry)) _polite[site] = entry = (new SemaphoreSlim(1, 1), DateTime.MinValue);
            gate = entry.Lock;
        }
        await gate.WaitAsync(ct);
        try
        {
            DateTime last;
            lock (_polite) last = _polite[site].Last;
            var wait = last + TimeSpan.FromSeconds(gap) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            lock (_polite) _polite[site] = (gate, DateTime.UtcNow);
        }
        finally { gate.Release(); }
    }

    static List<string> NamesOf(JsonNode? items, params string[] keys)
    {
        var outp = new List<string>();
        foreach (var x in items as JsonArray ?? new JsonArray())
        {
            if (x is JsonObject o)
            {
                var first = keys.Select(k => o.S(k)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
                if (first != null) outp.Add(first);
            }
            else if (Html.AsString(x) is { } s) outp.Add(s);
        }
        return outp;
    }

    /// <summary>[{"id", "name", "mix", "artists", "remixers", "release", "label", "date", "url"}] from a Beatport search page.</summary>
    public static List<JsonObject> ParseBeatportSearch(string html)
    {
        var m = NextData.Match(html ?? "");
        if (!m.Success) return new();
        var data = Html.ParseJson(m.Groups[1].Value);
        if (data == null) return new();
        var outp = new List<JsonObject>();
        foreach (var node in Walk(data))
        {
            var name = node.S("track_name") is { Length: > 0 } tn ? tn : node.ContainsKey("mix_name") && node.B("name") ? node.S("name") : null;
            var tid = node.S("track_id") is { Length: > 0 } ti ? ti : node.B("id") ? node.S("id") : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(tid) || node["artists"] is not JsonArray artists) continue;
            var rel = node["release"] as JsonObject ?? new JsonObject();
            var lab = node["label"] as JsonObject ?? rel["label"] as JsonObject ?? new JsonObject();
            var slug = node.S("slug") is { Length: > 0 } sl ? sl : node.S("track_slug") is { Length: > 0 } ts ? ts : Slugify(name) is { Length: > 0 } sg ? sg : "track";
            var date = node.S("publish_date") is { Length: > 0 } pd ? pd : node.S("new_release_date") is { Length: > 0 } nd ? nd : node.T("release_date");
            outp.Add(new JsonObject
            {
                ["id"] = tid, ["name"] = name, ["mix"] = node.S("mix_name") is { Length: > 0 } mx ? mx : node.T("mix"),
                ["artists"] = J.Arr(NamesOf(artists, "artist_name", "name")),
                ["remixers"] = J.Arr(NamesOf(node["remixers"], "artist_name", "remixer_name", "name")),
                ["release"] = rel.S("release_name") is { Length: > 0 } rn ? rn : rel.T("name"),
                ["label"] = lab.S("label_name") is { Length: > 0 } ln ? ln : lab.T("name"),
                ["date"] = date.Length > 10 ? date[..10] : date, ["url"] = $"https://www.beatport.com/track/{slug}/{tid}",
            });
        }
        return FirstOfEach(outp);
    }

    public static string BeatportTitle(JsonObject t)
    {
        var mix = t.T("mix").Trim();
        return mix.Length == 0 || mix.ToLowerInvariant() is "original mix" or "original" ? t.T("name") : $"{t.T("name")} ({mix})";
    }

    async Task<List<JsonObject>> BeatportSearchAsync(Net net, string text, CancellationToken ct)
    {
        if (CacheGet("bp_search", text, SearchDays) is JsonArray saved) return saved.OfType<JsonObject>().ToList();
        await PoliteAsync("beatport", BeatportGap, ct);
        using var r = await net.Client.GetAsync(Http.Query(BeatportSearch, new Dictionary<string, string> { ["q"] = text, ["per_page"] = "25" }), ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Beatport answered {(int)r.StatusCode}");
        var found = ParseBeatportSearch(await r.Content.ReadAsStringAsync(ct)).Take(25).ToList();
        CachePut("bp_search", text, J.Arr(found));
        return found;
    }

    /// <summary>The track on Beatport: one search, the best match taken. Returns a result or null.</summary>
    public async Task<JsonObject?> BeatportStepAsync(Net net, string artist, string title, List<string> steps, CancellationToken ct = default)
    {
        if (!_engine.Sites.LookupSites().Contains("beatport") || title.Length == 0) return null;
        List<JsonObject> found;
        try { found = await BeatportSearchAsync(net, $"{artist} {title}".Trim(), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            steps.Add(Cut($"Beatport search unavailable: {e.Message}", 200));
            return null;
        }
        var cands = found.Select(t => new Cand
        {
            Name = BeatportTitle(t), Artists = t.Strs("artists").Concat(t.Strs("remixers")).Cast<string?>().ToList(),
            Context = new() { t.S("release") }, Data = t,
        }).ToList();
        var (verdict, hit) = M.BestCandidate(artist, title, cands);
        if (verdict.Length == 0)
        {
            steps.Add($"Beatport: nothing matching among {found.Count} tracks");
            return null;
        }
        var tr = (JsonObject)hit!.Data!;
        var by = string.Join(", ", tr.Strs("artists"));
        steps.Add($"Beatport: \"{hit.Name}\" by {by}" + (verdict == "bandcamp" ? "" : " (partial match)"));
        var date = tr.T("date");
        return new JsonObject
        {
            ["status"] = verdict == "bandcamp" ? "beatport" : "beatport_check", ["site"] = "Beatport", ["url"] = tr.S("url"),
            ["release"] = tr.S("release"), ["year"] = date.Length >= 4 ? date[..4] : date, ["found"] = $"{by} — {hit.Name}",
            ["note"] = "On Beatport" + (tr.B("label") ? $" ({tr.S("label")})" : ""),
        };
    }

    /// <summary>A Discogs personal access token: Discogs only lets signed-in apps search its database.</summary>
    public string DiscogsToken() => (Db.KvGet("discogs_token") is { Length: > 0 } t ? t : Environment.GetEnvironmentVariable("DISCOGS_TOKEN") ?? "").Trim();

    public void SetDiscogsToken(string token) => Db.KvSet("discogs_token", (token ?? "").Trim());

    async Task<JsonObject> DiscogsAsync(Net net, string path, Dictionary<string, string>? query, CancellationToken ct)
    {
        await PoliteAsync("discogs", DiscogsGap, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, Http.Query(DiscogsApi + path, query ?? new()));
        req.Headers.TryAddWithoutValidation("Authorization", $"Discogs token={DiscogsToken()}");
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.UserAgent.Clear();
        req.Headers.TryAddWithoutValidation("User-Agent", "ArtistFeed/1.0 +https://github.com/divided-eye/artist-feed");
        using var r = await net.Client.SendAsync(req, ct);
        if ((int)r.StatusCode == 401) throw new InvalidOperationException("Discogs refused the access token (check it in Settings)");
        if ((int)r.StatusCode == 429) throw new InvalidOperationException("Discogs asked the app to slow down");
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Discogs answered {(int)r.StatusCode}");
        return J.Parse(await r.Content.ReadAsStringAsync(ct)) ?? new JsonObject();
    }

    async Task<JsonObject> DiscogsReleaseAsync(Net net, string rid, CancellationToken ct)
    {
        if (CacheGet("dg_release2", rid, 30) is JsonObject saved) return saved;
        var id = long.Parse(rid, System.Globalization.CultureInfo.InvariantCulture);
        var data = await DiscogsAsync(net, $"/releases/{id}", null, ct);
        var year = data.B("year") ? data.T("year") : "";
        var keep = new JsonObject
        {
            ["title"] = data.T("title"), ["year"] = year.Length > 4 ? year[..4] : year,
            ["uri"] = data.S("uri") is { Length: > 0 } u ? u : $"https://www.discogs.com/release/{rid}",
            // The main release page (Discogs' "master"), which gathers every pressing of it.
            ["master"] = data.B("master_id") ? $"https://www.discogs.com/master/{(long)(data.N("master_id") ?? 0)}" : "",
            ["artists"] = J.Arr(NamesOf(data["artists"], "name")),
            ["formats"] = J.Arr(data.Objs("formats").Where(f => f.B("name")).Select(f => f.T("name"))),
            ["tracks"] = new JsonArray(data.Objs("tracklist").Where(t => (t.S("type_") ?? "track") == "track")
                .Select(t => (JsonNode?)new JsonObject
                {
                    ["title"] = t.T("title"),
                    ["artists"] = J.Arr(NamesOf(t["artists"], "name").Concat(NamesOf(t["extraartists"], "name"))),
                }).ToArray()),
        };
        CachePut("dg_release2", rid, keep);
        return keep;
    }

    /// <summary>The track on a release listed on Discogs (often vinyl-only releases found nowhere else).</summary>
    public async Task<JsonObject?> DiscogsStepAsync(Net net, string artist, string title, List<string> steps, CancellationToken ct = default)
    {
        if (!_engine.Sites.LookupSites().Contains("discogs") || title.Length == 0) return null;
        if (DiscogsToken().Length == 0)
        {
            steps.Add("Discogs: not searched (add a Discogs access token in Settings)");
            return null;
        }
        var track = Matcher.BaseTitle(title) is { Length: > 0 } bt ? bt : title;
        List<JsonObject> found, mine;
        (string Verdict, Cand Hit)? best = null;
        try
        {
            var key = $"{Matcher.Norm(artist)}|{Matcher.Norm(track)}";
            if (CacheGet("dg_search", key, SearchDays) is JsonArray saved) found = saved.OfType<JsonObject>().ToList();
            else
            {
                var data = await DiscogsAsync(net, "/database/search", new() { ["type"] = "release", ["artist"] = artist, ["track"] = track, ["per_page"] = "15" }, ct);
                found = data.Objs("results").Where(x => x.B("id")).Select(x => new JsonObject { ["id"] = x["id"]!.DeepClone(), ["title"] = x.T("title") }).ToList();
                CachePut("dg_search", key, J.Arr(found));
            }
            var wanted = M.ArtistParts(artist);
            mine = found.Where(x => wanted.Count == 0 || wanted.SelectMany(w => M.ArtistParts(x.T("title").Split(" - ")[0]).Select(h => Matcher.NameSim(w, h))).DefaultIfEmpty(0).Max() >= 0.85).ToList();
            foreach (var x in (mine.Count > 0 ? mine : found).Take(DiscogsReleases))
            {
                var rel = await DiscogsReleaseAsync(net, x.T("id"), ct);
                var cands = rel.Objs("tracks").Select(t => new Cand
                {
                    Name = t.T("title"), Artists = t.Strs("artists").Concat(rel.Strs("artists")).Cast<string?>().ToList(),
                    Context = new() { rel.S("title") }, Data = (t, rel),
                }).ToList();
                var (verdict, hit) = M.BestCandidate(artist, title, cands);
                if (verdict.Length > 0 && (best == null || (verdict == "bandcamp" && best.Value.Verdict != "bandcamp"))) best = (verdict, hit!);
                if (verdict == "bandcamp") break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            steps.Add(Cut($"Discogs search unavailable: {e.Message}", 200));
            return null;
        }
        if (best == null)
        {
            steps.Add($"Discogs: nothing matching on {Math.Min((mine.Count > 0 ? mine : found).Count, DiscogsReleases)} of {found.Count} releases found");
            return null;
        }
        var (v, h) = best.Value;
        var (trk, release) = ((JsonObject, JsonObject))h.Data!;
        var formats = string.Join(", ", release.Strs("formats").Distinct());
        steps.Add($"Discogs: \"{trk.S("title")}\" on \"{release.S("title")}\"" + (formats.Length > 0 ? $" ({formats})" : ""));
        var arts = string.Join(", ", release.Strs("artists"));
        return new JsonObject
        {
            ["status"] = "discogs", ["site"] = "Discogs", ["url"] = release.S("master") is { Length: > 0 } ms ? ms : release.S("uri"),
            ["release"] = release.S("title"), ["year"] = release.S("year"),
            ["found"] = $"{(arts.Length > 0 ? arts : artist)} — {trk.S("title")}",
            ["note"] = "On Discogs" + (formats.Length > 0 ? $" ({formats})" : "") + (v == "bandcamp" ? "" : "; check match"),
        };
    }

    // ---------- fallbacks: the artist's page when the track itself isn't found ----------

    /// <summary>(url, name) of a page you track on this site for an artist with one of these tidied names.</summary>
    public (string Url, string Name)? TrackedPage(string source, IEnumerable<string> names)
    {
        var set = names.ToHashSet();
        foreach (var acc in Db.Query("SELECT a.url, a.display_name, ar.name FROM accounts a JOIN artists ar ON ar.id=a.artist_id WHERE a.source=? ORDER BY a.id", source))
            if (M.ArtistParts(acc.S("name")).Any(set.Contains))
                return (acc.S("url") ?? "", acc.S("display_name") is { Length: > 0 } d ? d : acc.S("name") ?? "");
        return null;
    }

    public static readonly string[] FallbackStatuses = { "soundcloud_track", "bandcamp_artist", "soundcloud", "soundcloud_search", "youtube",
                                                         "youtube_channel", "beatport_check", "discogs", "none" };
    public static readonly Dictionary<string, string> OptionSite = new()
    {
        ["track"] = "soundcloud", ["soundcloud"] = "soundcloud", ["search"] = "soundcloud", ["bandcamp"] = "bandcamp",
        ["youtube"] = "youtube", ["youtube_channel"] = "youtube", ["beatport"] = "beatport", ["discogs"] = "discogs",
    };
    public static readonly string[] TrackOptions = { "track", "beatport", "youtube" };    // options that are the track itself

    /// <summary>Which artist link to give when the track itself isn't found: "bandcamp" or "soundcloud".</summary>
    public string FallbackPref()
    {
        var value = (Db.KvGet("lookup_fallback") is { Length: > 0 } v ? v : Environment.GetEnvironmentVariable("LOOKUP_FALLBACK") ?? "soundcloud").Trim().ToLowerInvariant();
        return value is "bandcamp" or "soundcloud" ? value : "soundcloud";
    }

    /// <summary>The track on SoundCloud if it was found there; else the preferred artist link, else the other one,
    /// else a SoundCloud search.</summary>
    public JsonObject PickFallback(JsonObject options)
    {
        var order = FallbackPref() == "soundcloud" ? new[] { "soundcloud", "bandcamp" } : new[] { "bandcamp", "soundcloud" };
        if (options.B("remix") && options.B("soundcloud")) order = new[] { "soundcloud", "bandcamp" };
        var allowed = _engine.Sites.LookupSites();
        foreach (var key in new[] { "track", "beatport", "youtube", "discogs" }.Concat(order).Concat(new[] { "youtube_channel", "search" }))
            if (options.B(key) && allowed.Contains(OptionSite[key]))
                return options.O(key)!.Copy();
        return new JsonObject
        {
            ["status"] = "none",
            ["note"] = OptionSite.Keys.Any(options.B) ? "Not found on the sites Link Finder searches" : "No artist name to search for",
        };
    }

    static readonly string[] Blanks = { "site", "url", "found", "note", "release", "year" };

    static void Merge(JsonObject into, JsonObject from)
    {
        foreach (var (k, v) in from) into[k] = v?.DeepClone();
    }

    /// <summary>A saved not-found answer, re-pointed to the currently preferred artist link. Null when it was saved
    /// before both links were kept and needs searching again.</summary>
    public JsonObject? WithCurrentFallback(JsonObject saved)
    {
        if (!FallbackStatuses.Contains(saved.S("status"))) return saved;
        if (saved.B("options"))
        {
            var outp = saved.Copy();
            foreach (var k in Blanks) outp[k] = "";
            Merge(outp, PickFallback(saved.O("options")!));
            return outp;
        }
        if (FallbackPref() == "soundcloud" && saved.S("status") == "bandcamp_artist") return null;    // no SoundCloud link looked for then
        return saved;
    }

    public void SetFallback(string value)
    {
        if (value is not ("bandcamp" or "soundcloud")) throw new ArgumentException("Choose \"bandcamp\" or \"soundcloud\".");
        Db.KvSet("lookup_fallback", value);
        SitesChanged();
    }

    /// <summary>Lookups still on screen follow a new choice of sites or fallback.</summary>
    public void SitesChanged()
    {
        foreach (var job in Jobs.Values)
            lock (job)
                foreach (var row in job.Rows)
                    if (row.B("options") && FallbackStatuses.Contains(row.S("status")))
                    {
                        foreach (var k in Blanks) row[k] = "";
                        Merge(row, PickFallback(row.O("options")!));
                    }
    }

    /// <summary>Answer from what is already saved (feed, purchases, pages read earlier), with no request to any
    /// site. Null when the track needs searching for.</summary>
    public JsonObject? FindLocal(Library lib, string artist, string title)
    {
        JsonObject OwnedResult(OwnedItem item, string name, string how)
        {
            var rel = CacheGet("release", item.Url, 3650) as JsonObject ?? new JsonObject();
            return new JsonObject
            {
                ["status"] = "bandcamp", ["site"] = "Bandcamp", ["url"] = rel.S("release_url") is { Length: > 0 } ru ? ru : item.Url,
                ["release"] = rel.S("release") is { Length: > 0 } r ? r : item.Title, ["year"] = rel.T("year"),
                ["note"] = "In your purchases", ["steps"] = J.Arr(new[] { how }), ["found"] = $"{item.Band} — {name}",
            };
        }
        var (verdict, hit) = M.BestCandidate(artist, title, lib.Feed.GetValueOrDefault(Matcher.Norm(title)) ?? new());
        if (verdict == "bandcamp")
        {
            var h = (JsonObject)hit!.Data!;
            var pub = h.T("published_at");
            return new JsonObject
            {
                ["status"] = "bandcamp", ["site"] = "Bandcamp", ["url"] = h.S("url"), ["release"] = h.S("release"),
                ["year"] = pub.Length >= 4 ? pub[..4] : pub, ["note"] = "In your feed", ["found"] = $"{h.S("artist")} — {hit.Name}",
                ["steps"] = J.Arr(new[] { "feed: already collected from a page you track" }),
            };
        }
        if (title.Length == 0) return null;
        var (v2, cand) = M.BestCandidate(artist, title, lib.OwnedTracks.GetValueOrDefault(Matcher.Norm(title)) ?? new());
        if (v2 == "bandcamp")
        {
            var item = (OwnedItem)cand!.Data!;
            return OwnedResult(item, cand.Name, $"purchases: track \"{cand.Name}\" on \"{item.Title}\"");
        }
        var owned = lib.OwnedFor(artist, title);
        foreach (var item in owned)
            if (!M.Versioned(title) && Math.Max(Matcher.TitleSim(Matcher.Norm(title), Matcher.Norm(item.Title)),
                                                Matcher.TitleSim(Matcher.Norm(title), Matcher.Norm(After(item.Title)))) >= 0.88)
                return OwnedResult(item, item.Title, $"purchases: you own \"{item.Title}\" by {item.Band}");
        var known = PageIndex().GetValueOrDefault(Matcher.Norm(title)) ?? new();
        var (v3, hit3) = M.BestCandidate(artist, title, known.Where(c => lib.Owns((JsonObject)c.Data!)));
        var isMine = v3 == "bandcamp";
        if (!isMine) (v3, hit3) = M.BestCandidate(artist, title, known);
        if (v3 == "bandcamp" && (isMine || owned.Count == 0))
        {
            var rel = (JsonObject)hit3!.Data!;
            var outp = ReleaseResult(rel, v3, $"{(rel.S("artist") is { Length: > 0 } a ? a : artist)} — {hit3.Name}", isMine ? "In your purchases" : "On a page read earlier");
            outp["steps"] = J.Arr(new[] { $"pages read earlier: \"{hit3.Name}\" on \"{rel.S("title")}\"" });
            return outp;
        }
        return null;
    }

    static readonly string[] ExtraSites = { "youtube", "beatport", "discogs" };      // looked at on their own when ticked later

    /// <summary>Sites Link Finder searches now that weren't searched when this not-found answer was saved.</summary>
    public HashSet<string> NewlyOn(JsonObject saved, string artist, string title)
    {
        if (!FallbackStatuses.Contains(saved.S("status"))) return new();
        var then = saved.A("sites") != null ? saved.Strs("sites") : new List<string> { "bandcamp", "soundcloud" };
        var now = _engine.Sites.LookupSites().Except(then).ToHashSet();
        if (now.Contains("youtube") && !YoutubeWanted(artist, title)) now.Remove("youtube");
        if (now.Contains("discogs") && DiscogsToken().Length == 0) now.Remove("discogs");
        return now;
    }

    /// <summary>A saved not-found answer, with the sites ticked since (YouTube, Beatport, Discogs) looked at too.</summary>
    public async Task<JsonObject> AddExtrasAsync(Net net, JsonObject saved, string artist, string title, ICollection<string> extra, CancellationToken ct = default)
    {
        var steps = saved.Strs("steps").Where(s => s != "(saved result from an earlier lookup)").ToList();
        var options = saved.O("options")?.Copy() ?? new JsonObject();
        var then = saved.A("sites") != null ? saved.Strs("sites") : new List<string> { "bandcamp", "soundcloud" };
        var searched = J.Arr(then.Union(extra).Distinct().OrderBy(x => x, StringComparer.Ordinal));
        if (extra.Contains("beatport"))
        {
            var found = await BeatportStepAsync(net, artist, title, steps, ct);
            if (found != null && found.S("status") == "beatport")
                return found.With(("steps", J.Arr(steps)), ("sites", searched), ("options", options));
            if (found != null) options["beatport"] = found;
        }
        if (extra.Contains("youtube") && !options.B("track"))
        {
            var (video, channel) = await YoutubeStepAsync(net, artist, title, steps, ct);
            if (video != null) options["youtube"] = video;
            if (channel != null) options["youtube_channel"] = channel;
        }
        if (extra.Contains("discogs") && !TrackOptions.Any(options.B))
        {
            var found = await DiscogsStepAsync(net, artist, title, steps, ct);
            if (found != null) options["discogs"] = found;
        }
        var outp = saved.Copy();
        foreach (var k in Blanks) outp[k] = "";
        Merge(outp, PickFallback(options));
        outp["options"] = options.Copy();
        outp["steps"] = J.Arr(steps);
        outp["sites"] = searched;
        return outp;
    }
}
