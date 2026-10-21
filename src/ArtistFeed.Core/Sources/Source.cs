using System.Globalization;
using System.Net;

namespace ArtistFeed.Core;

/// <summary>What every site (Bandcamp, SoundCloud, YouTube) provides.</summary>
public interface ISource
{
    string Key { get; }
    string Label { get; }
    string Color { get; }
    string Placeholder { get; }

    /// <summary>How a stored handle is shown in Tracked Artists (must survive Normalize).</summary>
    string Short(string handle) => handle;

    /// <summary>Turn whatever the user typed into (handle, profile url). Throws SourceException.</summary>
    (string Handle, string Url) Normalize(string text);

    /// <summary>Recent posts. account.Meta may be updated to remember things between runs (it is saved afterwards).</summary>
    Task<FetchResult> FetchAsync(HttpClient client, Account account, HashSet<string> knownIds, CancellationToken ct = default);
}

public static class Http
{
    public const string UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    /// <summary>The client every request goes through. Tests pass a handler that answers with stand-in pages.</summary>
    public static HttpClient Create(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    /// <summary>A status other than success, kept so the refresh can say what the site answered.</summary>
    public sealed class StatusException(HttpStatusCode code) : Exception($"HTTP {(int)code}")
    {
        public HttpStatusCode Code { get; } = code;
    }

    public static void EnsureOk(HttpResponseMessage r)
    {
        if (!r.IsSuccessStatusCode) throw new StatusException(r.StatusCode);
    }

    /// <summary>UTC as the app stores it: "yyyy-MM-ddTHH:mm:ssZ".</summary>
    public static string Iso(DateTimeOffset dt) =>
        dt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>An ISO 8601 date-time ("2026-10-01T10:00:00Z", "+00:00", no zone = UTC).</summary>
    public static string? IsoFromIso(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.EndsWith('Z')) t = t[..^1] + "+00:00";
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*([+-]\d{2})(\d{2})$", "$1:$2");   // "+0000" -> "+00:00"
        if (DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return Iso(dt);
        return null;
    }

    public static string Query(string url, IEnumerable<KeyValuePair<string, string>> args)
    {
        var q = string.Join("&", args.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        if (q.Length == 0) return url;
        return url + (url.Contains('?') ? "&" : "?") + q;
    }
}
