using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArtistFeed.Core;

/// <summary>
/// SoundCloud: the same JSON endpoints the soundcloud.com site itself calls. They need a public
/// "client_id" embedded in the site's JavaScript; it is looked up automatically, cached, and looked
/// up again when it stops working.
/// </summary>
public sealed class SoundCloud(Db db) : ISource
{
    public const string Api = "https://api-v2.soundcloud.com";
    public string Key => "soundcloud";
    public string Label => "SoundCloud";
    public string Color => "#ff5500";
    public string Placeholder => "username or https://soundcloud.com/username";

    public int Limit { get; set; } = 30;
    const int MaxPages = 4;          // most pages with tracks on them read from one list in one visit
    const int MaxEmptyPages = 25;    // most pages read in all, empty ones included
    /// <summary>Set to supply your own key instead of the one found automatically.</summary>
    public string? FixedClientId { get; set; } = Environment.GetEnvironmentVariable("SOUNDCLOUD_CLIENT_ID");

    static readonly Regex ScriptRe = new(@"https://a-v2\.sndcdn\.com/assets/[A-Za-z0-9._-]+\.js");
    static readonly Regex ClientIdRe = new(@"client_id[""']?\s*[:=]\s*[""']([A-Za-z0-9]{32})[""']");
    static readonly HashSet<string> Reserved = new() { "discover", "stream", "search", "you", "upload", "charts", "pages", "settings" };

    public (string Handle, string Url) Normalize(string text)
    {
        text = (text ?? "").Trim().TrimEnd('/');
        if (text.Length == 0) throw new SourceException("Enter a SoundCloud username or address.");
        string handle;
        if (text.Contains("soundcloud.com"))
        {
            if (!text.Contains("://")) text = "https://" + text;
            string path;
            try { path = new Uri(text).AbsolutePath.Trim('/'); }
            catch (UriFormatException) { throw new SourceException("That doesn't look like a SoundCloud profile."); }
            handle = path.Length > 0 ? Uri.UnescapeDataString(path.Split('/')[0]) : "";
        }
        else handle = text.TrimStart('@');
        handle = handle.ToLowerInvariant();
        if (!Regex.IsMatch(handle, "^[a-z0-9_-]+$") || Reserved.Contains(handle))
            throw new SourceException("That doesn't look like a SoundCloud profile.");
        return (handle, $"https://soundcloud.com/{handle}");
    }

    static string? Date(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        return Http.IsoFromIso(text.Replace('/', '-'));
    }

    static string? Big(string? url) => url?.Replace("-large.", "-t500x500.");

    public static List<Post> ParseTracks(JsonNode? payload)
    {
        var posts = new List<Post>();
        if (payload?["collection"] is not JsonArray items) return posts;
        foreach (var node in items)
        {
            if (node is not JsonObject t || string.IsNullOrEmpty(Html.AsString(t["permalink_url"]))) continue;
            if ((Html.AsString(t["kind"]) ?? "track") != "track") continue;
            var user = t["user"] as JsonObject ?? new JsonObject();
            var pub = t["publisher_metadata"] as JsonObject ?? new JsonObject();
            var ms = Html.AsNumber(Html.Or(t["full_duration"], t["duration"]));
            var title = Html.AsString(t["title"]);
            if (string.IsNullOrEmpty(title)) title = "Untitled";
            var id = Html.Truthy(t["id"]) ? Html.Str(t["id"]) : null;
            var label = (Html.AsString(t["label_name"]) ?? "").Trim();
            var release = (Html.AsString(Html.Or(pub["album_title"], pub["release_title"])) ?? "").Trim();
            posts.Add(new Post
            {
                ExternalId = id ?? Html.AsString(t["permalink_url"])!,
                Title = title,
                Url = Html.AsString(t["permalink_url"])!,
                Image = Big(Html.AsString(Html.Or(t["artwork_url"], user["avatar_url"]))),
                Kind = "track",
                Author = Html.AsString(user["username"]),
                PublishedAt = Date(Html.AsString(Html.Or(t["display_date"], t["created_at"]))),
                PostedAt = Date(Html.AsString(t["created_at"])),
                Label = label.Length > 0 ? label : null,
                Release = release.Length > 0 ? release : null,
                Tracks = new List<Track> { new(title, ms is > 0 ? ms / 1000 : null, ExtId: id) },
            });
        }
        return posts;
    }

    async Task<string> ClientIdAsync(HttpClient client, bool force, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(FixedClientId)) return FixedClientId;
        var cached = db.KvGet("soundcloud_client_id");
        if (cached != null && !force) return cached;
        using var home = await client.GetAsync("https://soundcloud.com/", ct);
        Http.EnsureOk(home);
        var html = await home.Content.ReadAsStringAsync(ct);
        foreach (var script in ScriptRe.Matches(html).Select(m => m.Value).Reverse())
        {
            using var js = await client.GetAsync(script, ct);
            if (js.StatusCode != HttpStatusCode.OK) continue;
            var found = ClientIdRe.Match(await js.Content.ReadAsStringAsync(ct));
            if (found.Success)
            {
                db.KvSet("soundcloud_client_id", found.Groups[1].Value);
                return found.Groups[1].Value;
            }
        }
        throw new SourceException("Couldn't get a SoundCloud key from soundcloud.com (set SOUNDCLOUD_CLIENT_ID to supply one).");
    }

    static Dictionary<string, string> QueryOf(Uri uri)
    {
        var d = new Dictionary<string, string>();
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            d[Uri.UnescapeDataString(kv[0].Replace('+', ' '))] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return d;
    }

    /// <summary>One request to the API. `path` may be a full next_href, whose own query (cursor etc.) is kept.</summary>
    public async Task<JsonNode?> ApiAsync(HttpClient client, string path, Dictionary<string, string> args, CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var cid = await ClientIdAsync(client, attempt > 0, ct);
            var query = new Dictionary<string, string>();
            string target = path;
            if (path.StartsWith("http"))
            {
                var uri = new Uri(path);
                foreach (var kv in QueryOf(uri)) query[kv.Key] = kv.Value;
                target = uri.GetLeftPart(UriPartial.Path);
                if (target.StartsWith(Api)) target = target[Api.Length..];
            }
            foreach (var kv in args) query[kv.Key] = kv.Value;
            query["client_id"] = cid;
            using var req = new HttpRequestMessage(HttpMethod.Get, Http.Query(Api + target, query));
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.TryAddWithoutValidation("Origin", "https://soundcloud.com");
            req.Headers.Referrer = new Uri("https://soundcloud.com/");
            using var r = await client.SendAsync(req, ct);
            if ((r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && attempt == 0 && string.IsNullOrEmpty(FixedClientId))
                continue;      // key expired: fetch a fresh one and retry once
            if (r.StatusCode == HttpStatusCode.NotFound) throw new SourceException("SoundCloud profile not found (check the username).");
            if (r.StatusCode == HttpStatusCode.TooManyRequests)
                throw new SourceException("SoundCloud is rate-limiting this address; it will retry next refresh.");
            Http.EnsureOk(r);
            return Html.ParseJson(await r.Content.ReadAsStringAsync(ct));
        }
        return null;
    }

    static string NextNote(JsonNode? data)
    {
        var href = Html.AsString(data?["next_href"]);
        if (string.IsNullOrEmpty(href)) return "";
        var args = QueryOf(new Uri(href)).Where(kv => kv.Key is not ("client_id" or "limit" or "linked_partitioning")).ToList();
        return ", more pages" + (args.Count > 0 ? $" (next from {string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"))})" : "");
    }

    /// <summary>SoundCloud sometimes lists a track as just its number: look those up so they aren't dropped.</summary>
    async Task<JsonObject> FilledAsync(HttpClient client, List<JsonNode?> items, FetchResult result, string where, CancellationToken ct)
    {
        var full = new JsonArray();
        foreach (var t in items)
            if (t is JsonObject o && !string.IsNullOrEmpty(Html.AsString(o["permalink_url"]))) full.Add(o.DeepClone());
        var bare = items.OfType<JsonObject>()
            .Where(t => string.IsNullOrEmpty(Html.AsString(t["permalink_url"])) && Html.Truthy(t["id"])
                        && (Html.AsString(t["kind"]) ?? "track") == "track")
            .Select(t => Html.Str(t["id"])!).ToList();
        if (bare.Count > 0)
        {
            var got = new List<JsonNode?>();
            for (int i = 0; i < bare.Count; i += 50)
            {
                var found = await ApiAsync(client, "/tracks", new() { ["ids"] = string.Join(",", bare.Skip(i).Take(50)) }, ct);
                if (found is JsonArray arr) got.AddRange(arr);
                else if (found?["collection"] is JsonArray coll) got.AddRange(coll);
            }
            foreach (var t in got)
                if (t is JsonObject o && !string.IsNullOrEmpty(Html.AsString(o["permalink_url"]))) full.Add(o.DeepClone());
            result.Trace.Add($"{where}: {bare.Count} listed without details, {got.Count} of them looked up");
        }
        return new JsonObject { ["collection"] = full };
    }

    static List<JsonNode?> Items(JsonNode? data) => (data?["collection"] as JsonArray)?.ToList() ?? new List<JsonNode?>();

    public async Task<FetchResult> FetchAsync(HttpClient client, Account account, HashSet<string> knownIds, CancellationToken ct = default)
    {
        var meta = account.Meta;
        var result = new FetchResult();
        if (!Html.Truthy(meta["user_id"]))
        {
            var user = await ApiAsync(client, "/resolve", new() { ["url"] = account.Url }, ct);
            if (Html.AsString(user?["kind"]) != "user" || !Html.Truthy(user?["id"]))
                throw new SourceException("That address is not a SoundCloud profile.");
            meta["user_id"] = user!["id"]!.DeepClone();
            result.DisplayName = Html.AsString(user["username"]);
            result.Avatar = Big(Html.AsString(user["avatar_url"]));
        }
        var uid = Html.Str(meta["user_id"])!;
        var seen = new HashSet<string>();
        var posts = new List<Post>();
        void Add(List<Post> found)
        {
            foreach (var p in found)
                if (seen.Add(p.ExternalId)) posts.Add(p);
        }
        var lim = Limit.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // The Tracks list. A page can come back short, or empty, with more after it; empty pages are
        // cheap, so more of them are followed than full ones.
        var data = await ApiAsync(client, $"/users/{uid}/tracks", new() { ["limit"] = lim, ["linked_partitioning"] = "1" }, ct);
        Add(ParseTracks(await FilledAsync(client, Items(data), result, "Tracks list", ct)));
        var shortList = Html.Truthy(data?["next_href"]);
        result.Trace.Add($"Tracks list: {Items(data).Count} items" + NextNote(data));
        // Newest first, so once a page holds a track already saved, everything after it is known too.
        bool Known(JsonNode? d) => Items(d).Any(t => knownIds.Contains(Html.Str(t?["id"]) ?? ""));
        var reached = Known(data);
        int pages = 1, full = Items(data).Count > 0 ? 1 : 0;
        while (posts.Count < Limit && !reached && Html.Truthy(data?["next_href"]) && full < MaxPages && pages < MaxEmptyPages)
        {
            data = await ApiAsync(client, Html.AsString(data!["next_href"])!, new(), ct);
            pages++;
            if (Items(data).Count > 0) full++;
            Add(ParseTracks(await FilledAsync(client, Items(data), result, $"Tracks list page {pages}", ct)));
            result.Trace.Add($"Tracks list page {pages}: {Items(data).Count} items" + NextNote(data));
            reached = Known(data);
        }
        if (reached) result.Trace.Add("Reached tracks already saved: nothing older to look for");
        var extra = posts.Count < Limit && (shortList || posts.Count == 0) && !reached;
        if (extra && posts.Count > 0 && !Html.Truthy(data?["next_href"]))
        {
            // The whole list was read: look further only if SoundCloud says there are more tracks.
            long count;
            try { count = (long)(Html.AsNumber((await ApiAsync(client, $"/users/{uid}", new(), ct))?["track_count"]) ?? 0); }
            catch (Exception e) when (e is SourceException or HttpRequestException or Http.StatusException) { count = Limit; }
            extra = posts.Count < count;
            result.Trace.Add($"Tracks list read to the end: {posts.Count} of the {count} tracks the profile shows");
        }
        if (extra)
        {
            // The profile's "All" tab: its uploads, and reposts (kept only when they are its own tracks).
            JsonNode? stream = null;
            int spages = 0;
            while (posts.Count < Limit && spages < MaxPages && (spages == 0 || Html.Truthy(stream?["next_href"])))
            {
                stream = spages == 0
                    ? await ApiAsync(client, $"/stream/users/{uid}", new() { ["limit"] = lim, ["linked_partitioning"] = "1" }, ct)
                    : await ApiAsync(client, Html.AsString(stream!["next_href"])!, new(), ct);
                var items = Items(stream);
                var own = items.OfType<JsonObject>().Where(i => i["track"] is JsonObject tr && (
                        Html.AsString(i["type"]) == "track" ||
                        (Html.Str(Html.Or((tr["user"] as JsonObject)?["id"], tr["user_id"])) ?? "") == uid))
                    .Select(i => (JsonNode?)i["track"]).ToList();
                var kinds = new List<(string, int)>();
                foreach (var it in items)
                {
                    var k = Html.AsString(it?["type"]);
                    if (string.IsNullOrEmpty(k)) k = "?";
                    var idx = kinds.FindIndex(x => x.Item1 == k);
                    if (idx < 0) kinds.Add((k, 1)); else kinds[idx] = (k, kinds[idx].Item2 + 1);
                }
                var before = posts.Count;
                spages++;
                Add(ParseTracks(await FilledAsync(client, own, result, $"All tab page {spages}", ct)));
                result.Trace.Add($"All tab page {spages}: {items.Count} items " +
                                 $"({(kinds.Count > 0 ? string.Join(", ", kinds.Select(k => $"{k.Item2} {k.Item1}")) : "none")}), " +
                                 $"{own.Count} of them its own tracks, {posts.Count - before} not already found");
            }
        }
        if (extra && posts.Count < Limit)
        {
            // The "Popular tracks" tab: its most played uploads, old ones included.
            JsonNode? top = null;
            try { top = await ApiAsync(client, $"/users/{uid}/toptracks", new() { ["limit"] = "50" }, ct); }
            catch (Exception e) when (e is SourceException or HttpRequestException or Http.StatusException)
            {
                result.Trace.Add($"Popular tracks: couldn't read ({e.Message})");
            }
            var items = Items(top);
            var before = posts.Count;
            Add(ParseTracks(await FilledAsync(client, items, result, "Popular tracks", ct)));
            result.Trace.Add($"Popular tracks: {items.Count} items, {posts.Count - before} not already found");
        }
        result.Posts = posts;
        if (posts.Count == 0)
        {
            var user = await ApiAsync(client, $"/users/{uid}", new(), ct);
            var count = (long)(Html.AsNumber(user?["track_count"]) ?? 0);
            result.Trace.Add($"Profile ({Html.AsString(user?["permalink"])}, id {uid}) says it has {count} tracks");
            if (count > 0)
                result.Warning = $"SoundCloud says this profile has {count} tracks but didn't list any; it will try again next refresh.";
        }
        if (posts.Count > 0 && result.DisplayName == null) result.DisplayName = posts[0].Author;
        return result;
    }
}
