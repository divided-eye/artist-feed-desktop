using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArtistFeed.Core;

/// <summary>One lookup: its rows (each a JSON object, as the web version keeps them) and how far it has got.</summary>
public sealed class LookupJob
{
    public string Id { get; init; } = "";
    public int Total { get; init; }
    public int Done;
    public volatile bool Finished;
    public string Warning = "";
    public string Phase = "";
    public int? Current;
    public List<JsonObject> Rows { get; } = new();
    public HashSet<int> Skip { get; } = new();
    internal readonly Dictionary<int, (JsonObject Saved, HashSet<string> Extra)> Extend = new();
    /// <summary>Raised (on a worker thread) whenever a row or the phase changes.</summary>
    public event Action? Changed;
    internal void Touch() => Changed?.Invoke();
}

public sealed partial class LinkFinder
{
    public Dictionary<string, LookupJob> Jobs { get; } = new();

    bool JobsRunning()
    {
        lock (Jobs) return Jobs.Values.Any(j => !j.Finished);
    }

    // ---------- keys under which answers are saved ----------

    /// <summary>The tidied title, plus which version it is.</summary>
    public string TitleKey(string title)
    {
        var b = Matcher.Norm(title);
        var marks = new List<string>();
        var credits = M.RemixCredits(title);
        if (credits.Count > 0) marks.Add(Matcher.Norm(credits[0]));
        var vip = M.VipWords(title);
        if (vip != null)
        {
            var stripped = Regex.Replace(Regex.Replace(b, @"\bvip\b", " "), @"\s+", " ").Trim();
            b = stripped.Length > 0 ? stripped : b;
            marks.Add(string.Join(" ", new[] { "vip" }.Concat(vip)));
        }
        return b + (marks.Count > 0 ? "#" + string.Join("+", marks.Where(m => m.Length > 0)) : "");
    }

    public string TrackKey(string artist, string title) => $"{Matcher.Norm(artist)}|{TitleKey(title)}";
    internal string CacheKey(string artist, string title) => $"{CacheVersion}|{TrackKey(artist, title)}";

    JsonObject? Cached(string key)
    {
        var rows = Db.Query("SELECT result FROM lookup_cache WHERE key=?", key);
        return rows.Count > 0 ? J.Parse(rows[0].S("result")) ?? new JsonObject() : null;
    }

    internal void Remember(string key, JsonObject result, string? artist = null, string? title = null)
    {
        var r = result.With(("engine", EngineVersion));
        if (artist != null || title != null)                  // as typed, for the Saved results view
        {
            r["artist"] = artist ?? "";
            r["title"] = title ?? "";
        }
        Db.Execute("INSERT INTO lookup_cache(key, result, at) VALUES(?,?,?) " +
                   "ON CONFLICT(key) DO UPDATE SET result=excluded.result, at=excluded.at", key, J.Dump(r), Db.Now());
    }

    internal JsonObject? SavedFor(string artist, string title)
    {
        var found = Cached(CacheKey(artist, title));
        if (found == null && Matcher.OldSpelling(artist + title) != artist + title)
            found = Cached(CacheKey(Matcher.OldSpelling(artist), Matcher.OldSpelling(title)));
        return found;
    }

    /// <summary>Answers saved before versions had keys of their own: moved to the key their track has now.</summary>
    public int RekeyOldAnswers()
    {
        int moved = 0;
        foreach (var r in Db.Query("SELECT key, result FROM lookup_cache"))
        {
            var full = r.S("key") ?? "";
            var bar = full.IndexOf('|');
            var kind = bar >= 0 ? full[..bar] : full;
            var rest = bar >= 0 ? full[(bar + 1)..] : "";
            if ((kind != CacheVersion && kind != "manual") || rest.Contains('#')) continue;
            if (J.Parse(r.S("result")) is not JsonObject saved || saved.B("title")) continue;
            var bar2 = rest.IndexOf('|');
            var artist = bar2 >= 0 ? rest[..bar2] : rest;
            var title = bar2 >= 0 ? rest[(bar2 + 1)..] : "";
            var fresh = TitleKey(title);
            var found = AfterLastDash(saved.T("found"));
            if (!fresh.Contains('#') && found.Length > 0 && M.RemixCredits(found).Count > 0 &&
                (Matcher.Norm(found) == title || Matcher.Norm(found).EndsWith(" " + title)))
                fresh = title + "#" + Matcher.Norm(M.RemixCredits(found)[0]);
            var target = $"{kind}|{artist}|{fresh}";
            if (target == full) continue;
            if (Cached(target) == null) Db.Execute("UPDATE lookup_cache SET key=? WHERE key=?", target, full);
            else Db.Execute("DELETE FROM lookup_cache WHERE key=?", full);
            moved++;
        }
        return moved;
    }

    static string AfterLastDash(string found)
    {
        var i = found.LastIndexOf(" — ", StringComparison.Ordinal);
        return i >= 0 ? found[(i + 3)..] : found;
    }

    internal string ManualKey(string artist, string title) => $"manual|{TrackKey(artist, title)}";

    /// <summary>The link you set for this track.</summary>
    internal JsonObject? ManualFor(string artist, string title)
    {
        var found = Cached(ManualKey(artist, title));
        if (found == null && Matcher.OldSpelling(artist + title) != artist + title)
            found = Cached(ManualKey(Matcher.OldSpelling(artist), Matcher.OldSpelling(title)));
        if (found == null && TitleKey(title).Contains('#'))
        {
            var old = Cached($"manual|{Matcher.Norm(artist)}|{Matcher.Norm(title)}");
            if (old != null && !old.ContainsKey("title")) found = old;
        }
        return found;
    }

    public static string SiteOf(string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority.ToLowerInvariant() : "";
        return host.Contains("bandcamp") ? "Bandcamp" : host.Contains("soundcloud") ? "SoundCloud" : host.Contains("beatport") ? "Beatport"
            : host.Contains("discogs") ? "Discogs" : host.Contains("youtube") || host.Contains("youtu.be") ? "YouTube"
            : host.StartsWith("www.") ? host[4..] : host;
    }

    /// <summary>Save your own link for a track. It is used from then on, whatever a search would find.</summary>
    public async Task<JsonObject> SetManualAsync(string artist, string title, string url, string year = "", string release = "", string key = "")
    {
        var result = new JsonObject
        {
            ["status"] = "manual", ["manual"] = true, ["site"] = SiteOf(url), ["url"] = url, ["year"] = year, ["release"] = release,
            ["found"] = "", ["note"] = "", ["steps"] = J.Arr(new[] { "link set by you" }),
        };
        if (url.Contains("bandcamp") && !(year.Length > 0 && release.Length > 0))
        {
            try        // fill in the year and release name from the page, if that is quick
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var client = _engine.MakeClient();
                var rel = await GetReleaseAsync(MakeNet(client), url, ct: cts.Token);
                if (rel != null)
                {
                    result["year"] = year.Length > 0 ? year : rel.T("year");
                    result["release"] = release.Length > 0 ? release : rel.S("release") is { Length: > 0 } r ? r : rel.T("title");
                }
            }
            catch (Exception) { }
        }
        Remember(key.Length > 0 ? $"manual|{key}" : ManualKey(artist, title), result, artist, title);
        if (ProfileUrl(url).Length > 0)                  // your link also settles whose profile that name means
            LearnProfile(WantedNames(artist, title).Take(1), url, 100);
        return result;
    }

    /// <summary>Save (or, with an empty address, remove) your own link for a track, and show it in lookups on screen.</summary>
    public async Task<JsonObject> SetLinkAsync(string artist, string title, string url, string year = "", string release = "", string key = "")
    {
        if ((artist ?? "").Trim().Length == 0 && (title ?? "").Trim().Length == 0) throw new ArgumentException("Which track is this for?");
        artist ??= "";
        title ??= "";
        url = (url ?? "").Trim();
        key = (key ?? "").Trim();
        JsonObject result;
        if (url.Length == 0)
        {
            ClearManual(artist, title, key);
            result = new JsonObject { ["status"] = "none", ["note"] = "Your link was removed; run the lookup to search again" };
        }
        else
        {
            if (!url.Contains("://")) url = "https://" + url;
            if (!Regex.IsMatch(url, @"^https?://[^\s/]+\.\S+$")) throw new ArgumentException("That doesn't look like a web address.");
            year = (year ?? "").Trim();
            if (year.Length > 0 && !Regex.IsMatch(year, "^(19|20)[0-9]{2}$")) throw new ArgumentException("Year should be four digits, or left empty.");
            result = await SetManualAsync(artist, title, url, year, (release ?? "").Trim(), key);
        }
        ApplyToJobs(artist, title, result);
        return result;
    }

    public void ClearManual(string artist, string title, string key = "")
    {
        Db.Execute("DELETE FROM lookup_cache WHERE key=?", key.Length > 0 ? $"manual|{key}" : ManualKey(artist, title));
        var oldKey = $"manual|{Matcher.Norm(artist)}|{Matcher.Norm(title)}";
        var old = Cached(oldKey);
        if (old != null && !old.ContainsKey("title")) Db.Execute("DELETE FROM lookup_cache WHERE key=?", oldKey);
    }

    /// <summary>Update rows for this track in lookups still on screen, so the table and export agree.</summary>
    public void ApplyToJobs(string artist, string title, JsonObject result)
    {
        var key = TrackKey(artist, title);
        List<LookupJob> jobs;
        lock (Jobs) jobs = Jobs.Values.ToList();
        foreach (var job in jobs)
        {
            lock (job)
                foreach (var row in job.Rows)
                {
                    if (TrackKey(row.T("artist"), row.T("title")) != key) continue;
                    if (row.S("status") == "pending" && !job.Finished && row.N("n") is { } n) job.Skip.Add((int)n);   // no need to search for it
                    foreach (var k in new[] { "site", "url", "year", "release", "found", "note" }) row[k] = "";
                    row["steps"] = new JsonArray();
                    Merge(row, result);
                }
            job.Touch();
        }
    }

    // ---------- "Tune X Flip": a remix typed without brackets ----------

    static readonly Regex BareRemix = new(@"^(.*\S)\s+(remix|rmx|re-?edit|edit|bootleg|flip|refix|rework|mash-?up)\s*$", RegexOptions.IgnoreCase);
    static readonly Regex AnyBracket = new(@"[\(\[]([^\)\]]*)[\)\]]");

    /// <summary>Each reading of a title that may be someone's remix typed without brackets, best first.</summary>
    public List<string> BareRemixReadings(string title)
    {
        var m = BareRemix.Match(title ?? "");
        if (!m.Success || M.RemixCredits(title).Count > 0 || AnyBracket.IsMatch(title!)) return new();
        var words = m.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var kind = m.Groups[2].Value;
        var known = Known().Artists.Keys.ToHashSet();
        foreach (var (k, _) in Profiles()) known.Add(k);
        foreach (var r in Db.Query("SELECT name, aliases FROM artists"))
            foreach (var n in new[] { r.S("name") ?? "" }.Concat(Matcher.SplitNames(r.S("aliases")))) known.Add(Matcher.Norm(n));
        var outp = new List<string>();
        foreach (var n in new[] { 4, 3, 2, 1 })
        {
            if (words.Length <= n) continue;
            var name = string.Join(" ", words[^n..]);
            if (Matcher.Norm(name).Length > 0 && known.Contains(Matcher.Norm(name)))
                outp.Add($"{string.Join(" ", words[..^n])} ({name} {kind})");
        }
        var count = words.Length > 3 && words[^2].ToLowerInvariant() is "&" or "x" or "and" or "+" ? 3 : 1;     // "Kahn & Neek"
        var last = Matcher.Plain(words[^1]);
        if (words.Length > count && last.Length > 0 && !Names.Generic.Contains(last) && !Names.Format.Contains(last))
            outp.Add($"{string.Join(" ", words[..^count])} ({string.Join(" ", words[^count..])} {kind})");
        return outp.Distinct().Take(2).ToList();
    }

    // ---------- one track ----------

    sealed class RunState { public bool BandcampOk = true, SearchOk = true; public string Warning = ""; }

    /// <summary>One track, as typed; and when that finds neither the track nor anything better than the artist's
    /// page, once more as somebody's remix if the title can be read that way.</summary>
    async Task<(JsonObject Result, bool Keep)> FindAsync(Net net, Library lib, string artist, string title, RunState state, CancellationToken ct)
    {
        var (result, keep) = await FindAsTypedAsync(net, lib, artist, title, state, ct);
        if (result.S("status") is "bandcamp_artist" or "soundcloud" or "soundcloud_search" or "none")
            foreach (var reading in BareRemixReadings(title))
            {
                var (other, kept) = await FindAsTypedAsync(net, lib, artist, reading, state, ct);
                var foundIt = other.S("status") is "bandcamp" or "soundcloud_track";
                if (foundIt || (other.S("status") == "soundcloud" && other.O("options").B("remix")))
                {
                    other["steps"] = J.Arr(result.Strs("steps").Append($"nothing for the title as typed; read as \"{reading}\"").Concat(other.Strs("steps")));
                    return (other, keep && kept);
                }
            }
        return (result, keep);
    }

    /// <summary>All the steps for one track. Keep is false when a site refused requests part-way, so the answer
    /// may be incomplete and should not be remembered.</summary>
    async Task<(JsonObject Result, bool Keep)> FindAsTypedAsync(Net net, Library lib, string artist, string title, RunState state, CancellationToken ct)
    {
        var steps = new List<string>();
        bool keep = true;
        JsonObject? check = null;
        var pages = new List<(string Url, string Name)>();
        var makerPages = new List<(string Url, string Name)>();
        var use = _engine.Sites.LookupSites();                 // the sites Link Finder is set to search
        bool bc = use.Contains("bandcamp"), sc = use.Contains("soundcloud");

        (JsonObject, bool) Done(JsonObject result)
        {
            result["steps"] = J.Arr(steps);
            result["sites"] = J.Arr(use);
            return (result, keep);
        }

        // 1. the feed
        var (verdict, hit) = bc ? M.BestCandidate(artist, title, lib.Feed.GetValueOrDefault(Matcher.Norm(title)) ?? new()) : ("", null);
        if (verdict == "bandcamp")
        {
            var h = (JsonObject)hit!.Data!;
            var pub = h.T("published_at");
            steps.Add("feed: already collected from a page you track");
            return Done(new JsonObject
            {
                ["status"] = "bandcamp", ["site"] = "Bandcamp", ["url"] = h.S("url"), ["release"] = h.S("release"),
                ["year"] = pub.Length >= 4 ? pub[..4] : pub, ["note"] = "In your feed", ["found"] = $"{h.S("artist")} — {hit.Name}",
            });
        }
        // 1b. any release page read during an earlier lookup; a release you own first
        var known = bc ? PageIndex().GetValueOrDefault(Matcher.Norm(title)) ?? new() : new();
        var mine = known.Where(c => lib.Owns((JsonObject)c.Data!)).ToList();
        (verdict, hit) = M.BestCandidate(artist, title, mine);
        if (verdict != "bandcamp") (verdict, hit) = M.BestCandidate(artist, title, known);
        JsonObject? earlier = null;
        if (hit != null)
        {
            var rel = (JsonObject)hit.Data!;
            var owned = lib.Owns(rel);
            steps.Add($"pages read earlier: \"{hit.Name}\" on \"{rel.S("title")}\"" + (verdict == "bandcamp" ? "" : " (artist only partly matches)"));
            earlier = ReleaseResult(rel, verdict, $"{(rel.S("artist") is { Length: > 0 } a ? a : artist)} — {hit.Name}",
                                    owned ? "In your purchases" : "On a page read earlier");
            if (verdict == "bandcamp" && (owned || lib.OwnedFor(artist, title).Count == 0)) return Done(earlier);
        }
        if (bc && state.BandcampOk && title.Length > 0)
        {
            try
            {
                // 2. your purchases
                var (result, chk) = await OwnedStepAsync(net, lib, artist, title, steps, ct);
                check = chk;
                if (result != null) return Done(result);
                if (earlier != null && earlier.S("status") == "bandcamp") return Done(earlier);   // the page read earlier stands
                check ??= earlier;
                // 3. Bandcamp search
                try { result = await SearchStepAsync(net, artist, title, steps, ct); }
                catch (Blocked e)
                {
                    state.SearchOk = false;
                    steps.Add($"search unavailable: {e.Message}");
                    result = null;
                }
                if (result != null && result.S("status") == "bandcamp") return Done(result);
                check ??= result;
                // 3b. pages of labels and artists you already know
                result = await CatalogueStepAsync(net, lib, artist, title, steps, ct);
                if (result != null) return Done(result);
                // 4. the artist's own page; for an edit, remix or bootleg its maker's page first
                foreach (var credit in M.RemixCredits(title))
                {
                    makerPages = await ArtistPagesAsync(net, lib, credit, steps, state.SearchOk, ct);
                    if (makerPages.Count > 0) break;
                }
                pages = makerPages.Concat((await ArtistPagesAsync(net, lib, artist, steps, state.SearchOk, ct)).Where(p => !makerPages.Contains(p))).ToList();
                result = pages.Count > 0 ? await ScanStepAsync(net, artist, title, pages, steps, ct) : null;
                if (result != null && result.S("status") == "bandcamp") return Done(result);
                check ??= result;
            }
            catch (Blocked e)
            {
                state.BandcampOk = false;
                keep = false;
                steps.Add($"stopped asking Bandcamp: {e.Message}");
                state.Warning = $"{e.Message}, even after waiting. Rows after that were only checked against your " +
                                "feed, your saved purchases and SoundCloud; run the lookup again later.";
            }
            if (!state.SearchOk) keep = false;
        }
        else if (bc && title.Length > 0)
        {
            keep = false;
            var (v, cand) = M.BestCandidate(artist, title, lib.OwnedTracks.GetValueOrDefault(Matcher.Norm(title)) ?? new());
            if (cand != null)
            {
                var item = (OwnedItem)cand.Data!;
                check = new JsonObject
                {
                    ["status"] = v, ["site"] = "Bandcamp", ["url"] = item.Url, ["year"] = "", ["release"] = item.Title,
                    ["note"] = "In your purchases", ["found"] = $"{item.Band} — {cand.Name}",
                };
            }
        }
        // 5. Beatport (when ticked): a track found there is a place to buy it, so it comes before SoundCloud
        var beatport = await BeatportStepAsync(net, artist, title, steps, ct);
        if (beatport != null && beatport.S("status") == "beatport") return Done(beatport);
        // 6. fallbacks
        check ??= earlier;
        if (check != null) return Done(check);
        // Both possible artist links are worked out and kept, so the preference can change later without searching again.
        var options = new JsonObject { ["track"] = null, ["bandcamp"] = null, ["soundcloud"] = null, ["search"] = null };
        JsonObject PageOption((string Url, string Name) page, string whose) => new()
        {
            ["status"] = "bandcamp_artist", ["site"] = "Bandcamp", ["url"] = page.Url, ["found"] = page.Name,
            ["release"] = "", ["year"] = "", ["note"] = $"{whose} page; the track isn't on any release there now",
        };
        bool AnyLink() => options.B("track") || options.B("bandcamp") || options.B("soundcloud");
        // An edit, remix, bootleg or flip belongs to whoever made it: link to them, not the original artist.
        var credits = M.RemixCredits(title);
        if (credits.Count > 0)
        {
            if (makerPages.Count > 0) options["bandcamp"] = PageOption(makerPages[0], "Remixer's");
            var found = sc ? await FromSoundCloudAsync(net, artist, steps, title, credits, ct) : null;
            if (found != null && found.S("status") is "soundcloud" or "soundcloud_track")
            {
                options[found.S("status") == "soundcloud_track" ? "track" : "soundcloud"] = found;
                options["remix"] = true;                      // that profile is the remixer's own
            }
            if (!AnyLink()) steps.Add($"no page found for \"{credits[0]}\"; falling back to the artist");
        }
        if (!AnyLink())
        {
            var artistOnly = pages.Where(p => !makerPages.Contains(p)).ToList();
            var tracked = bc ? TrackedPage("bandcamp", M.ArtistParts(artist)) : null;
            if (tracked != null)                               // a page you track for this artist, under any of their names
            {
                var opt = PageOption(tracked.Value, "Artist's");
                opt["note"] = "A page you track; the track isn't on any release there now";
                options["bandcamp"] = opt;
                steps.Add($"Bandcamp: {tracked.Value.Url} is a page you track for this artist");
            }
            else if (artistOnly.Count > 0) options["bandcamp"] = PageOption(artistOnly[0], "Artist's");
            var found = sc ? await FromSoundCloudAsync(net, artist, steps, title, ct: ct) : null;
            if (found != null)
                options[found.S("status") switch { "soundcloud_track" => "track", "soundcloud" => "soundcloud", _ => "search" }] = found;
        }
        if (beatport != null) options["beatport"] = beatport;      // a partial match there
        // 7. YouTube: only when the track wasn't found on SoundCloud
        if (!options.B("track"))
        {
            var (video, channel) = await YoutubeStepAsync(net, artist, title, steps, ct);
            if (video != null) options["youtube"] = video;
            if (channel != null) options["youtube_channel"] = channel;
        }
        // 8. Discogs, last: only when the track itself wasn't found anywhere else
        if (!TrackOptions.Any(options.B))
        {
            var found = await DiscogsStepAsync(net, artist, title, steps, ct);
            if (found != null) options["discogs"] = found;
        }
        var picked = PickFallback(options);
        picked["options"] = options.Copy();
        return Done(picked);
    }

    // ---------- running a lookup ----------

    /// <summary>Tracks searched for in the latest lookup (Saved results' "Searched in the last lookup").</summary>
    public HashSet<string> LastRun() => Db.KvList("last_run").ToHashSet();

    void NoteRun(IEnumerable<string> keys, bool reset = false)
    {
        var all = (reset ? new List<string>() : LastRun().ToList()).Concat(keys.Where(k => k.Length > 0));
        Db.KvSetList("last_run", all.Distinct().OrderBy(x => x, StringComparer.Ordinal));
    }

    static void Fill(JsonObject row, JsonObject result)
    {
        foreach (var k in new[] { "site", "url", "year", "release", "found", "note" }) row[k] = "";
        row["steps"] = new JsonArray();
        row["options"] = null;
        Merge(row, result);
    }

    static JsonObject Skipped() => new()
    {
        ["status"] = "skipped", ["note"] = "Skipped; add the link with Edit, or search for it again",
        ["steps"] = J.Arr(new[] { "skipped by you before the search finished" }),
    };

    /// <summary>A row's search, unless you press Skip on it meanwhile: then it is stopped and null comes back.</summary>
    static async Task<T?> UnlessSkippedAsync<T>(LookupJob job, int n, Func<CancellationToken, Task<T>> work, CancellationToken outer) where T : struct
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var task = work(cts.Token);
        while (!task.IsCompleted)
        {
            await Task.WhenAny(task, Task.Delay(300, outer));
            bool skipped;
            lock (job) skipped = job.Skip.Contains(n);
            if (skipped && !task.IsCompleted)
            {
                cts.Cancel();
                try { await task; } catch (Exception) { }
                return null;
            }
        }
        return await task;
    }

    /// <summary>Stop searching for one row now (or never start): it is left for you to fill in.</summary>
    public void SkipRow(LookupJob job, int n)
    {
        lock (job)
        {
            var row = job.Rows.FirstOrDefault(r => (int)(r.N("n") ?? -1) == n) ?? throw new InvalidOperationException("That row isn't in this lookup.");
            if (row.S("status") != "pending" || job.Finished) throw new InvalidOperationException("That row isn't waiting to be searched.");
            job.Skip.Add(n);
            if (job.Current != n) Fill(row, WithListLink(Skipped(), row.T("artist"), row.T("title"))!);   // not started yet: skipped at once
        }
        job.Touch();
    }

    static string PyError(Exception e) => $"{PyName(e)}: {e.Message}";

    /// <summary>Three passes, quickest first: saved answers and what is already known; release years for those;
    /// then searching for whatever is left.</summary>
    async Task RunAsync(LookupJob job, bool fresh, bool retryMissing, CancellationToken ct)
    {
        var state = new RunState();
        void Phase(string text) { lock (job) job.Phase = text; job.Touch(); }

        List<JsonObject> QuickPass(List<JsonObject> rows, Library lib)
        {
            var left = new List<JsonObject>();
            foreach (var row in rows)
            {
                string artist = row.T("artist"), title = row.T("title");
                var manual = ManualFor(artist, title);                  // your own link always wins
                var saved = manual ?? (fresh ? null : SavedFor(artist, title));
                if (saved != null && manual == null && retryMissing && FallbackStatuses.Contains(saved.S("status"))) saved = null;
                if (saved != null && manual == null && (saved.N("engine") ?? 0) < EngineVersion &&
                    (saved.S("status") != "bandcamp" || M.VipWords(title) != null || Regex.IsMatch(Matcher.Norm(title), @"\d") || WipIn(title)))
                    saved = null;                                       // a miss from an older version: try again
                if (saved != null && manual == null) saved = WithKnownProfile(saved, artist, title);
                if (saved != null && manual == null) saved = WithCurrentFallback(saved);
                if (saved != null && manual == null)
                {
                    var extra = NewlyOn(saved, artist, title);
                    if (extra.Count > 0 && extra.IsSubsetOf(ExtraSites))
                    {
                        lock (job) job.Extend[(int)(row.N("n") ?? 0)] = (saved, extra);   // only extra sites are new: look there
                        left.Add(row);
                        continue;
                    }
                    if (extra.Count > 0) saved = null;                  // a site switched on since: search again
                }
                if (saved != null && manual == null)
                    saved = saved.With(("steps", J.Arr(saved.Strs("steps").Append("(saved result from an earlier lookup)"))));
                var result = saved ?? (_engine.Sites.LookupSites().Contains("bandcamp") ? FindLocal(lib, artist, title) : null);
                if (manual == null)
                {
                    var listed = KnownFor(artist, title).Url;
                    if (listed.Length > 0 && (result != null || Regex.IsMatch(listed, @"bandcamp\.com/(album|track)/")))
                        result = WithListLink(result, artist, title);
                }
                if (result != null)
                {
                    lock (job) { Fill(row, result); job.Done++; }
                    job.Touch();
                }
                else left.Add(row);
            }
            return left;
        }

        Phase("Checking what is already saved…");
        SeedProfiles();
        var lib = new Library(Db, M);
        var todo = QuickPass(job.Rows, lib);

        if (CollectionFan().Length > 0 && CollectionIsStale())
        {
            Phase("Reading your Bandcamp purchases…");
            var before = lib.OwnedItems.Count;
            await SyncCollectionAsync(onProgress: Phase, ct: ct);
            if (Collection.Error.Length > 0) lock (job) job.Warning = Collection.Error + " Carrying on with the purchases already saved.";
            lib = new Library(Db, M);
            if (lib.OwnedItems.Count != before) todo = QuickPass(todo, lib);    // new purchases: check the rest against them
        }

        using var client = _engine.MakeClient();
        var net = MakeNet(client);
        net.Fresh = fresh || retryMissing;                       // an explicit "search again" asks Bandcamp anew
        // 2. years for answers found in your purchases whose page hasn't been read, and the album for single-track pages
        List<JsonObject> needYear;
        lock (job)
            needYear = job.Rows.Where(r => r.S("site") == "Bandcamp" && (
                (r.S("status") == "imported" && !r.B("year") && Regex.IsMatch(r.T("url"), "/(album|track)/"))
                || (r.S("status") is "bandcamp" or "check" && ((!r.B("year") && r.S("note") == "In your purchases") || r.T("url").Contains("/track/"))))).ToList();
        for (int n = 1; n <= needYear.Count; n++)
        {
            if (!state.BandcampOk) break;
            var row = needYear[n - 1];
            Phase($"Found {job.Done} of {job.Total}; reading release details ({n} of {needYear.Count})");
            try
            {
                var rel = await GetReleaseAsync(net, row.T("url"), ct: ct);
                lock (job)
                {
                    if (rel != null && rel.B("year") && !row.B("year")) row["year"] = rel.S("year");
                    if (row.S("status") == "imported")              // your own link is left exactly as you gave it
                    {
                        if (rel != null && !row.B("release")) row["release"] = rel.S("release") is { Length: > 0 } r ? r : rel.T("title");
                    }
                    else if (rel != null && rel.B("release_url") && row.T("url").Contains("/track/"))
                    {
                        row["url"] = rel.S("release_url");
                        row["release"] = rel.S("release") is { Length: > 0 } r ? r : row.T("release");
                        row["steps"] = J.Arr(row.Strs("steps").Append("linked to the album this track is on"));
                    }
                }
                job.Touch();
            }
            catch (Blocked e)
            {
                state.BandcampOk = false;
                lock (job) job.Warning = $"{e.Message}, even after waiting. Run the lookup again later for the rest.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { }
        }
        // 3. everything not answered yet
        NoteRun(todo.Select(r => TrackKey(r.T("artist"), r.T("title"))));
        for (int n = 1; n <= todo.Count; n++)
        {
            var row = todo[n - 1];
            var rn = (int)(row.N("n") ?? 0);
            (JsonObject Saved, HashSet<string> Extra)? extend = null;
            lock (job)
            {
                if (job.Extend.Remove(rn, out var ex)) extend = ex;
                if (job.Skip.Contains(rn))
                {
                    if (row.S("status") == "pending") Fill(row, WithListLink(Skipped(), row.T("artist"), row.T("title"))!);   // skipped before its turn
                    job.Done++;
                    continue;
                }
                job.Current = rn;                                 // the row being searched right now
            }
            var keys = extend != null ? Sites.LookupOrder.Where(extend.Value.Extra.Contains).ToList() : _engine.Sites.LookupSites();
            var where = string.Join(" and ", keys.Select(_engine.Sites.Label).Distinct());
            if (where.Length == 0) where = "nothing";
            Phase($"Searching {where} for “{row.T("title")}” by {row.T("artist")} ({n} of {todo.Count} not already saved)");
            (JsonObject Result, bool Keep)? found;
            try
            {
                if (extend != null)
                {
                    var (saved, extra) = extend.Value;
                    found = await UnlessSkippedAsync<(JsonObject, bool)>(job, rn, async c => (await AddExtrasAsync(net, saved, row.T("artist"), row.T("title"), extra, c), true), ct);
                }
                else found = await UnlessSkippedAsync<(JsonObject, bool)>(job, rn, c => FindAsync(net, lib, row.T("artist"), row.T("title"), state, c), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                found = (new JsonObject
                {
                    ["status"] = "error", ["steps"] = J.Arr(new[] { Cut(PyError(e), 200) }),
                    ["note"] = $"Couldn't complete the search ({PyName(e)}); run it again to retry",
                }, false);
            }
            if (found == null)                                   // you skipped it while it was being searched
            {
                lock (job)
                {
                    if (row.S("status") == "pending") Fill(row, WithListLink(Skipped(), row.T("artist"), row.T("title"))!);
                    job.Done++;
                }
                job.Touch();
                continue;
            }
            var (result, keep) = found.Value;
            if (keep && result.S("note") != "In your feed") Remember(CacheKey(row.T("artist"), row.T("title")), result, row.T("artist"), row.T("title"));
            lock (job)
            {
                Fill(row, WithListLink(result, row.T("artist"), row.T("title"))!);
                job.Done++;
                if (state.Warning.Length > 0) job.Warning = state.Warning;
            }
            job.Touch();
        }
        lock (job)
        {
            job.Phase = "";
            job.Current = null;
            job.Finished = true;
        }
        job.Touch();
    }

    static readonly Regex Wip = new(@"[\(\[]\s*(?:wip|w\.i\.p\.?|work in progress|forthcoming|forth\.?(?=\s))[^\)\]]*[\)\]]|(?<=\S)\s+[-–]?\s*\b(?:wip|w\.i\.p\.?)(?=\s*(?:$|[\(\[]))", RegexOptions.IgnoreCase);
    static bool WipIn(string title) => Wip.IsMatch(Names.Fold(title));

    /// <summary>Start a lookup in the background. The job's rows fill in as it goes.</summary>
    public LookupJob Start(IReadOnlyList<WantedTrack> tracks, bool fresh = false, bool retryMissing = false)
    {
        var job = new LookupJob { Id = Guid.NewGuid().ToString("N")[..12], Total = tracks.Count };
        foreach (var t in tracks)
            job.Rows.Add(new JsonObject
            {
                ["n"] = t.N, ["artist"] = t.Artist, ["title"] = t.Title, ["status"] = "pending", ["site"] = "", ["url"] = "",
                ["year"] = "", ["release"] = "", ["found"] = "", ["note"] = "", ["steps"] = new JsonArray(),
            });
        lock (Jobs)
        {
            while (Jobs.Count > 20) Jobs.Remove(Jobs.Keys.First());    // keep only recent lookups in memory
            Jobs[job.Id] = job;
        }
        NoteRun(Array.Empty<string>(), reset: true);            // a new lookup starts a new "searched in the last lookup" list
        _ = Task.Run(async () =>
        {
            try
            {
                using var hold = Pace.Foreground();
                await RunAsync(job, fresh, retryMissing, _stop.Token);
            }
            catch (Exception e)
            {
                lock (job)
                {
                    job.Warning = $"The lookup stopped early: {PyError(e)}";
                    job.Finished = true;
                }
                job.Touch();
            }
        });
        return job;
    }

    /// <summary>Search again for one row of a finished lookup, ignoring what was saved for it.</summary>
    public void RetryRow(LookupJob job, int n)
    {
        JsonObject row;
        lock (job)
        {
            row = job.Rows.FirstOrDefault(r => (int)(r.N("n") ?? -1) == n) ?? throw new InvalidOperationException("That row isn't in this lookup.");
            if (!job.Finished) throw new InvalidOperationException("Wait for the lookup to finish, then search this row again.");
            Fill(row, new JsonObject { ["status"] = "pending" });
            job.Finished = false;
            job.Current = n;
            job.Done = Math.Max(0, job.Done - 1);
            job.Phase = $"Searching again for “{row.T("title")}” by {row.T("artist")}";
        }
        NoteRun(new[] { TrackKey(row.T("artist"), row.T("title")) });
        job.Touch();
        _ = Task.Run(async () =>
        {
            JsonObject result;
            try
            {
                using var hold = Pace.Foreground();
                var state = new RunState();
                using var client = _engine.MakeClient();
                var net = MakeNet(client);
                net.Fresh = true;
                var found = await UnlessSkippedAsync<(JsonObject, bool)>(job, n, c => FindAsync(net, new Library(Db, M), row.T("artist"), row.T("title"), state, c), _stop.Token);
                bool keep;
                (result, keep) = found ?? (Skipped(), false);
                if (keep && result.S("note") != "In your feed") Remember(CacheKey(row.T("artist"), row.T("title")), result, row.T("artist"), row.T("title"));
                if (state.Warning.Length > 0) lock (job) job.Warning = state.Warning;
            }
            catch (Exception e)
            {
                result = new JsonObject
                {
                    ["status"] = "error", ["steps"] = J.Arr(new[] { Cut(PyError(e), 200) }),
                    ["note"] = $"Couldn't complete the search ({PyName(e)}); try again",
                };
            }
            lock (job)
            {
                if (row.S("status") == "pending") Fill(row, WithListLink(result, row.T("artist"), row.T("title"))!);  // not answered meanwhile
                job.Done++;
                job.Current = null;
                job.Phase = "";
                job.Finished = true;
                job.Skip.Remove(n);
            }
            job.Touch();
        });
    }

    public static readonly Dictionary<string, string> StatusText = new()
    {
        ["imported"] = "From your list", ["manual"] = "Link you set", ["bandcamp"] = "Bandcamp release", ["bandcamp_check"] = "Bandcamp, check match",
        ["bandcamp_artist"] = "Bandcamp artist page", ["soundcloud_track"] = "SoundCloud track", ["soundcloud"] = "SoundCloud artist page",
        ["soundcloud_search"] = "SoundCloud search", ["youtube"] = "YouTube video", ["youtube_channel"] = "YouTube channel",
        ["beatport"] = "Beatport track", ["beatport_check"] = "Beatport, check match", ["discogs"] = "Discogs release",
        ["none"] = "Not found", ["error"] = "Error", ["pending"] = "Not looked up yet", ["skipped"] = "Skipped",
    };

    /// <summary>Why a row's link should be checked by a person; empty when there is no particular doubt.</summary>
    public static string ReviewReason(JsonObject row)
    {
        var status = row.S("status");
        var note = row.T("note").ToLowerInvariant();
        if (status == "soundcloud" && note.Contains("name only")) return "Profile picked by name alone; it may belong to a different artist with the same name";
        if (status is "bandcamp_check" or "beatport_check" || (status == "discogs" && note.Contains("check match"))) return "Partial match; check it is the right track";
        return "";
    }

    /// <summary>A lookup's rows as shown and exported: each says whether it needs review, with your list's spelling.</summary>
    public List<JsonObject> View(LookupJob job)
    {
        List<JsonObject> rows;
        lock (job) rows = job.Rows.Select(r => r.Copy()).ToList();
        foreach (var r in rows)
        {
            var known = KnownFor(r.T("artist"), r.T("title"));
            if (!r.B("year") && r.B("status")) r["year"] = known.Year;      // your list's year, where the lookup has none
            r["review"] = ReviewReason(r);
            r["list_artist"] = known.Artist;
            r["list_title"] = known.Title;
            r["list_link"] = known.Url;
            r["list_note"] = known.Note;
        }
        return rows;
    }

    /// <summary>A lookup as a spreadsheet.</summary>
    public string ToCsv(LookupJob job)
    {
        var sb = new StringBuilder("﻿");
        sb.Append(Csv.Line(new[] { "#", "artist", "title", "year", "link", "needs review", "artist (your spelling)", "title (your spelling)",
                                   "link note", "site", "release", "result", "found as", "note", "steps" })).Append("\r\n");
        List<JsonObject> rows;
        lock (job) rows = job.Rows.Select(r => r.Copy()).ToList();
        foreach (var r in rows)
        {
            var doubt = ReviewReason(r);
            var known = KnownFor(r.T("artist"), r.T("title"));
            sb.Append(Csv.Line(new[]
            {
                r.T("n"), r.T("artist"), r.T("title"), r.B("year") ? r.T("year") : known.Year, r.T("url"), doubt.Length > 0 ? "REVIEW: " + doubt : "",
                known.Artist, known.Title, known.Note, r.T("site"), r.T("release"), StatusText.GetValueOrDefault(r.T("status")) ?? r.T("status"),
                r.T("found"), r.T("note"), string.Join(" | ", r.Strs("steps")),
            })).Append("\r\n");
        }
        return sb.ToString();
    }

    // ---------- your own list: how names should be written, and links you already have ----------

    sealed record KnownCache(Dictionary<string, List<Dictionary<string, object?>>> Tracks, Dictionary<string, string> Artists);
    KnownCache? _known;

    /// <summary>(your list's entries by key, artist spellings by key); the plainest version of a key first.</summary>
    KnownCache Known()
    {
        lock (_indexLock)
        {
            if (_known != null) return _known;
            var tracks = new Dictionary<string, List<Dictionary<string, object?>>>();
            foreach (var r in Db.Query("SELECT * FROM known_tracks ORDER BY key, LENGTH(variant), variant"))
            {
                var key = r.S("key") ?? "";
                if (!tracks.TryGetValue(key, out var list)) tracks[key] = list = new();
                list.Add(r);
            }
            var artists = new Dictionary<string, string>();
            foreach (var r in Db.Query("SELECT * FROM known_artists")) artists[r.S("key") ?? ""] = r.S("name") ?? "";
            return _known = new KnownCache(tracks, artists);
        }
    }

    public void ForgetKnownCache() { lock (_indexLock) _known = null; }

    public sealed record KnownInfo(string Artist, string Title, string Year, string Url, string Others, string Note);

    /// <summary>Your list's entry for this track: the one written the same way, else the plainest version.</summary>
    public Dictionary<string, object?>? KnownEntry(string artist, string title)
    {
        var entries = Known().Tracks.GetValueOrDefault(TrackKey(artist, title));
        if (entries == null)
            foreach (var reading in BareRemixReadings(title))   // "Tune X Flip" for the list's "Tune (X Flip)"
            {
                entries = Known().Tracks.GetValueOrDefault(TrackKey(artist, reading));
                title = reading;
                if (entries != null) break;
            }
        if (entries == null || entries.Count == 0) return null;
        var plain = Matcher.Plain(title);
        return entries.FirstOrDefault(t => t.S("variant") == plain) ?? entries[0];
    }

    /// <summary>What your list says about a track, blank where it says nothing.</summary>
    public KnownInfo KnownFor(string artist, string title)
    {
        var t = KnownEntry(artist, title);
        return new KnownInfo(t?.S("artist") is { Length: > 0 } a ? a : Known().Artists.GetValueOrDefault(Matcher.Norm(artist)) ?? "",
            t?.S("title") ?? "", t?.S("year") ?? "", t?.S("url") ?? "", t?.S("others") ?? "", t?.S("note") ?? "");
    }

    public static readonly string[] Strong = { "manual", "bandcamp", "soundcloud_track" };     // answers that outrank your list's link

    public static JsonObject FromList(string url, string year, string note) => new()
    {
        ["status"] = "imported", ["site"] = SiteOf(url), ["url"] = url, ["year"] = year, ["release"] = "", ["found"] = "", ["note"] = note,
        ["steps"] = J.Arr(new[] { "link taken from the list you imported" }),
    };

    /// <summary>Your list's link, where the lookup has nothing better.</summary>
    public JsonObject? WithListLink(JsonObject? result, string artist, string title)
    {
        var known = KnownFor(artist, title);
        if (known.Url.Length == 0 || Strong.Contains(result?.S("status"))) return result;
        var outp = FromList(known.Url, known.Year, known.Note);
        if (result != null) outp["steps"] = J.Arr(result.Strs("steps").Concat(outp.Strs("steps")));
        return outp;
    }

    public JsonObject KnownCounts() => new()
    {
        ["known_tracks"] = Db.Query("SELECT COUNT(*) AS n FROM known_tracks")[0].L("n"),
        ["known_artists"] = Db.Query("SELECT COUNT(*) AS n FROM known_artists")[0].L("n"),
    };

    static string Commonest(IEnumerable<string> values)
    {
        var counts = new Dictionary<string, int>();
        var order = new List<string>();
        foreach (var v in values)
        {
            if (!counts.ContainsKey(v)) order.Add(v);
            counts[v] = counts.GetValueOrDefault(v) + 1;
        }
        string best = ""; int top = 0;
        foreach (var v in order) if (counts[v] > top) { top = counts[v]; best = v; }     // the first of equals
        return best;
    }

    /// <summary>Store your list. Rows for the same track are merged; importing again updates what is there.</summary>
    public JsonObject ImportKnown(List<Tracklist.Known> entries)
    {
        var now = Db.Now();
        var groups = new Dictionary<(string, string), List<Tracklist.Known>>();
        var groupOrder = new List<(string, string)>();
        var names = new Dictionary<string, List<string>>();
        int skipped = 0, spellingClashes = 0, linkClashes = 0;
        var examples = new List<JsonObject>();
        foreach (var e in entries)
        {
            var aKey = Matcher.Norm(e.Artist);
            if (aKey.Length > 0)
            {
                if (!names.TryGetValue(aKey, out var l)) names[aKey] = l = new List<string>();
                l.Add(e.Artist);
            }
            if (e.Title.Length == 0) continue;
            if (Matcher.Norm(e.Title).Length == 0 || aKey.Length == 0) { skipped++; continue; }
            var g = (TrackKey(e.Artist, e.Title), Matcher.Plain(e.Title));
            if (!groups.TryGetValue(g, out var list)) { groups[g] = list = new(); groupOrder.Add(g); }
            list.Add(e);
        }
        var artists = names.ToDictionary(kv => kv.Key, kv => Commonest(kv.Value));
        foreach (var (k, v) in names)
            if (v.Distinct().Count() > 1)
            {
                spellingClashes++;
                examples.Add(new JsonObject { ["what"] = "artist", ["kept"] = artists[k], ["ignored"] = string.Join(", ", v.Distinct().Where(x => x != artists[k]).OrderBy(x => x, StringComparer.Ordinal)) });
            }
        var tracks = new List<(string Key, string Variant, string Artist, string Title, string Url, string Year, string Others, string Note)>();
        foreach (var g in groupOrder)
        {
            var group = groups[g];
            var spellingText = Commonest(group.Select(e => e.Artist + "\u0000" + e.Title));
            var spelling = spellingText.Split('\u0000');
            var joined = string.Join(" - ", spelling);
            if (group.Select(e => (e.Artist, e.Title)).Distinct().Count() > 1)
            {
                spellingClashes++;
                examples.Add(new JsonObject { ["what"] = "track", ["kept"] = joined,
                    ["ignored"] = string.Join(", ", group.Select(e => $"{e.Artist} - {e.Title}").Distinct().Where(x => x != joined).OrderBy(x => x, StringComparer.Ordinal)) });
            }
            var linked = group.Where(e => e.Url.Length > 0).ToList();
            var url = "";
            if (linked.Count > 0)
            {
                var top = linked.Min(e => Tracklist.LinkRank(e.Url));
                url = Commonest(linked.Where(e => Tracklist.LinkRank(e.Url) == top).Select(e => e.Url));
                if (linked.Select(e => e.Url).Distinct().Count() > 1)
                {
                    linkClashes++;
                    examples.Add(new JsonObject { ["what"] = "link", ["kept"] = $"{joined}: {url}",
                        ["ignored"] = string.Join(", ", linked.Select(e => e.Url).Distinct().Where(x => x != url).OrderBy(x => x, StringComparer.Ordinal)) });
                }
            }
            var chosen = group.Where(e => e.Url == url).ToList();     // the note and year that go with that link
            var year = Commonest(chosen.Where(e => e.Year.Length > 0).Select(e => e.Year));
            if (year.Length == 0) year = Commonest(group.Where(e => e.Year.Length > 0).Select(e => e.Year));
            tracks.Add((g.Item1, g.Item2, spelling[0], spelling.Length > 1 ? spelling[1] : "", url, year,
                        Commonest(chosen.Where(e => e.Others.Length > 0).Select(e => e.Others)), Commonest(chosen.Where(e => e.Note.Length > 0).Select(e => e.Note))));
        }
        Db.InTransaction(tx =>
        {
            foreach (var t in tracks)
                tx.Execute("INSERT INTO known_tracks(key, variant, artist, title, year, url, others, note, at) VALUES(?,?,?,?,?,?,?,?,?) " +
                           "ON CONFLICT(key, variant) DO UPDATE SET artist=excluded.artist, title=excluded.title, year=excluded.year," +
                           " url=excluded.url, others=excluded.others, note=excluded.note, at=excluded.at",
                           t.Key, t.Variant, t.Artist, t.Title, t.Year, t.Url, t.Others, t.Note, now);
            foreach (var (k, name) in artists)
                tx.Execute("INSERT INTO known_artists(key, name, at) VALUES(?,?,?) ON CONFLICT(key) DO UPDATE SET name=excluded.name, at=excluded.at", k, name, now);
            return 0;
        });
        ForgetKnownCache();
        var order = new Dictionary<string, int> { ["link"] = 0, ["track"] = 1, ["artist"] = 2 };
        var outp = new JsonObject
        {
            ["rows"] = entries.Count, ["tracks"] = tracks.Count, ["artists"] = artists.Count, ["skipped"] = skipped,
            ["with_link"] = tracks.Count(t => t.Url.Length > 0), ["release_links"] = tracks.Count(t => Tracklist.LinkRank(t.Url) == 0),
            ["with_note"] = tracks.Count(t => t.Note.Length > 0), ["spelling_clashes"] = spellingClashes, ["link_clashes"] = linkClashes,
            ["clashes"] = spellingClashes + linkClashes,
            ["clash_examples"] = J.Arr(examples.OrderBy(x => order[x.T("what")]).Take(40)),
        };
        foreach (var (k, v) in KnownCounts()) outp[k] = v?.DeepClone();
        return outp;
    }

    /// <summary>When the tidying changes, file each entry of your list under what its name tidies to now.</summary>
    public int RekeyKnown()
    {
        int moved = 0;
        foreach (var r in Db.Query("SELECT rowid AS id, key, variant, artist, title FROM known_tracks"))
        {
            var key = TrackKey(r.S("artist") ?? "", r.S("title") ?? "");
            var variant = Matcher.Plain(r.S("title"));
            if (key != r.S("key") || variant != r.S("variant"))
            {
                Db.Execute("UPDATE OR REPLACE known_tracks SET key=?, variant=? WHERE rowid=?", key, variant, r.L("id"));
                moved++;
            }
        }
        foreach (var r in Db.Query("SELECT key, name FROM known_artists"))
            if (Matcher.Norm(r.S("name")) != r.S("key"))
            {
                Db.Execute("UPDATE OR REPLACE known_artists SET key=? WHERE key=?", Matcher.Norm(r.S("name")), r.S("key"));
                moved++;
            }
        ForgetKnownCache();
        return moved;
    }

    /// <summary>Your spelling for one saved track, typed in Saved results. Both empty removes the entry.</summary>
    public JsonObject SetSpelling(string key, string artist, string title, string variant = "", (string Key, string Variant)? like = null)
    {
        artist = string.Join(' ', (artist ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        title = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var old = variant.Length > 0 ? Db.Query("SELECT * FROM known_tracks WHERE key=? AND variant=?", key, variant).FirstOrDefault() : null;
        if (old != null) Db.Execute("DELETE FROM known_tracks WHERE key=? AND variant=?", key, variant);
        else if (like != null) old = Db.Query("SELECT * FROM known_tracks WHERE key=? AND variant=?", like.Value.Key, like.Value.Variant).FirstOrDefault();
        if (artist.Length > 0 || title.Length > 0)
            Db.Execute("INSERT INTO known_tracks(key, variant, artist, title, year, url, others, note, at) VALUES(?,?,?,?,?,?,?,?,?) " +
                       "ON CONFLICT(key, variant) DO UPDATE SET artist=excluded.artist, title=excluded.title, at=excluded.at",
                       key, title.Length > 0 ? Matcher.Plain(title) : variant, artist, title, old?.S("year") ?? "", old?.S("url") ?? "",
                       old?.S("others") ?? "", old?.S("note") ?? "", Db.Now());
        ForgetKnownCache();
        return KnownCounts();
    }

    /// <summary>Take one track out of your imported list.</summary>
    public JsonObject ForgetEntry(string key, string variant)
    {
        Db.Execute("DELETE FROM known_tracks WHERE key=? AND variant=?", key, variant);
        ForgetKnownCache();
        return KnownCounts();
    }

    public JsonObject ClearKnown()
    {
        Db.Execute("DELETE FROM known_tracks");
        Db.Execute("DELETE FROM known_artists");
        ForgetKnownCache();
        return KnownCounts();
    }

    internal static string Invariant(double d) => d.ToString(CultureInfo.InvariantCulture);
}
