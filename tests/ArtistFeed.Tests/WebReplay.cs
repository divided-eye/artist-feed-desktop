using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArtistFeed.Tests;

/// <summary>
/// The stand-in pages the web version's tests use, recorded while they ran (tools/record_web_exchanges.py)
/// and served back here, so Link Finder is checked against exactly the same pages in both versions.
/// Requests are matched the way the recorder keyed them: method, address with its query sorted (and the
/// SoundCloud key left out), and a JSON body written as Python's json.dumps(sort_keys=True) writes it.
/// </summary>
public sealed class WebReplay
{
    readonly Dictionary<string, List<JsonObject>> _answers;
    public readonly List<string> Asked = new();
    public readonly List<string> Missing = new();
    /// <summary>For a request recorded with several different answers: which one to give (by its order in the recording).</summary>
    public readonly Dictionary<string, int> Use = new();

    public List<JsonObject> Variants(string key) => _answers[key];

    public WebReplay()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "web-exchanges.json");
        var all = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        _answers = all.ToDictionary(kv => kv.Key, kv => kv.Value!.AsArray().OfType<JsonObject>().ToList());
    }

    /// <summary>Python's urllib.parse.quote_plus.</summary>
    static string QuotePlus(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' || c is '_' or '.' or '-' or '~') sb.Append(c);
            else if (c == ' ') sb.Append('+');
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    static string Unquote(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    /// <summary>JSON as Python's json.dumps(value, sort_keys=True) writes it.</summary>
    public static string PyDumps(JsonNode? node)
    {
        switch (node)
        {
            case null: return "null";
            case JsonObject o:
                return "{" + string.Join(", ", o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => PyString(kv.Key) + ": " + PyDumps(kv.Value))) + "}";
            case JsonArray a:
                return "[" + string.Join(", ", a.Select(PyDumps)) + "]";
            default:
                var v = (JsonValue)node;
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String: return PyString(v.GetValue<string>());
                    case JsonValueKind.True: return "true";
                    case JsonValueKind.False: return "false";
                    case JsonValueKind.Null: return "null";
                    default:
                        var d = v.GetValue<double>();
                        return d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);
                }
        }
    }

    static string PyString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    public static string KeyOf(HttpRequestMessage request)
    {
        var u = request.RequestUri!;
        var pairs = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => { var i = p.IndexOf('='); return i >= 0 ? (Unquote(p[..i]), Unquote(p[(i + 1)..])) : (Unquote(p), ""); })
            .Where(p => p.Item1 != "client_id")
            .OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal);
        var query = string.Join("&", pairs.Select(p => QuotePlus(p.Item1) + "=" + QuotePlus(p.Item2)));
        var body = request.Content?.ReadAsStringAsync().Result ?? "";
        if (body.Length > 0)
        {
            try { body = PyDumps(JsonNode.Parse(body)); }
            catch (JsonException) { }
        }
        var authority = u.IsDefaultPort ? u.Host.ToLowerInvariant() : $"{u.Host.ToLowerInvariant()}:{u.Port}";
        return $"{request.Method.Method} {u.Scheme}://{authority}{u.AbsolutePath}?{query} {body}".Trim();
    }

    /// <summary>The recorded answer for this request (the first successful one when several were seen), or null.</summary>
    public HttpResponseMessage? Answer(HttpRequestMessage request)
    {
        var key = KeyOf(request);
        lock (Asked) Asked.Add(key);
        if (!_answers.TryGetValue(key, out var answers))
        {
            lock (Missing) Missing.Add(key);
            return null;
        }
        var a = Use.TryGetValue(key, out var pick) ? answers[pick]
            : answers.FirstOrDefault(x => (int)(x["status"]!.GetValue<double>()) == 200) ?? answers[0];
        var type = a["type"]?.GetValue<string>() ?? "text/html";
        var mediaType = type.Split(';')[0].Trim();
        return new HttpResponseMessage((HttpStatusCode)(int)a["status"]!.GetValue<double>())
        {
            Content = new StringContent(a["text"]!.GetValue<string>(), Encoding.UTF8, string.IsNullOrEmpty(mediaType) ? "text/html" : mediaType),
        };
    }
}
