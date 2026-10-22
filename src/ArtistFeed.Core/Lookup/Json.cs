using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArtistFeed.Core;

/// <summary>
/// Small helpers for the lookup's records. They are kept as JSON objects, in the web version's
/// shapes, because they are stored in the shared database (saved answers, cached pages).
/// </summary>
public static class J
{
    /// <summary>The value at `key` as text (numbers and booleans written out), or null.</summary>
    public static string? S(this JsonObject? o, string key) => o == null ? null : Html.Str(o[key]);

    /// <summary>The text at `key`, or "" (Python's `d.get(key) or ""`).</summary>
    public static string T(this JsonObject? o, string key) => o.S(key) ?? "";

    public static JsonObject? O(this JsonObject? o, string key) => o?[key] as JsonObject;
    public static JsonArray? A(this JsonObject? o, string key) => o?[key] as JsonArray;
    public static List<JsonObject> Objs(this JsonObject? o, string key) => o.A(key)?.OfType<JsonObject>().ToList() ?? new();
    public static List<string> Strs(this JsonObject? o, string key) =>
        o.A(key)?.Select(Html.Str).Where(x => x != null).Select(x => x!).ToList() ?? new();

    /// <summary>Python truthiness of the value at `key`.</summary>
    public static bool B(this JsonObject? o, string key) => o != null && Html.Truthy(o[key]);

    public static double? N(this JsonObject? o, string key) => o == null ? null : Html.AsNumber(o[key]);

    /// <summary>A copy with these keys set (Python's dict(d, k=v)).</summary>
    public static JsonObject With(this JsonObject o, params (string Key, JsonNode? Value)[] sets)
    {
        var c = (JsonObject)o.DeepClone();
        foreach (var (k, v) in sets) c[k] = v?.DeepClone();
        return c;
    }

    public static JsonObject Copy(this JsonObject o) => (JsonObject)o.DeepClone();

    public static JsonArray Arr(IEnumerable<string> items) => new(items.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    public static JsonArray Arr(IEnumerable<JsonNode?> items) => new(items.Select(x => x?.DeepClone()).ToArray());

    public static JsonObject? Parse(string? text) => Html.ParseJson(text) as JsonObject;

    /// <summary>Written as Python's json.dumps writes it (", " and ": "), so stored answers look alike.</summary>
    public static string Dump(JsonNode? node) => node?.ToJsonString(new JsonSerializerOptions
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
}
