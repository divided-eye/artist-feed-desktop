using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArtistFeed.Core;

/// <summary>What could fill a Tracked Artists row's empty cells: guesses, to be reviewed before saving.</summary>
public sealed record Suggestion(string Name, Dictionary<string, string> Cells, string Note);

public sealed class BlockedException(string message) : Exception(message);

/// <summary>
/// Find missing links, as in the web version: the Bandcamp page, the SoundCloud profile, and the name
/// when only a link was given. Bandcamp is asked at the shared pace, ahead of the feed refresh.
/// Answers from Bandcamp's search and artist pages are kept in web_cache, in the web version's format.
/// </summary>
public sealed class Suggester(Engine engine)
{
    const int SearchDays = 7, GridDays = 3, FollowsHours = 12;

    // ---------- plumbing ----------

    async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            await engine.Pace.WaitAsync(background: false, ct);
            var r = await client.GetAsync(url, ct);
            if (r.StatusCode != HttpStatusCode.TooManyRequests) return r;
            var wait = engine.Pace.PauseFor(r, attempt);
            r.Dispose();
            engine.Pace.SlowDown(wait);
            if (attempt >= 2) throw new BlockedException("Bandcamp is asking the app to slow down. Try the rest again in a few minutes.");
        }
    }

    JsonNode? CacheGet(string kind, string key, double days)
    {
        var rows = engine.Db.Query("SELECT data, at FROM web_cache WHERE kind=? AND key=?", kind, key);
        if (rows.Count == 0) return null;
        if (!DateTime.TryParse((string)rows[0]["at"]!, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)) return null;
        return DateTime.UtcNow - at <= TimeSpan.FromDays(days) ? Html.ParseJson((string)rows[0]["data"]!) : null;
    }

    void CachePut(string kind, string key, JsonNode data) =>
        engine.Db.Execute("INSERT INTO web_cache(kind, key, data, at) VALUES(?,?,?,?) " +
                          "ON CONFLICT(kind, key) DO UPDATE SET data=excluded.data, at=excluded.at",
                          kind, key, data.ToJsonString(), Db.Now());

    /// <summary>bandcamp.com/search results of one kind ("b": artists and labels): type, name, url.</summary>
    async Task<List<(string Type, string Name, string Url)>> SearchAsync(HttpClient client, string text, string kind, CancellationToken ct)
    {
        var key = $"{kind}|{Names.Plain(text)}";
        var saved = CacheGet("search", key, SearchDays) as JsonArray;
        if (saved == null)
        {
            using var r = await GetAsync(client, $"https://bandcamp.com/search?q={Uri.EscapeDataString(text)}&item_type={kind}", ct);
            if (!r.IsSuccessStatusCode) throw new HttpRequestException($"Bandcamp search answered {(int)r.StatusCode}");
            saved = new JsonArray();
            foreach (var c in Bandcamp.ParseSearch(await r.Content.ReadAsStringAsync(ct)))
                saved.Add(new JsonObject { ["type"] = c.Type, ["name"] = c.Name, ["url"] = c.Url, ["artist"] = c.Artist, ["album"] = "", ["released"] = "" });
            CachePut("search", key, saved);
        }
        return saved.OfType<JsonObject>()
            .Select(o => (Html.AsString(o["type"]) ?? "", Html.AsString(o["name"]) ?? "", Html.AsString(o["url"]) ?? "")).ToList();
    }

    /// <summary>An artist or label page's own name (null when there is no such page).</summary>
    async Task<string?> PageNameAsync(HttpClient client, string baseUrl, CancellationToken ct)
    {
        var saved = CacheGet("grid", baseUrl, GridDays);
        if (saved != null) return saved is JsonObject o && o.Count > 0 ? Html.AsString(o["name"]) ?? "" : null;
        using var r = await GetAsync(client, baseUrl + "/music", ct);
        if (r.StatusCode == HttpStatusCode.NotFound)
        {
            CachePut("grid", baseUrl, new JsonObject());
            return null;
        }
        if (!r.IsSuccessStatusCode) return null;
        var html = await r.Content.ReadAsStringAsync(ct);
        var items = new JsonArray();
        foreach (var it in Bandcamp.ParseMusicGrid(html, baseUrl))
            items.Add(new JsonObject { ["title"] = it.Title, ["url"] = it.Url, ["artist"] = it.Author ?? "" });
        var name = Bandcamp.ParseProfile(html).Name ?? "";
        CachePut("grid", baseUrl, new JsonObject { ["name"] = name, ["items"] = items });
        return name;
    }

    /// <summary>The profiles the SoundCloud account named in the web version's Settings follows (kept 12 hours).</summary>
    async Task<List<(string Username, string Permalink)>> FollowedAsync(CancellationToken ct)
    {
        var me = (engine.Db.KvGet("soundcloud_me") ?? "").Trim().ToLowerInvariant();
        if (me.Length == 0) return new();
        var saved = Html.ParseJson(engine.Db.KvGet("soundcloud_follows")) as JsonObject;
        List<(string, string)> From(JsonObject o) => (o["users"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(u => (Html.AsString(u["username"]) ?? "", Html.AsString(u["permalink"]) ?? "")).Where(u => u.Item2.Length > 0).ToList();
        var mine = saved != null && Html.AsString(saved["user"]) == me;
        var at = Html.AsNumber(saved?["at"]) ?? 0;
        if (mine && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - at < FollowsHours * 3600) return From(saved!);
        try
        {
            var found = await engine.Follows.SoundCloudAsync(me, ct);
            var users = new JsonArray(found.Select(f => (JsonNode?)new JsonObject
                { ["username"] = f.Name, ["permalink"] = f.Handle, ["url"] = f.Url }).ToArray());
            var fresh = new JsonObject
            {
                ["user"] = me, ["users"] = users, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["error"] = "",
                ["read_at"] = Db.Now(),
            };
            engine.Db.KvSet("soundcloud_follows", fresh.ToJsonString());
            return From(fresh);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return mine ? From(saved!) : new();
        }
    }

    static string Squash(string? text) => Regex.Replace(Names.Fold(text), "[^a-z0-9]", "");

    static string HandleOf(ISource source, string text)
    {
        try { return source.Normalize(text).Handle; }
        catch (Exception) { return ""; }
    }

    static string RootOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}" : url;

    /// <summary>(tier, similarity): how well a SoundCloud user fits the wanted name.</summary>
    static (int Tier, double Sim) ProfileFit(string username, string permalink, string want)
    {
        var shown = Names.Norm(username);
        var address = Names.Plain(permalink).Replace(" ", "");
        var squashed = want.Replace(" ", "");
        var sim = Math.Max(Names.NameSim(want, shown), Math.Max(Names.Ratio(squashed, address),
            address.Contains("and") ? Names.Ratio(squashed, address.Replace("and", "")) : 0));
        return sim >= 0.85 ? (2, Math.Round(sim, 2)) : (0, 0);
    }

    // ---------- the guesses ----------

    /// <param name="skip">Sites not to look for (cells cleared on purpose).</param>
    /// <param name="avoid">Pages already offered and turned down, per site: never offered again.</param>
    public async Task<Suggestion> SuggestAsync(string name, IReadOnlyDictionary<string, string> cells, IEnumerable<string> skip,
                                               IReadOnlyDictionary<string, List<string>> avoid, CancellationToken ct = default)
    {
        name = (Names.SplitNames(name).FirstOrDefault() ?? "").Trim();       // other names in the cell are aliases
        var typed = name;
        var have = new[] { "bandcamp", "soundcloud" }.ToDictionary(k => k,
            k => (cells.GetValueOrDefault(k) ?? "").Split(',')[0].Trim());
        var outCells = new Dictionary<string, string>();
        var notes = new List<string>();
        var bc = engine.Bandcamp;
        var sc = engine.SoundCloud;
        var skipped = skip.ToHashSet();
        var wanted = have.Where(kv => kv.Value.Length == 0 && !skipped.Contains(kv.Key) && engine.Sites.IsOn(kv.Key)).Select(kv => kv.Key).ToHashSet();
        var bad = new Dictionary<string, HashSet<string>>
        {
            ["bandcamp"] = (avoid.GetValueOrDefault("bandcamp") ?? new()).Select(v => HandleOf(bc, v)).Where(h => h.Length > 0).ToHashSet(),
            ["soundcloud"] = (avoid.GetValueOrDefault("soundcloud") ?? new()).Select(v => HandleOf(sc, v)).Where(h => h.Length > 0).ToHashSet(),
        };
        // a cell that doesn't parse: leave the row to the person
        foreach (var (k, v) in have)
            if (v.Length > 0 && HandleOf(k == "bandcamp" ? bc : sc, v).Length == 0) return new Suggestion("", outCells, "");

        using var hold = engine.Pace.Foreground();           // the feed refresh stands aside meanwhile
        using var client = engine.MakeClient();
        string foundName = "";
        if (name.Length == 0)                                  // only a link: the name its page gives itself
        {
            try
            {
                if (have["bandcamp"].Length > 0) name = await PageNameAsync(client, bc.Normalize(have["bandcamp"]).Url, ct) ?? "";
                if (name.Length == 0 && have["soundcloud"].Length > 0)
                    name = Html.AsString((await sc.ApiAsync(client, "/resolve", new() { ["url"] = sc.Normalize(have["soundcloud"]).Url }, ct))?["username"]) ?? "";
            }
            catch (BlockedException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { }
            name = name.Trim();
            if (name.Length == 0) name = have["bandcamp"].Length > 0 ? bc.Short(have["bandcamp"]) : have["soundcloud"];
            foundName = name;
        }
        if (name.Length == 0) return new Suggestion(foundName, outCells, "");
        var want = Names.Norm(name);
        var hints = new HashSet<string>();
        if (have["bandcamp"].Length > 0) hints.Add(Squash(bc.Short(bc.Normalize(have["bandcamp"]).Handle)));
        if (have["soundcloud"].Length > 0) hints.Add(Squash(sc.Normalize(have["soundcloud"]).Handle));
        hints.Remove("");

        if (wanted.Contains("bandcamp"))
        {
            var found = new List<(bool Hint, double Fit, int Order, string Url)>();
            try
            {
                var results = await SearchAsync(client, name, "b", ct);
                for (int n = 0; n < results.Count; n++)
                {
                    var c = results[n];
                    var fit = Names.NameSim(want, Names.Norm(c.Name));
                    var host = Uri.TryCreate(c.Url, UriKind.Absolute, out var u) ? u.Host : "";
                    var sub = Squash(host.Split('.')[0]);
                    if (bad["bandcamp"].Contains(HandleOf(bc, RootOf(c.Url)))) continue;
                    if ((c.Type is "artist" or "label" or "") && (fit >= 0.85 || hints.Contains(sub)))
                        found.Add((hints.Contains(sub), Math.Round(fit, 2), -n, RootOf(c.Url)));
                }
            }
            catch (BlockedException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { notes.Add("Bandcamp search didn't answer"); }
            if (found.Count == 0)                              // an obvious address that search didn't offer
            {
                foreach (var guess in new[] { Squash(name) }.Concat(hints.OrderBy(x => x, StringComparer.Ordinal)).Distinct())
                {
                    if (guess.Length < 2 || bad["bandcamp"].Contains($"{guess}.bandcamp.com")) continue;
                    string? pageName;
                    try { pageName = await PageNameAsync(client, $"https://{guess}.bandcamp.com", ct); }
                    catch (BlockedException) { throw; }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception) { pageName = null; }
                    if (pageName != null && Names.NameSim(want, Names.Norm(pageName)) >= 0.85)
                    {
                        found.Add((hints.Contains(guess), 1.0, 0, $"https://{guess}.bandcamp.com"));
                        break;
                    }
                }
            }
            if (found.Count > 0)
            {
                var best = found.OrderByDescending(f => f.Hint).ThenByDescending(f => f.Fit).ThenByDescending(f => f.Order)
                    .ThenByDescending(f => f.Url, StringComparer.Ordinal).First();
                outCells["bandcamp"] = bc.Short(bc.Normalize(best.Url).Handle);
            }
            else notes.Add("no Bandcamp page found");
        }

        if (wanted.Contains("soundcloud"))
        {
            var found = new List<(bool Hint, bool Followed, double Fit, long Followers, string Profile)>();
            var follows = await FollowedAsync(ct);
            var followed = follows.Select(f => f.Permalink).ToHashSet();
            foreach (var (username, permalink) in follows)       // profiles you follow come first, even if search misses them
            {
                if (bad["soundcloud"].Contains(permalink)) continue;
                var handle = Squash(permalink);
                var fit = ProfileFit(username, handle, want);
                if (fit.Tier > 0 || hints.Contains(handle)) found.Add((hints.Contains(handle), true, fit.Sim, 0, permalink));
            }
            try
            {
                var data = await sc.ApiAsync(client, "/search/users", new() { ["q"] = name, ["limit"] = "20" }, ct);
                foreach (var u in (data?["collection"] as JsonArray) ?? new JsonArray())
                {
                    var url = (Html.AsString(u?["permalink_url"]) ?? "").TrimEnd('/');
                    var handle = Squash(Html.AsString(u?["permalink"]) ?? url.Split('/')[^1]);
                    var profile = url.Split('/')[^1].ToLowerInvariant();
                    if (bad["soundcloud"].Contains(profile)) continue;
                    var fit = ProfileFit(Html.AsString(u?["username"]) ?? "", handle, want);
                    if (handle.Length > 0 && (fit.Tier > 0 || hints.Contains(handle)))
                        found.Add((hints.Contains(handle), followed.Contains(profile), fit.Sim, (long)(Html.AsNumber(u?["followers_count"]) ?? 0), profile));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                if (found.Count == 0) notes.Add("SoundCloud search didn't answer");
            }
            if (found.Count > 0)
                outCells["soundcloud"] = found.OrderByDescending(f => f.Hint).ThenByDescending(f => f.Followed).ThenByDescending(f => f.Fit)
                    .ThenByDescending(f => f.Followers).ThenByDescending(f => f.Profile, StringComparer.Ordinal).First().Profile;
            else if (!notes.Contains("SoundCloud search didn't answer")) notes.Add("no SoundCloud profile found");
        }
        return new Suggestion(typed.Length > 0 ? "" : foundName, outCells, string.Join("; ", notes));
    }
}
