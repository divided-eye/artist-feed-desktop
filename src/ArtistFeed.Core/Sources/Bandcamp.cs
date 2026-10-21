using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace ArtistFeed.Core;

/// <summary>
/// Bandcamp: read an artist's public /music page, then each new release page. Bandcamp has no
/// public API or artist feed, and the /music grid lists releases without dates, so each release
/// not seen before is opened once to read its date and track list from the page's embedded data.
/// </summary>
public sealed class Bandcamp(Db db, Pace pace) : ISource
{
    public string Key => "bandcamp";
    public string Label => "Bandcamp";
    public string Color => "#1da0c3";
    public string Placeholder => "artistname or https://artistname.bandcamp.com";

    /// <summary>Release pages opened per page per refresh; a long back catalogue fills in over later refreshes.</summary>
    public int MaxNew { get; set; } = 6;
    /// <summary>A release page that failed this often no longer takes a slot ahead of unread ones.</summary>
    public const int GiveWayAfter = 3;
    public TimeSpan Pause { get; set; } = TimeSpan.Zero;

    public string Short(string handle) => handle.EndsWith(".bandcamp.com") ? handle[..^".bandcamp.com".Length] : handle;

    public (string Handle, string Url) Normalize(string text)
    {
        text = (text ?? "").Trim().TrimEnd('/');
        if (text.Length == 0) throw new SourceException("Enter a Bandcamp name or address.");
        if (!text.Contains("://")) text = "https://" + (text.Contains('.') ? text : $"{text}.bandcamp.com");
        string host;
        try { host = new Uri(text).Host.ToLowerInvariant(); }
        catch (UriFormatException) { throw new SourceException("That doesn't look like a Bandcamp address."); }
        if (host.Length == 0 || host.Contains(' ')) throw new SourceException("That doesn't look like a Bandcamp address.");
        if (host is "bandcamp.com" or "www.bandcamp.com")
            throw new SourceException("Use the artist's own page, e.g. artistname.bandcamp.com.");
        return (host, $"https://{host}");
    }

    // ---------- reading pages ----------

    public static string Clean(string url) => url.Split('?')[0].Split('#')[0].TrimEnd('/');

    static string KindOf(string url) => url.Contains("/track/") ? "track" : "album";

    static readonly string[] DateFormats =
    {
        "dd MMM yyyy HH:mm:ss 'GMT'", "d MMM yyyy HH:mm:ss 'GMT'", "ddd, dd MMM yyyy HH:mm:ss 'GMT'", "ddd, d MMM yyyy HH:mm:ss 'GMT'",
        "dd MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm:ss zzz", "ddd, dd MMM yyyy HH:mm:ss zzz", "ddd, d MMM yyyy HH:mm:ss zzz",
        "dd MMM yyyy HH:mm 'GMT'", "dd MMM yyyy",
    };

    /// <summary>"07 Mar 2025 00:00:00 GMT", or an ISO date.</summary>
    public static string? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        var zoned = Regex.Replace(t, @"([+-]\d{2})(\d{2})$", "$1:$2");          // "+0000" -> "+00:00"
        if (DateTimeOffset.TryParseExact(zoned, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var dt))
            return Http.Iso(dt);
        return Http.IsoFromIso(t);
    }

    static readonly Regex IsoDuration = new(@"^P(?:(\d+)D)?T?(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?$");

    /// <summary>Seconds from a number or an ISO 8601 duration such as "P00H03M45S".</summary>
    public static double? Seconds(JsonNode? value)
    {
        var n = Html.AsNumber(value);
        if (n != null) return n > 0 ? n : null;
        var m = IsoDuration.Match(Html.AsString(value) ?? "");
        if (!m.Success || !Enumerable.Range(1, 4).Any(i => m.Groups[i].Success)) return null;
        double G(int i) => m.Groups[i].Success ? double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        var total = G(1) * 86400 + G(2) * 3600 + G(3) * 60 + G(4);
        return total == 0 ? null : total;
    }

    static string? NameOf(JsonNode? thing) => thing is JsonObject o ? Html.AsString(o["name"]) : null;

    static JsonObject JsonAttr(IDocument doc, string attr)
    {
        var holder = doc.QuerySelector($"[{attr}]");
        return Html.ParseJson(holder?.GetAttribute(attr)) as JsonObject ?? new JsonObject();
    }

    public sealed record Profile(string? Name, string? Avatar);

    public static Profile ParseProfile(string html)
    {
        var doc = Html.Parse(html);
        var name = Html.Meta(doc, "og:site_name");
        if (name == null)
        {
            var el = doc.QuerySelector("#band-name-location .title");
            name = el != null ? Html.Text(el, "") : null;
        }
        var photo = doc.QuerySelector("img.band-photo");
        return new Profile(name, photo?.GetAttribute("src"));
    }

    public sealed record GridItem(string Url, string Title, string? Image, string? Author);

    /// <summary>Releases listed on /music, in page order.</summary>
    public static List<GridItem> ParseMusicGrid(string html, string baseUrl)
    {
        var doc = Html.Parse(html);
        var grid = doc.QuerySelector("#music-grid");
        if (grid == null) return new();
        var items = new List<GridItem>();
        var seen = new HashSet<string>();

        void Add(string url, string? title, string? image, string? author)
        {
            url = Clean(Html.Join(baseUrl + "/", url));
            if (seen.Contains(url) || !Regex.IsMatch(url, "/(album|track)/")) return;
            seen.Add(url);
            items.Add(new GridItem(url, string.IsNullOrEmpty(title) ? url[(url.LastIndexOf('/') + 1)..] : title, image, author));
        }

        foreach (var li in grid.QuerySelectorAll("li"))
        {
            var a = li.QuerySelector("a[href]");
            if (a == null) continue;
            string? title = null, author = null;
            var t = li.QuerySelector(".title");
            if (t != null)
            {
                var copy = (IElement)t.Clone(true);
                var over = copy.QuerySelector(".artist-override");
                if (over != null)
                {
                    author = Html.Text(over);
                    over.Remove();
                }
                title = Html.Text(copy);
            }
            var img = li.QuerySelector("img");
            string? image = null;
            if (img != null)
            {
                image = img.GetAttribute("data-original");
                if (string.IsNullOrEmpty(image)) image = img.GetAttribute("src");
                if (image != null && image.EndsWith("0.gif")) image = null;
            }
            Add(a.GetAttribute("href")!, title, image, author);
        }
        // Big discographies keep the rest of the grid in a JSON attribute.
        if (Html.ParseJson(grid.GetAttribute("data-client-items")) is JsonArray more)
        {
            foreach (var it in more.OfType<JsonObject>())
            {
                var art = Html.Str(it["art_id"]);
                var image = Html.Truthy(it["art_id"]) ? $"https://f4.bcbits.com/img/a{art}_2.jpg" : null;
                var page = Html.AsString(it["page_url"]);
                if (!string.IsNullOrEmpty(page)) Add(page, Html.AsString(it["title"]), image, Html.AsString(it["artist"]));
            }
        }
        return items;
    }

    // A line of a written-out track list: "1. Serpentine", "02 - High Priest (5:41)", "A1: Disclosed".
    static readonly Regex ListLine = new(@"^\s*(?:(\d{1,2})|([A-Ha-h]{1,2}\d{1,2}|AA|BB))\s*[.)\]:\-–—]\s*(\S.{0,150}?)\s*$");
    static readonly Regex TrailingTime = new(@"[\s\-–—]*[\(\[]?\d{1,2}:\d{2}[\)\]]?\s*$");

    /// <summary>Track names written out as a numbered list in free text (at least two consecutive lines).</summary>
    public static List<string> ListedTitles(IEnumerable<string> texts)
    {
        var found = new List<string>();
        void Keep(List<string> run)
        {
            if (run.Count >= 2) foreach (var t in run) if (!found.Contains(t)) found.Add(t);
        }
        foreach (var text in texts)
        {
            var run = new List<string>();
            int? expect = null;
            bool sides = false;
            foreach (var line in Regex.Split(text ?? "", "[\r\n]+"))
            {
                var m = ListLine.Match(line);
                var title = m.Success ? TrailingTime.Replace(m.Groups[3].Value, "").Trim(' ', '-', '–', '—') : "";
                if (!m.Success || title.Length == 0 || title.Contains("http") || title.Length > 120)
                {
                    Keep(run);
                    run = new();
                    expect = null;
                    sides = false;
                }
                else if (m.Groups[1].Success)
                {
                    int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (run.Count > 0 && !sides && expect <= n && n <= expect + 2) run.Add(title);   // a skipped line or two is tolerated
                    else
                    {
                        Keep(run);
                        run = n is 0 or 1 ? new List<string> { title } : new List<string>();
                        sides = false;
                    }
                    expect = n + 1;
                }
                else
                {
                    if (!(run.Count > 0 && sides))
                    {
                        Keep(run);
                        run = new();
                    }
                    run.Add(title);
                    sides = true;
                    expect = null;
                }
            }
            Keep(run);
        }
        return found;
    }

    public sealed class Release
    {
        public string Url = "";
        public string? Title, Image, Author, PublishedAt, Label, ReleaseName, Embed, PostedAt, ReleaseUrl;
        public bool IsRelease;
        public List<Track>? Tracks;
        public List<string> ListedTracks = new();
        /// <summary>Shown here but sold from this other page (usually the label's): see SoldElsewhere.</summary>
        public string? SoldAt;
        /// <summary>No tracks and nothing physical either: shown here, but it can't be bought here.</summary>
        public bool Placeholder;
    }

    public const string NotForSale = "Not for sale on this page";

    /// <summary>Album results of a bandcamp.com/search page: url, name, artist.</summary>
    public static List<(string Url, string Name, string Artist)> ParseSearchAlbums(string html)
    {
        var doc = Html.Parse(html);
        string Text(IElement? e) => e == null ? "" : Regex.Replace(e.TextContent, @"\s+", " ").Trim();
        var found = new List<(string, string, string)>();
        foreach (var li in doc.QuerySelectorAll("li.searchresult"))
        {
            var info = li.QuerySelector(".result-info") ?? li;
            var heading = info.QuerySelector(".heading a");
            var url = Text(info.QuerySelector(".itemurl"));
            if (url == "") url = heading?.GetAttribute("href") ?? "";
            if (heading == null || url == "") continue;
            if (!url.Contains("://")) url = "https://" + url;
            var type = Text(info.QuerySelector(".itemtype")).ToLowerInvariant();
            if (type is not ("album" or "")) continue;
            var sub = Text(info.QuerySelector(".subhead"));
            var by = sub.LastIndexOf(" by ", StringComparison.Ordinal);
            var artist = sub.StartsWith("by ") ? sub[3..] : sub.StartsWith("from ") && by >= 0 ? sub[(by + 4)..] : "";
            found.Add((Clean(url), Text(heading), artist.Trim()));
        }
        return found;
    }

    static bool SameWords(string? a, string? b)
    {
        var x = Names.Norm(a ?? "");
        var y = Names.Norm(b ?? "");
        return x != "" && y != "" && (x == y || x.Contains(y) || y.Contains(x));
    }

    /// <summary>
    /// A release with no tracks and nothing to buy that doesn't say where it is sold: one Bandcamp search
    /// for the same title and artist, on another page. Returns that page, or null.
    /// </summary>
    async Task<string?> SellerAsync(HttpClient client, string url, string? title, string? artist, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(title)) return null;
        string html;
        try
        {
            var q = Uri.EscapeDataString($"{artist ?? ""} {title}".Trim());
            (html, _) = await GetAsync(client, $"https://bandcamp.com/search?q={q}&item_type=a", ct);
        }
        catch (Pace.BusyException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
        var here = new Uri(url).Host;
        foreach (var (u, name, by) in ParseSearchAlbums(html))
            if (Uri.TryCreate(u, UriKind.Absolute, out var there) && !string.Equals(there.Host, here, StringComparison.OrdinalIgnoreCase)
                && SameWords(name, title) && (string.IsNullOrEmpty(artist) || SameWords(by, artist)))
                return u;
        return null;
    }

    /// <summary>
    /// A release shown on an artist's page but sold from another page (usually its label's). Bandcamp
    /// gives such a page no tracks and nothing to buy, and points to the real one in a &lt;link&gt; in its
    /// head: the canonical address, or the same /album/ path on the other page.
    /// </summary>
    static string? SoldElsewhere(IDocument doc, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var here)) return null;
        foreach (var tag in doc.QuerySelectorAll("link[href]"))
        {
            var href = Clean(tag.GetAttribute("href") ?? "");
            if (!Uri.TryCreate(href, UriKind.Absolute, out var there) || there.Scheme is not ("http" or "https")
                || string.Equals(there.Host, here.Host, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Regex.IsMatch(there.AbsolutePath, @"^/(album|track)/[^/]+$")) continue;
            var rels = (tag.GetAttribute("rel") ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rels.Contains("canonical") || there.AbsolutePath == here.AbsolutePath.TrimEnd('/')) return href;
        }
        return null;
    }

    static string Squash(string s) => Regex.Replace(s, @"\W+", "").ToLowerInvariant();

    /// <summary>Title, date, cover, artist, label, track list and player id from a single album/track page.</summary>
    public static Release ParseRelease(string html, string url)
    {
        var doc = Html.Parse(html);
        var outp = new Release { Url = Clean(url) };
        var ldTracks = new List<Track>();
        string? publisher = null;
        var texts = new List<string>();

        foreach (var script in doc.QuerySelectorAll("script[type=\"application/ld+json\"]"))
        {
            if (Html.ParseJson(script.TextContent) is not JsonObject ld) continue;
            Html.Strings(ld, texts);
            var type = Html.AsString(ld["@type"]);
            if (type is "MusicAlbum" or "MusicRecording") outp.IsRelease = true;
            outp.Title = Html.Truthy(ld["name"]) ? Html.AsString(ld["name"]) : outp.Title;
            outp.PublishedAt = ParseDate(Html.AsString(ld["datePublished"])) ?? outp.PublishedAt;
            var image = ld["image"];
            if (image is JsonArray arr) image = arr.Count > 0 ? arr[0] : null;
            if (Html.Truthy(image)) outp.Image = Html.AsString(image) ?? outp.Image;
            if (ld["byArtist"] is JsonObject by) outp.Author = Html.AsString(by["name"]);
            var id = Html.AsString(ld["@id"]);
            if (id != null && id.StartsWith("http")) outp.Url = Clean(id);
            publisher = NameOf(ld["publisher"]) ?? publisher;
            outp.Label = NameOf(ld["recordLabel"]) ?? outp.Label;
            outp.ReleaseName = NameOf(ld["inAlbum"]) ?? outp.ReleaseName;
            if (ld["inAlbum"] is JsonObject album && Html.AsString(album["@id"]) is { } aid && aid.StartsWith("http"))
                outp.ReleaseUrl = Clean(aid);                         // the album a track page belongs to
            if (ld["track"] is JsonObject listed && listed["itemListElement"] is JsonArray elements)
            {
                foreach (var entry in elements)
                {
                    if (entry is JsonObject e && e["item"] is JsonObject item && Html.Truthy(item["name"]))
                        ldTracks.Add(new Track(Html.AsString(item["name"])!, Seconds(item["duration"]),
                            Url: Html.AsString(item["@id"])));
                }
            }
            if (type == "MusicRecording" && ldTracks.Count == 0 && Html.Truthy(ld["name"]))
                ldTracks.Add(new Track(Html.AsString(ld["name"])!, Seconds(ld["duration"])));
        }

        // The player's own data: track list with durations, and the ids its embed needs.
        var tralbum = JsonAttr(doc, "data-tralbum");
        var current = tralbum["current"] as JsonObject ?? new JsonObject();
        var tracks = new List<Track>();
        if (tralbum["trackinfo"] is JsonArray infos)
        {
            foreach (var t in infos)
            {
                if (t is not JsonObject ti || !Html.Truthy(ti["title"])) continue;
                var ext = Html.Or(ti["track_id"], ti["id"]);
                var link = Html.AsString(ti["title_link"]);
                var artist = Html.AsString(ti["artist"]);
                tracks.Add(new Track(Html.AsString(ti["title"]) ?? Html.Str(ti["title"])!, Seconds(ti["duration"]),
                    string.IsNullOrEmpty(artist) ? null : artist,
                    Html.Truthy(ext) ? Html.Str(ext) : null,
                    !string.IsNullOrEmpty(link) ? Clean(Html.Join(url, link)) : null));
            }
        }
        if (tracks.Count > 0 || ldTracks.Count > 0) outp.Tracks = tracks.Count > 0 ? tracks : ldTracks;
        else
        {
            outp.SoldAt = SoldElsewhere(doc, url);
            outp.Placeholder = tralbum["packages"] is not JsonArray { Count: > 0 };
        }
        // A track page names its album in the player data too, sometimes only there.
        var albumLink = Html.AsString(tralbum["album_url"]);
        if (outp.ReleaseUrl == null && outp.Url.Contains("/track/") && !string.IsNullOrWhiteSpace(albumLink))
            outp.ReleaseUrl = Clean(Html.Join(url, albumLink.Trim()));
        if (outp.ReleaseUrl == null && outp.Url.Contains("/track/"))
        {
            foreach (var a in doc.QuerySelectorAll("#name-section a[href], .albumTitle a[href], .fromAlbum a[href]"))
            {
                if (!a.GetAttribute("href")!.Contains("/album/")) continue;
                outp.ReleaseUrl = Clean(Html.Join(url, a.GetAttribute("href")!));
                var text = Html.Text(a);
                outp.ReleaseName ??= text.Length > 0 ? text : null;
                break;
            }
        }
        // Track names that are only written out in the description.
        Html.Strings(tralbum, texts);
        foreach (var holder in doc.QuerySelectorAll("[data-blob]"))
            Html.Strings(Html.ParseJson(holder.GetAttribute("data-blob")), texts);
        var body = (INode?)doc.Body ?? doc;
        var bodyCopy = body.Clone(true);
        if (bodyCopy is IElement bodyEl)
            foreach (var junk in bodyEl.QuerySelectorAll("script, style, noscript").ToList()) junk.Remove();
        texts.Add(Html.RawText(bodyCopy, "\n"));
        var playable = (outp.Tracks ?? new()).Select(t => Squash(t.Title)).ToHashSet();
        outp.ListedTracks = ListedTitles(texts).Where(t => !playable.Contains(Squash(t))).ToList();
        // When it went up on Bandcamp, which is not always the stated release date.
        outp.PostedAt = ParseDate(Html.AsString(current["publish_date"]))
                        ?? ParseDate(Html.AsString(tralbum["album_publish_date"]))
                        ?? ParseDate(Html.AsString(current["new_date"]));

        var props = Html.ParseJson(doc.QuerySelector("meta[name=\"bc-page-properties\"]")?.GetAttribute("content")) as JsonObject
                    ?? new JsonObject();
        var itemId = Html.Or(props["item_id"], tralbum["id"], current["id"]);
        var itemType = Html.Str(Html.Or(props["item_type"], tralbum["item_type"], current["type"])) ?? "";
        if (Html.Truthy(itemId) && itemType.Length > 0 && itemType[0] is 'a' or 't')
            outp.Embed = $"{(itemType[0] == 'a' ? "album" : "track")}={Html.Str(itemId)}";
        else
        {
            var m = Regex.Match(Html.Meta(doc, "og:video") ?? "", @"EmbeddedPlayer/(?:v=2/)?(album|track)=(\d+)");
            if (m.Success) outp.Embed = $"{m.Groups[1].Value}={m.Groups[2].Value}";
        }
        if (tralbum.Count > 0) outp.IsRelease = true;
        if (outp.PublishedAt == null && doc.QuerySelector("[data-tralbum]") is { } holderEl)
        {
            if (Html.ParseJson(holderEl.GetAttribute("data-tralbum")) is JsonObject tb)
            {
                var cur = tb["current"] as JsonObject;
                outp.PublishedAt = ParseDate(Html.AsString(Html.Or(cur?["release_date"], cur?["publish_date"])));
                outp.IsRelease = true;
            }
        }
        outp.Title ??= Html.Meta(doc, "og:title");
        outp.Image ??= Html.Meta(doc, "og:image");
        if (string.IsNullOrEmpty(outp.Author))
        {
            var a = Html.AsString(tralbum["artist"]);
            outp.Author = string.IsNullOrEmpty(a) ? null : a;
        }
        // A release on someone else's page (a label's) is credited to that page.
        if (outp.Label == null && !string.IsNullOrEmpty(publisher) && !string.IsNullOrEmpty(outp.Author)
            && !string.Equals(publisher.Trim(), outp.Author.Trim(), StringComparison.OrdinalIgnoreCase))
            outp.Label = publisher;
        if (string.IsNullOrEmpty(outp.ReleaseName)) outp.ReleaseName = outp.Title;     // an album, or a stand-alone single
        return outp;
    }

    // ---------- the feed ----------

    async Task<(string Text, string Url)> GetAsync(HttpClient client, string url, CancellationToken ct)
    {
        await pace.WaitAsync(background: true, ct);           // the feed stands aside for lookups and pauses
        using var r = await client.GetAsync(url, ct);
        if (r.StatusCode == HttpStatusCode.TooManyRequests)
        {
            pace.SlowDown(pace.PauseFor(r, 0));
            throw new Pace.BusyException();                  // stop this round; the next refresh picks up from here
        }
        if (r.StatusCode == HttpStatusCode.NotFound) throw new SourceException("Bandcamp page not found (check the name).");
        Http.EnsureOk(r);
        var text = await r.Content.ReadAsStringAsync(ct);
        return (text, r.RequestMessage?.RequestUri?.ToString() ?? url);
    }

    public async Task<FetchResult> FetchAsync(HttpClient client, Account account, HashSet<string> knownIds, CancellationToken ct = default)
    {
        var baseUrl = account.Url;
        var (html, finalUrl) = await GetAsync(client, baseUrl + "/music", ct);
        var profile = ParseProfile(html);
        var result = new FetchResult { DisplayName = profile.Name, Avatar = profile.Avatar };
        var items = ParseMusicGrid(html, baseUrl);

        if (items.Count == 0)
        {
            // Artists with a single release: /music shows that release directly.
            var rel = ParseRelease(html, finalUrl);
            if (!rel.IsRelease) throw new SourceException("No releases found on this Bandcamp page.");
            result.Posts.Add(new Post
            {
                ExternalId = rel.Url, Title = rel.Title ?? "Untitled", Url = rel.Url, Image = rel.Image, Kind = KindOf(rel.Url),
                Author = rel.Author, PublishedAt = rel.PublishedAt, PostedAt = rel.PostedAt, Label = rel.Label,
                Release = rel.ReleaseName, Embed = rel.Embed, Tracks = rel.Tracks,
            });
            return result;
        }

        var fresh = items.Where(it => !knownIds.Contains(it.Url)).ToList();
        // Pages that could not be read are remembered, and tried after the ones not yet tried,
        // so a few unreadable releases cannot hold up the rest of a back catalogue.
        var failKey = $"bandcamp_unread:{account.Id}";
        var stored = Html.ParseJson(db.KvGet(failKey)) as JsonObject ?? new JsonObject();
        var fails = new Dictionary<string, int>();
        foreach (var it in fresh)
            if (stored[it.Url] is JsonNode n && Html.AsNumber(n) is { } count) fails[it.Url] = (int)count;
        fresh = fresh.OrderBy(it => Math.Min(fails.GetValueOrDefault(it.Url), GiveWayAfter)).ToList();   // stable
        int i = 0;
        foreach (var it in fresh.Take(MaxNew))
        {
            if (i++ > 0 && Pause > TimeSpan.Zero) await Task.Delay(Pause, ct);
            Release rel;
            var url = it.Url;
            try
            {
                var (page, _) = await GetAsync(client, url, ct);
                rel = ParseRelease(page, url);
                if (rel.Placeholder && rel.SoldAt == null)
                    rel.SoldAt = await SellerAsync(client, url, rel.Title ?? it.Title, rel.Author ?? it.Author ?? result.DisplayName, ct);
                if (rel.SoldAt != null)
                {
                    // Shown here but sold from another page: link and play that one instead, so the
                    // feed shows one copy and its link is a page where the release can be bought.
                    var (realPage, _) = await GetAsync(client, rel.SoldAt, ct);
                    var real = ParseRelease(realPage, rel.SoldAt);
                    if (real.Tracks != null || (real.IsRelease && !real.Placeholder))
                    {
                        url = rel.SoldAt;
                        real.Author ??= rel.Author;
                        real.PublishedAt ??= rel.PublishedAt;
                        rel = real;
                    }
                }
            }
            catch (Pace.BusyException)
            {
                break;                 // a lookup is running or Bandcamp asked for a pause: keep what loaded, stop for now
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                fails[it.Url] = fails.GetValueOrDefault(it.Url) + 1;
                continue;              // try again on a later refresh
            }
            fails.Remove(it.Url);
            result.Posts.Add(new Post
            {
                ExternalId = it.Url, Url = url, Title = rel.Title ?? it.Title, Image = rel.Image ?? it.Image,
                Kind = KindOf(url), Author = rel.Author ?? it.Author, PublishedAt = rel.PublishedAt, PostedAt = rel.PostedAt,
                Label = rel.Label, Release = rel.ReleaseName ?? rel.Title ?? it.Title, Embed = rel.Embed, Tracks = rel.Tracks,
                Note = rel.Placeholder ? NotForSale : null,
            });
        }
        db.KvSet(failKey, JsonSerializer.Serialize(fails));
        var unreadable = fails.Values.Count(n => n >= GiveWayAfter);
        var left = fresh.Count - result.Posts.Count - unreadable;
        var notes = new List<string>();
        if (left > 0) notes.Add($"{left} older release(s) still to load; {MaxNew} more load every few minutes while the app is running.");
        if (unreadable > 0) notes.Add($"{unreadable} release page(s) could not be read after several tries; still retrying.");
        if (notes.Count > 0) result.Warning = string.Join(" ", notes);
        return result;
    }
}
