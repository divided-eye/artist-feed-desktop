using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArtistFeed.Core;

namespace ArtistFeed.Tests;

/// <summary>Stand-in copies of the sites' pages, the same ones the web app's tests use.</summary>
public sealed class StandIn : HttpMessageHandler
{
    public const string Cid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public int Requests;
    public bool ExpiredOnce;
    public readonly List<string> Seen = new();
    public Func<HttpRequestMessage, HttpResponseMessage?>? Extra;

    public static string Release(string name, string date, string kind = "MusicAlbum", JsonObject? ldExtra = null, string head = "")
    {
        var ld = new JsonObject
        {
            ["@type"] = kind, ["name"] = name, ["datePublished"] = date,
            ["image"] = $"https://f4.bcbits.com/img/{name}_10.jpg", ["byArtist"] = new JsonObject { ["name"] = "Night Ferry" },
        };
        foreach (var (k, v) in ldExtra ?? new JsonObject()) ld[k] = v?.DeepClone();
        return $"<html><head>{head}<script type=\"application/ld+json\">{ld.ToJsonString()}</script></head></html>";
    }

    public const string Grid = """
        <html><head><meta property="og:site_name" content="Night Ferry"></head><body>
        <img class="band-photo" src="https://f4.bcbits.com/img/0001_21.jpg">
        <ol id="music-grid" data-client-items='[{"art_id":333,"id":3,"page_url":"/album/third","title":"Third","type":"album"}]'>
         <li data-item-id="album-1" class="music-grid-item"><a href="/album/first?from=grid">
           <div class="art"><img src="https://f4.bcbits.com/img/a111_2.jpg"></div>
           <p class="title">First</p></a></li>
         <li data-item-id="track-2" class="music-grid-item"><a href="/track/second">
           <div class="art"><img src="/img/0.gif" data-original="https://f4.bcbits.com/img/a222_2.jpg"></div>
           <p class="title">Second <br><span class="artist-override">Guest Person</span></p></a></li>
        </ol></body></html>
        """;

    static readonly string Tralbum = new JsonObject
    {
        ["id"] = 111, ["item_type"] = "album", ["artist"] = "Night Ferry",
        ["current"] = new JsonObject { ["id"] = 111, ["type"] = "album", ["publish_date"] = "05 Mar 2025 16:00:00 GMT" },
        ["trackinfo"] = new JsonArray(
            new JsonObject { ["title"] = "Intro", ["duration"] = 61.5, ["track_id"] = 1001, ["title_link"] = "/track/intro" },
            new JsonObject { ["title"] = "Long One", ["duration"] = 3725, ["track_id"] = 1002, ["title_link"] = "/track/long-one", ["artist"] = "Guest Person" }),
    }.ToJsonString();

    public static readonly string First = Release("First", "07 Mar 2025 00:00:00 GMT", ldExtra: new JsonObject { ["publisher"] = new JsonObject { ["name"] = "Deep Label" } })
        .Replace("</head>", "</head><body><script data-tralbum='" + Tralbum + "'></script></body>")
        .Replace("<head>", "<head><meta name=\"bc-page-properties\" content='{\"item_type\":\"a\",\"item_id\":111}'>");

    public static readonly string Second = Release("Second", "01 Sep 2026 00:00:00 GMT", "MusicRecording",
        new JsonObject
        {
            ["duration"] = "P00H03M45S", ["inAlbum"] = new JsonObject { ["name"] = "First" },
            ["publisher"] = new JsonObject { ["name"] = "Night Ferry" },
        },
        "<meta property=\"og:video\" content=\"https://bandcamp.com/EmbeddedPlayer/v=2/track=222/size=large/\">");

    public const string YtFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns:yt="http://www.youtube.com/xml/schemas/2015" xmlns:media="http://search.yahoo.com/mrss/" xmlns="http://www.w3.org/2005/Atom">
         <title>Tube Band</title>
         <entry><yt:videoId>vid00000001</yt:videoId><title>Tube Band - Glass River</title>
          <link rel="alternate" href="https://www.youtube.com/watch?v=vid00000001"/>
          <author><name>Tube Band</name></author><published>2026-10-02T12:00:00+00:00</published>
          <media:group><media:thumbnail url="https://i1.ytimg.com/vi/vid00000001/hqdefault.jpg" width="480" height="360"/></media:group></entry>
         <entry><yt:videoId>vid00000002</yt:videoId><title>Studio Diary</title>
          <link rel="alternate" href="https://www.youtube.com/watch?v=vid00000002"/>
          <author><name>Tube Band</name></author><published>2026-09-01T12:00:00+00:00</published></entry>
        </feed>
        """;

    public static HttpResponseMessage Text(string text, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(text, Encoding.UTF8, "text/html") };

    public static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("") };

    public static Dictionary<string, string> QueryOf(Uri uri)
    {
        var d = new Dictionary<string, string>();
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            d[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return d;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref Requests);
        lock (Seen) Seen.Add(request.RequestUri!.ToString());
        var r = Answer(request);
        r.RequestMessage = request;
        return Task.FromResult(r);
    }

    HttpResponseMessage Answer(HttpRequestMessage request)
    {
        if (Extra?.Invoke(request) is { } extra) return extra;
        var uri = request.RequestUri!;
        var host = uri.Host;
        var path = uri.AbsolutePath;
        var q = QueryOf(uri);
        if (host == "nightferry.bandcamp.com")
        {
            return path switch
            {
                "/music" => Text(Grid),
                "/album/first" => Text(First),
                "/track/second" => Text(Second),
                "/album/third" => Text(Release("Third", "15 Jan 2024 00:00:00 GMT")),
                _ => Status(HttpStatusCode.NotFound),
            };
        }
        if (host == "deeplabel.bandcamp.com" && path == "/music")      // a label page listing the same album
            return Text("<html><head><meta property=\"og:site_name\" content=\"Deep Label\"></head><body>" +
                        "<ol id=\"music-grid\"><li><a href=\"https://nightferry.bandcamp.com/album/first?label=9\">" +
                        "<p class=\"title\">First</p></a></li></ol></body></html>");
        if (host == "solo.bandcamp.com" && path == "/music")
            return Text(Release("Only One", "02 Feb 2026 00:00:00 GMT").Replace("\"@type\"", "\"@id\":\"https://solo.bandcamp.com/album/only-one\",\"@type\""));
        if (host == "soundcloud.com") return Text("<script src=\"https://a-v2.sndcdn.com/assets/0-abc.js\"></script>");
        if (host == "a-v2.sndcdn.com") return Text($"x={{client_id:\"{Cid}\",y:1}}");
        if (host == "api-v2.soundcloud.com")
        {
            if (q.GetValueOrDefault("client_id") != Cid) return Status(HttpStatusCode.Unauthorized);
            if (path == "/resolve")
            {
                if (!q["url"].EndsWith("/nightferry")) return Status(HttpStatusCode.NotFound);
                return Json(new JsonObject { ["kind"] = "user", ["id"] = 42, ["username"] = "Night Ferry", ["avatar_url"] = "https://i1.sndcdn.com/avatars-1-large.jpg" });
            }
            if (path == "/users/42/tracks")
                return Json(JsonNode.Parse("""
                    {"collection": [
                      {"kind": "track", "id": 900, "title": "Demo", "permalink_url": "https://soundcloud.com/nightferry/demo",
                       "artwork_url": "https://i1.sndcdn.com/artworks-9-large.jpg", "created_at": "2026-10-01T10:00:00Z",
                       "duration": 30000, "full_duration": 200000, "label_name": "SC Label",
                       "publisher_metadata": {"album_title": "Demo EP"}, "user": {"username": "Night Ferry"}},
                      {"kind": "track", "id": 901, "title": "No Art", "permalink_url": "https://soundcloud.com/nightferry/no-art",
                       "artwork_url": null, "display_date": "2026-08-01T10:00:00Z", "created_at": "2020-01-01T00:00:00Z",
                       "user": {"username": "Night Ferry", "avatar_url": "https://i1.sndcdn.com/avatars-1-large.jpg"}}]}
                    """)!);
        }
        if (host == "www.youtube.com")
        {
            if (path == "/@tubeband")
                return Text("<html><link rel=\"canonical\" href=\"https://www.youtube.com/channel/UC1234567890123456789012\"></html>");
            if (path == "/feeds/videos.xml" && q.GetValueOrDefault("channel_id") == "UC1234567890123456789012")
                return Text(YtFeed);
        }
        return Status(HttpStatusCode.NotFound);
    }
}

/// <summary>A fresh app with its own database, reading the stand-in sites.</summary>
public sealed class TestApp : IDisposable
{
    public readonly string Folder = Path.Combine(Path.GetTempPath(), "artistfeed-test-" + Guid.NewGuid().ToString("N"));
    public readonly StandIn Web = new();
    public readonly Engine Engine;

    public TestApp()
    {
        Engine = new Engine(Path.Combine(Folder, "feed.db"), Web, new Pace(gap: 0, maxGap: 0, backoff: 0));
        Engine.Bandcamp.MaxNew = 2;
    }

    public IReadOnlyList<string> On => Engine.Sites.Enabled();

    public List<FeedPost> Feed(FeedFilter? f = null) => Engine.Db.Feed(f ?? new FeedFilter(), On, 60, 0);

    public List<TrackRow> Tracks(FeedFilter? f = null, string sort = "date", bool asc = false, int? limit = null, int offset = 0) =>
        Engine.Db.Tracks(f ?? new FeedFilter(), On, sort, asc, limit, offset);

    /// <summary>Add an artist the way Tracked Artists does, and read their pages.</summary>
    public async Task<List<long>> AddAsync(params (string Site, string Text)[] pages)
    {
        var rows = Engine.Table.Rows();
        var row = new TableRow();
        foreach (var (site, text) in pages) row.Cells[site] = text;
        rows.Add(row);
        var added = Engine.Table.Save(rows);
        await Engine.Refresher.RefreshAsync(added);
        return added;
    }

    public void Dispose()
    {
        Engine.Dispose();
        try { Directory.Delete(Folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
