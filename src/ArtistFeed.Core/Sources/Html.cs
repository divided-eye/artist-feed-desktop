using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ArtistFeed.Core;

/// <summary>Small helpers for reading pages and the JSON inside them.</summary>
public static class Html
{
    static readonly HtmlParser Parser = new();

    public static IDocument Parse(string html) => Parser.ParseDocument(html ?? "");

    /// <summary>Each piece of text inside a node, trimmed, joined with `sep` (like BeautifulSoup's get_text(sep, strip=True)).</summary>
    public static string Text(INode? node, string sep = " ")
    {
        if (node == null) return "";
        var parts = new List<string>();
        Collect(node, parts, strip: true);
        return string.Join(sep, parts);
    }

    /// <summary>All the text inside a node, each piece separated by `sep`, untrimmed (get_text(sep)).</summary>
    public static string RawText(INode? node, string sep)
    {
        if (node == null) return "";
        var parts = new List<string>();
        Collect(node, parts, strip: false);
        return string.Join(sep, parts);
    }

    static void Collect(INode node, List<string> parts, bool strip)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == NodeType.Text)
            {
                var t = strip ? child.TextContent.Trim() : child.TextContent;
                if (!strip || t.Length > 0) parts.Add(t);
            }
            else if (child.NodeType == NodeType.Element)
                Collect(child, parts, strip);
        }
    }

    public static string? Meta(IDocument doc, string prop)
    {
        var tag = doc.QuerySelector($"meta[property=\"{prop}\"]") ?? doc.QuerySelector($"meta[name=\"{prop}\"]");
        var content = tag?.GetAttribute("content");
        return string.IsNullOrEmpty(content) ? null : content;
    }

    public static JsonNode? ParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }); }
        catch (JsonException) { return null; }
    }

    /// <summary>A JSON value as Python's str() would write it: strings as they are, whole numbers without ".0".</summary>
    public static string? Str(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<long>(out var l)) return l.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v.TryGetValue<double>(out var d))
                return d == Math.Floor(d) && Math.Abs(d) < 1e15
                    ? ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v.TryGetValue<bool>(out var b)) return b ? "True" : "False";
        }
        return node.ToJsonString();
    }

    /// <summary>A string value, or null for anything else (numbers, objects, missing).</summary>
    public static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static double? AsNumber(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        return null;
    }

    /// <summary>Truthy like Python: non-empty string, non-zero number, true, non-empty container.</summary>
    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        _ => true,
    };

    /// <summary>The first of the values that is truthy (Python's `a or b or c`), else the last.</summary>
    public static JsonNode? Or(params JsonNode?[] values)
    {
        foreach (var v in values) if (Truthy(v)) return v;
        return values.Length > 0 ? values[^1] : null;
    }

    /// <summary>Every multi-line string inside a JSON value.</summary>
    public static List<string> Strings(JsonNode? value, List<string> outp)
    {
        switch (value)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                if (s.Contains('\n') || s.Contains('\r')) outp.Add(s);
                break;
            case JsonObject o:
                foreach (var kv in o) Strings(kv.Value, outp);
                break;
            case JsonArray a:
                foreach (var x in a) Strings(x, outp);
                break;
        }
        return outp;
    }

    /// <summary>Join a link onto the page it was found on (like urllib's urljoin).</summary>
    public static string Join(string baseUrl, string link)
    {
        try
        {
            var joined = new Uri(new Uri(baseUrl), link);
            // Keep the address as written: Uri would escape letters such as "é" that the web app keeps.
            return Uri.UnescapeDataString(joined.AbsoluteUri) == joined.AbsoluteUri || link.Contains('%')
                ? joined.AbsoluteUri
                : joined.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery | UriComponents.Fragment, UriFormat.Unescaped);
        }
        catch (UriFormatException)
        {
            return link;
        }
    }
}
