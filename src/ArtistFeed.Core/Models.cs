using System.Text.Json.Nodes;

namespace ArtistFeed.Core;

/// <summary>A track of a release, as a site lists it.</summary>
public sealed record Track(string Title, double? Duration = null, string? Artist = null, string? ExtId = null, string? Url = null);

/// <summary>A release or upload read from a site.</summary>
public sealed class Post
{
    public required string ExternalId { get; init; }      // stable per account; usually the post URL
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Image { get; init; }
    public string? Kind { get; init; }                     // "album", "track"
    public string? Author { get; init; }
    public string? PublishedAt { get; init; }              // release date; UTC, "yyyy-MM-ddTHH:mm:ssZ"
    public string? PostedAt { get; init; }                 // when it was put up on the site, if the site says
    public string? Label { get; init; }
    public string? Release { get; init; }                  // the album/EP this belongs to
    public string? Embed { get; init; }                    // what the site's embeddable player needs
    public List<Track>? Tracks { get; init; }              // null: leave stored tracks alone
}

public sealed class FetchResult
{
    public List<Post> Posts { get; set; } = new();
    public string? DisplayName { get; set; }
    public string? Avatar { get; set; }
    public string? Warning { get; set; }                   // shown next to the page in Tracked Artists
    public List<string> Trace { get; } = new();            // what each request returned (for checking a page)
}

/// <summary>A problem worth showing to the user as it is.</summary>
public sealed class SourceException(string message) : Exception(message);

public sealed class Account
{
    public long Id { get; init; }
    public long ArtistId { get; init; }
    public required string Source { get; init; }
    public required string Handle { get; init; }
    public required string Url { get; init; }
    public string? DisplayName { get; init; }
    public string? Avatar { get; init; }
    public JsonObject Meta { get; init; } = new();
    public string? LastChecked { get; init; }
    public string? LastError { get; init; }

    public static Account From(Dictionary<string, object?> r)
    {
        JsonObject meta;
        try { meta = JsonNode.Parse(r["meta"] as string ?? "{}") as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { meta = new JsonObject(); }
        return new Account
        {
            Id = (long)r["id"]!, ArtistId = (long)r["artist_id"]!, Source = (string)r["source"]!, Handle = (string)r["handle"]!,
            Url = (string)r["url"]!, DisplayName = r["display_name"] as string, Avatar = r["avatar"] as string, Meta = meta,
            LastChecked = r["last_checked"] as string, LastError = r["last_error"] as string,
        };
    }
}

public sealed class Artist
{
    public long Id { get; init; }
    public required string Name { get; set; }
    public string Aliases { get; set; } = "";
    public List<Account> Accounts { get; set; } = new();
    public long PostCount { get; set; }
}

/// <summary>The feed's filters, shared by the grid, the list and the export.</summary>
public sealed record FeedFilter
{
    public string? Source { get; init; }
    public long? ArtistId { get; init; }
    public string? Query { get; init; }
    public string? Year { get; init; }
    public string? Label { get; init; }                    // "-" means "no label"
    public string? Kind { get; init; }                     // "album" or "track"
    public string? Length { get; init; }                   // short, medium, long, extended
    public bool ShowHidden { get; init; }
    public bool PreviewOnly { get; init; }                 // leave out what can't be listened to yet
    public string? Since { get; init; }                    // yyyy-MM-dd
    public string? Until { get; init; }
    public bool DateByReleased { get; init; }              // the date range uses the release date, not the posted one
    public IReadOnlyList<long>? FocusIds { get; init; }    // the Focus Feed: only these artists
}

public sealed record Facets(List<string> Years, List<string> Labels, int Hidden);

static class Row
{
    public static string? S(this Dictionary<string, object?> r, string k) => r.TryGetValue(k, out var v) ? v as string : null;
    public static long L(this Dictionary<string, object?> r, string k) => r.TryGetValue(k, out var v) && v != null ? Convert.ToInt64(v) : 0;
    public static long? LN(this Dictionary<string, object?> r, string k) => r.TryGetValue(k, out var v) && v != null ? Convert.ToInt64(v) : null;
    public static double? D(this Dictionary<string, object?> r, string k) => r.TryGetValue(k, out var v) && v != null ? Convert.ToDouble(v) : null;
}

/// <summary>One card of the grid.</summary>
public sealed class FeedPost
{
    public long Id { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Image { get; init; }
    public string? Kind { get; init; }
    public string? Author { get; init; }
    public string? PublishedAt { get; init; }
    public required string FirstSeen { get; init; }
    public string? Label { get; init; }
    public string? Release { get; init; }
    public string? Embed { get; init; }
    public bool Hidden { get; init; }
    public required string Source { get; init; }
    public bool Played { get; set; }
    public long ArtistId { get; init; }
    public required string Artist { get; init; }
    public long TrackCount { get; init; }
    public double? Duration { get; init; }

    public static FeedPost From(Dictionary<string, object?> r) => new()
    {
        Id = r.L("id"), Title = r.S("title") ?? "", Url = r.S("url") ?? "", Image = r.S("image"), Kind = r.S("kind"),
        Author = r.S("author"), PublishedAt = r.S("published_at"), FirstSeen = r.S("first_seen") ?? "", Label = r.S("label"),
        Release = r.S("release"), Embed = r.S("embed"), Hidden = r.L("hidden") != 0, Source = r.S("source") ?? "",
        Played = r.L("played") != 0, ArtistId = r.L("artist_id"), Artist = r.S("artist") ?? "", TrackCount = r.L("track_count"),
        Duration = r.D("duration"),
    };
}

/// <summary>One row of the list view.</summary>
public sealed class TrackRow
{
    public long PostId { get; init; }
    public long? TrackId { get; init; }
    public string Artist { get; init; } = "";
    public string Label { get; init; } = "";
    public string Release { get; init; } = "";
    public string Track { get; init; } = "";
    public double? Duration { get; init; }
    public long? Position { get; init; }
    public string? TrackExtId { get; init; }
    public string? Released { get; init; }
    public string? PublishedAt { get; init; }
    public string? Posted { get; init; }
    public string Url { get; init; } = "";
    public string PostUrl { get; init; } = "";
    public string? Embed { get; init; }
    public string? Image { get; init; }
    public string? Kind { get; init; }
    public bool ReleaseHidden { get; init; }
    public bool TrackHidden { get; init; }
    public bool Hidden { get; init; }
    public string TrackKey { get; init; } = "";
    public bool Played { get; set; }
    public string PostTitle { get; init; } = "";
    public string Source { get; init; } = "";
    public long ArtistId { get; init; }
    public string TrackedArtist { get; init; } = "";

    public static TrackRow From(Dictionary<string, object?> r) => new()
    {
        PostId = r.L("post_id"), TrackId = r.LN("track_id"), Artist = r.S("artist") ?? "", Label = r.S("label") ?? "",
        Release = r.S("release") ?? "", Track = r.S("track") ?? "", Duration = r.D("duration"), Position = r.LN("position"),
        TrackExtId = r.S("track_ext_id"), Released = r.S("released"), PublishedAt = r.S("published_at"), Posted = r.S("posted"),
        Url = r.S("url") ?? "", PostUrl = r.S("post_url") ?? "", Embed = r.S("embed"), Image = r.S("image"), Kind = r.S("kind"),
        ReleaseHidden = r.L("release_hidden") != 0, TrackHidden = r.L("track_hidden") != 0, Hidden = r.L("hidden") != 0,
        TrackKey = r.S("track_key") ?? "", Played = r.L("played") != 0, PostTitle = r.S("post_title") ?? "",
        Source = r.S("source") ?? "", ArtistId = r.L("artist_id"), TrackedArtist = r.S("tracked_artist") ?? "",
    };
}
