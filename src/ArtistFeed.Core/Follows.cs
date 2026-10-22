using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArtistFeed.Core;

/// <summary>One profile, artist or label an account follows.</summary>
public sealed record Followed(string Name, string Handle, string Url, bool Tracked);

public sealed class FollowsException(string message) : Exception(message);

/// <summary>
/// Who an account follows, for adding them to Tracked Artists in one go (as in the web version).
/// SoundCloud lists the profiles any account follows. Bandcamp lists the artists and labels a fan
/// follows when the fan's page is public. YouTube keeps subscriptions private, so it isn't offered.
/// Nothing is saved here: Tracked Artists shows the list, and you choose what to add and save.
/// </summary>
public sealed class Follows(Engine engine)
{
    public const string BandcampFollowingApi = "https://bandcamp.com/api/fancollection/1/following_bands";
    const int Max = 5000;

    /// <summary>Usernames already given (in the web version's Settings and Link Finder, for a copied database).</summary>
    public Dictionary<string, string> Defaults() => new()
    {
        ["soundcloud"] = (engine.Db.KvGet("soundcloud_me") ?? "").Trim(),
        ["bandcamp"] = (engine.Db.KvGet("bandcamp_fan") ?? "").Trim(),
    };

    public async Task<List<Followed>> ReadAsync(string site, string user, CancellationToken ct = default)
    {
        var found = site switch
        {
            "soundcloud" => await SoundCloudAsync(user, ct),
            "bandcamp" => await BandcampAsync(user, ct),
            _ => throw new FollowsException("Follows can be read from SoundCloud or Bandcamp."),
        };
        var tracked = engine.Db.Query("SELECT handle FROM accounts WHERE source=?", site).Select(r => (string)r["handle"]!).ToHashSet();
        return found.Select(f => f with { Tracked = tracked.Contains(f.Handle) }).ToList();
    }

    static string FirstPathPart(string text, string host)
    {
        text = text.Trim().TrimEnd('/');
        if (text.Contains(host, StringComparison.OrdinalIgnoreCase))
        {
            if (!text.Contains("://")) text = "https://" + text;
            if (Uri.TryCreate(text, UriKind.Absolute, out var u)) text = u.AbsolutePath.Trim('/').Split('/')[0];
        }
        return text;
    }

    public async Task<List<Followed>> SoundCloudAsync(string user, CancellationToken ct = default)
    {
        var me = FirstPathPart(user ?? "", "soundcloud.com").TrimStart('@').ToLowerInvariant();
        if (!Regex.IsMatch(me, "^[a-z0-9_-]{1,100}$")) throw new FollowsException("That doesn't look like a SoundCloud username.");
        var sc = engine.SoundCloud;
        var output = new List<Followed>();
        using var client = engine.MakeClient();
        JsonNode? account;
        try { account = await sc.ApiAsync(client, "/resolve", new() { ["url"] = $"https://soundcloud.com/{me}" }, ct); }
        catch (SourceException) { throw new FollowsException($"No SoundCloud account \"{me}\" was found."); }
        if (Html.AsString(account?["kind"]) != "user" || !Html.Truthy(account?["id"]))
            throw new FollowsException($"\"{me}\" is not a SoundCloud profile.");
        string path = $"/users/{Html.Str(account!["id"])}/followings";
        var args = new Dictionary<string, string> { ["limit"] = "200", ["linked_partitioning"] = "1" };
        while (output.Count < Max)
        {
            var data = await sc.ApiAsync(client, path, args, ct);
            foreach (var u in (data?["collection"] as JsonArray) ?? new JsonArray())
            {
                var handle = (Html.AsString(u?["permalink"]) ?? "").ToLowerInvariant();
                if (handle.Length == 0 || !Regex.IsMatch(handle, "^[a-z0-9_-]+$")) continue;
                var name = Html.AsString(u?["username"]);
                output.Add(new Followed(string.IsNullOrWhiteSpace(name) ? handle : name.Trim(), handle, $"https://soundcloud.com/{handle}", false));
            }
            var next = Html.AsString(data?["next_href"]);
            if (string.IsNullOrEmpty(next)) break;
            path = next;                        // a full next_href: its own cursor is kept
            args = new();
        }
        return output;
    }

    Followed? Band(JsonNode? item)
    {
        if (item is not JsonObject o) return null;
        var hints = o["url_hints"] as JsonObject;
        var url = Html.AsString(o["url"]) ?? Html.AsString(o["band_url"]) ?? "";
        if (Html.AsString(hints?["custom_domain"]) is { Length: > 0 } custom) url = "https://" + custom;
        else if (Html.AsString(hints?["subdomain"]) is { Length: > 0 } sub) url = $"https://{sub}.bandcamp.com";
        if (url.Length == 0) return null;
        try
        {
            var (handle, page) = engine.Bandcamp.Normalize(url);
            var name = Html.AsString(o["name"]) ?? Html.AsString(o["band_name"]);
            return new Followed(string.IsNullOrWhiteSpace(name) ? handle : name.Trim(), handle, page, false);
        }
        catch (SourceException) { return null; }
    }

    static JsonArray BandList(JsonNode? data)
    {
        foreach (var key in new[] { "followeers", "followeds", "following_bands", "items", "bands" })
            if (data?[key] is JsonArray arr) return arr;
        return new JsonArray();
    }

    async Task<HttpResponseMessage> AskAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        await engine.Pace.WaitAsync(background: false, ct);
        var r = await client.SendAsync(request, ct);
        if (r.StatusCode == HttpStatusCode.TooManyRequests)
        {
            engine.Pace.SlowDown(engine.Pace.PauseFor(r, 0));
            throw new FollowsException("Bandcamp is asking the app to slow down. Try again in a few minutes.");
        }
        return r;
    }

    public async Task<List<Followed>> BandcampAsync(string fan, CancellationToken ct = default)
    {
        fan = FirstPathPart(fan ?? "", "bandcamp.com/");
        if (!Regex.IsMatch(fan, "^[A-Za-z0-9_.-]{1,100}$")) throw new FollowsException("That doesn't look like a Bandcamp username.");
        var output = new List<Followed>();
        var seen = new HashSet<string>();
        void AddAll(IEnumerable<JsonNode?> items)
        {
            foreach (var it in items)
                if (Band(it) is { } b && seen.Add(b.Handle)) output.Add(b);
        }
        using var hold = engine.Pace.Foreground();      // the feed refresh stands aside meanwhile
        using var client = engine.MakeClient();
        using var page = await AskAsync(client, new HttpRequestMessage(HttpMethod.Get, $"https://bandcamp.com/{Uri.EscapeDataString(fan)}"), ct);
        if (page.StatusCode == HttpStatusCode.NotFound) throw new FollowsException($"No Bandcamp fan page called \"{fan}\".");
        if (!page.IsSuccessStatusCode) throw new FollowsException($"Bandcamp answered {(int)page.StatusCode} for that fan page.");
        var doc = Html.Parse(await page.Content.ReadAsStringAsync(ct));
        var blob = Html.ParseJson(doc.QuerySelector("#pagedata[data-blob]")?.GetAttribute("data-blob")) as JsonObject ?? new JsonObject();
        var fanId = blob["fan_data"]?["fan_id"];
        if (!Html.Truthy(fanId)) throw new FollowsException("Couldn't read that fan page (is it public?).");
        var info = blob["following_bands_data"] as JsonObject;
        var cache = blob["item_cache"]?["following_bands"] as JsonObject;
        if (cache != null)
        {
            var order = (info?["sequence"] as JsonArray)?.Select(Html.Str).Where(k => k != null).Select(k => k!).ToList()
                        ?? cache.Select(kv => kv.Key).ToList();
            AddAll(order.Where(cache.ContainsKey).Select(k => cache[k]));
        }
        var token = cache != null || info != null ? Html.AsString(info?["last_token"]) : "9999999999:9999999999";
        var asked = new HashSet<string>();
        while (!string.IsNullOrEmpty(token) && asked.Add(token) && output.Count < Max)
        {
            var body = new JsonObject { ["fan_id"] = fanId!.DeepClone(), ["older_than_token"] = token, ["count"] = 100 };
            using var req = new HttpRequestMessage(HttpMethod.Post, BandcampFollowingApi)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var r = await AskAsync(client, req, ct);
            if (!r.IsSuccessStatusCode)
            {
                if (output.Count > 0) break;           // keep what was read
                throw new FollowsException($"Bandcamp answered {(int)r.StatusCode} while reading who {fan} follows.");
            }
            var data = Html.ParseJson(await r.Content.ReadAsStringAsync(ct));
            var items = BandList(data);
            AddAll(items);
            token = data?["more_available"]?.GetValueKind() == System.Text.Json.JsonValueKind.True && items.Count > 0
                ? Html.AsString(data?["last_token"]) : null;
        }
        return output;
    }
}
