using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ArtistFeed.Core;

/// <summary>
/// YouTube: a channel's newest uploads, from the feed YouTube publishes for every channel. The feed
/// needs no key and lists the 15 newest videos. It is addressed by the channel's id ("UC..."), so a
/// channel given by its @handle is opened once to read its id, which is then remembered.
/// The feed gives no running times, so YouTube posts have no duration.
/// </summary>
public sealed class YouTube : ISource
{
    public const string FeedUrl = "https://www.youtube.com/feeds/videos.xml";
    public string Key => "youtube";
    public string Label => "YouTube";
    public string Color => "#ff3b3b";
    public string Placeholder => "@handle or https://www.youtube.com/@handle";

    // Answers the cookie question YouTube asks visitors from some countries, so the channel page itself comes back.
    const string Cookies = "SOCS=CAI; CONSENT=YES+1";
    static readonly XNamespace A = "http://www.w3.org/2005/Atom";
    static readonly XNamespace Yt = "http://www.youtube.com/xml/schemas/2015";
    static readonly XNamespace Media = "http://search.yahoo.com/mrss/";
    static readonly Regex ChannelId = new("^UC[A-Za-z0-9_-]{22}$");
    static readonly Regex[] IdOnPage =
    {
        new(@"<link rel=""canonical"" href=""https://www\.youtube\.com/channel/(UC[A-Za-z0-9_-]{22})"""),
        new(@"<meta itemprop=""(?:identifier|channelId)"" content=""(UC[A-Za-z0-9_-]{22})"""),
        new(@"""externalId"":""(UC[A-Za-z0-9_-]{22})"""),
        new(@"""browseId"":""(UC[A-Za-z0-9_-]{22})"""),
    };
    static readonly HashSet<string> Reserved = new()
    {
        "watch", "results", "feed", "playlist", "shorts", "embed", "live", "gaming", "music", "premium",
        "account", "about", "t", "hashtag", "channel", "c", "user", "redirect",
    };

    /// <summary>(channel name, posts) from a channel's video feed.</summary>
    public static (string? Channel, List<Post> Posts) ParseFeed(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (XmlException) { throw new SourceException("YouTube sent something that isn't a channel feed."); }
        var root = doc.Root!;
        var channel = (root.Element(A + "title")?.Value ?? "").Trim();
        var posts = new List<Post>();
        foreach (var e in root.Elements(A + "entry"))
        {
            var vid = (e.Element(Yt + "videoId")?.Value ?? "").Trim();
            if (vid.Length == 0) continue;
            var title = (e.Element(A + "title")?.Value ?? "").Trim();
            if (title.Length == 0) title = "Untitled";
            var link = e.Element(A + "link")?.Attribute("href")?.Value;
            var url = string.IsNullOrEmpty(link) ? $"https://www.youtube.com/watch?v={vid}" : link;
            var thumb = e.Element(Media + "group")?.Element(Media + "thumbnail")?.Attribute("url")?.Value;
            var when = Http.IsoFromIso(e.Element(A + "published")?.Value);
            var author = (e.Element(A + "author")?.Element(A + "name")?.Value ?? "").Trim();
            posts.Add(new Post
            {
                ExternalId = vid, Title = title, Url = url,
                Image = string.IsNullOrEmpty(thumb) ? $"https://i.ytimg.com/vi/{vid}/hqdefault.jpg" : thumb,
                Kind = "track", Author = author.Length > 0 ? author : (channel.Length > 0 ? channel : null),
                PublishedAt = when, PostedAt = when, Embed = vid,
                Tracks = new List<Track> { new(title, null, ExtId: vid, Url: url) },
            });
        }
        return (channel.Length > 0 ? channel : null, posts);
    }

    public static string? ChannelIdIn(string html)
    {
        foreach (var pattern in IdOnPage)
        {
            var m = pattern.Match(html ?? "");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    public (string Handle, string Url) Normalize(string text)
    {
        text = (text ?? "").Trim().TrimEnd('/');
        if (text.Length == 0) throw new SourceException("Enter a YouTube channel: its @handle or address.");
        string handle;
        if (Regex.IsMatch(text, @"(^|\.|//)(youtube\.com|youtu\.be)\b", RegexOptions.IgnoreCase))
        {
            if (!text.Contains("://")) text = "https://" + text;
            string[] parts;
            try { parts = new Uri(text).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray(); }
            catch (UriFormatException) { throw new SourceException("That address isn't a YouTube channel."); }
            if (parts.Length == 0) throw new SourceException("That address isn't a YouTube channel.");
            if (parts[0].StartsWith('@')) handle = parts[0];
            else if (parts[0] == "channel" && parts.Length > 1) handle = parts[1];
            else if (parts[0] is "c" or "user" && parts.Length > 1) handle = $"{parts[0]}/{parts[1]}";
            else if (!Reserved.Contains(parts[0].ToLowerInvariant())) handle = "c/" + parts[0];    // youtube.com/SomeName: an old custom address
            else throw new SourceException("That's a video or page, not a channel. Use the channel's @handle or address.");
        }
        else handle = ChannelId.IsMatch(text) ? text : "@" + text.TrimStart('@');
        if (ChannelId.IsMatch(handle)) return (handle, $"https://www.youtube.com/channel/{handle}");
        if (handle.StartsWith('@'))
        {
            var name = handle[1..];
            if (!Regex.IsMatch(name, @"^[\w.\-·]{1,100}$")) throw new SourceException("That doesn't look like a YouTube @handle.");
            handle = "@" + name.ToLowerInvariant();
            return (handle, $"https://www.youtube.com/{handle}");
        }
        var slash = handle.IndexOf('/');
        var kind = handle[..slash];
        var rest = handle[(slash + 1)..];
        if (!Regex.IsMatch(rest, @"^[\w.\-]{1,100}$")) throw new SourceException("That doesn't look like a YouTube channel address.");
        return ($"{kind}/{rest}", $"https://www.youtube.com/{kind}/{rest}");
    }

    static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Cookie", Cookies);
        return await client.SendAsync(req, ct);
    }

    public async Task<FetchResult> FetchAsync(HttpClient client, Account account, HashSet<string> knownIds, CancellationToken ct = default)
    {
        var meta = account.Meta;
        var result = new FetchResult();
        var cid = Html.AsString(meta["channel_id"]);
        if (string.IsNullOrEmpty(cid)) cid = ChannelId.IsMatch(account.Handle) ? account.Handle : null;
        if (cid == null)
        {
            using var page = await GetAsync(client, account.Url, ct);
            if (page.StatusCode == HttpStatusCode.NotFound) throw new SourceException("YouTube channel not found (check the handle).");
            Http.EnsureOk(page);
            cid = ChannelIdIn(await page.Content.ReadAsStringAsync(ct))
                  ?? throw new SourceException("Couldn't read this channel's id from its YouTube page.");
            meta["channel_id"] = cid;
        }
        using var r = await GetAsync(client, Http.Query(FeedUrl, new Dictionary<string, string> { ["channel_id"] = cid }), ct);
        if (r.StatusCode == HttpStatusCode.NotFound)
        {
            meta.Remove("channel_id");                       // the channel may have gone, or its id changed
            throw new SourceException("YouTube has no channel with this id any more.");
        }
        Http.EnsureOk(r);
        (result.DisplayName, result.Posts) = ParseFeed(await r.Content.ReadAsStringAsync(ct));
        return result;
    }
}
