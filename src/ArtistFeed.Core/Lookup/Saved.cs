using System.Text;
using System.Text.Json.Nodes;

namespace ArtistFeed.Core;

/// <summary>
/// Everything Link Finder has saved: one row per track ever looked up, plus the tracks in the list you
/// imported that haven't been looked up yet (the web version's saved.py).
/// </summary>
public sealed partial class LinkFinder
{
    public static readonly string[] NotFound = { "bandcamp_artist", "soundcloud", "soundcloud_search", "youtube_channel", "none", "error" };
    public static readonly string[] FoundStatuses = { "bandcamp", "soundcloud_track", "bandcamp_check", "youtube", "beatport", "beatport_check", "discogs" };
    public static readonly string[] SavedFilters = { "", "lastrun", "review", "missing", "found", "manual", "list", "nospelling", "unsearched" };

    static JsonObject Entry(string rest)
    {
        var bar = rest.IndexOf('|');
        var tidyArtist = bar >= 0 ? rest[..bar] : rest;
        var tkey = bar >= 0 ? rest[(bar + 1)..] : "";
        var hash = tkey.IndexOf('#');
        var b = hash >= 0 ? tkey[..hash] : tkey;
        var version = hash >= 0 ? tkey[(hash + 1)..] : "";
        var o = new JsonObject
        {
            ["key"] = rest, ["tidy_artist"] = tidyArtist, ["tidy_title"] = b, ["version"] = version.Replace("+", ", "),
            ["artist"] = "", ["title"] = "", ["status"] = "", ["url"] = "", ["site"] = "", ["year"] = "", ["release"] = "", ["found"] = "",
            ["note"] = "", ["at"] = "", ["searched"] = false, ["list_artist"] = "", ["list_title"] = "", ["list_link"] = "", ["list_note"] = "",
            ["list_key"] = "", ["list_variant"] = "", ["list_guess"] = false,
        };
        return o;
    }

    static void FromListInto(JsonObject row, Dictionary<string, object?> t)
    {
        row["list_artist"] = t.S("artist") ?? "";
        row["list_title"] = t.S("title") ?? "";
        row["list_link"] = t.S("url") ?? "";
        row["list_note"] = t.S("note") ?? "";
        row["year"] = row.B("year") ? row.T("year") : t.S("year") ?? "";
        row["list_key"] = t.S("key") ?? "";
        row["list_variant"] = t.S("variant") ?? "";
    }

    static JsonObject ListResult(Dictionary<string, object?> t)
    {
        var r = FromList(t.S("url") ?? "", t.S("year") ?? "", t.S("note") ?? "");
        r.Remove("steps");
        return r;
    }

    /// <summary>Your list's entry for a saved track that doesn't match one exactly: the entry with the same words,
    /// else the only version of that title in your list.</summary>
    static Dictionary<string, object?>? Closest(List<Dictionary<string, object?>> entries, JsonObject row)
    {
        var words = Matcher.Plain(string.Join(" ", new[] { row.T("tidy_title"), row.T("version") }.Where(x => x.Length > 0)));
        var same = entries.FirstOrDefault(t => t.S("variant") == words);
        if (same != null) return same;
        var b = row.T("tidy_title");
        var versions = entries.Where(t =>
        {
            var k = t.S("key") ?? "";
            var rest = k.Contains('|') ? k[(k.IndexOf('|') + 1)..] : "";
            return (rest.Contains('#') ? rest[..rest.IndexOf('#')] : rest) == b;
        }).ToList();
        return versions.Count == 1 ? versions[0] : null;
    }

    static long Stamp(string at)
    {
        var digits = new string(at.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? 0 : long.TryParse(digits.Length > 18 ? digits[..18] : digits, out var n) ? n : 0;
    }

    public List<JsonObject> AllSavedRows()
    {
        var known = Known();
        var outp = new Dictionary<string, JsonObject>();
        var order = new List<string>();
        var manual = new HashSet<string>();
        void Put(string k, JsonObject v) { if (!outp.ContainsKey(k)) order.Add(k); outp[k] = v; }
        foreach (var r in Db.Query("SELECT key, result, at FROM lookup_cache"))
        {
            var full = r.S("key") ?? "";
            var bar = full.IndexOf('|');
            var kind = bar >= 0 ? full[..bar] : full;
            var rest = bar >= 0 ? full[(bar + 1)..] : "";
            if ((kind != CacheVersion && kind != "manual") || manual.Contains(rest)) continue;     // a link you set outranks the search
            if (J.Parse(r.S("result")) is not JsonObject saved) continue;
            if (kind == "manual") manual.Add(rest);
            else saved = WithCurrentFallback(saved) ?? saved;
            var row = Entry(rest);
            foreach (var k in new[] { "artist", "title", "status", "url", "site", "year", "release", "found", "note" }) row[k] = saved.T(k);
            row["at"] = r.S("at") ?? "";
            row["searched"] = true;
            Put(rest, row);
        }
        foreach (var (key, entries) in known.Tracks)
        {
            outp.TryGetValue(key, out var row);
            Dictionary<string, object?>? shown = null;
            if (row != null)                                     // looked up: the entry written the way it was typed
            {
                shown = (row.B("title") ? KnownEntry(row.T("artist"), row.T("title")) : null) ?? entries[0];
                if ((shown.S("url") ?? "").Length > 0 && !Strong.Contains(row.S("status")))
                    Merge(row, ListResult(shown));
                FromListInto(row, shown);
            }
            foreach (var t in entries)                           // in your list, never looked up
            {
                if (ReferenceEquals(t, shown)) continue;
                var extra = Entry(key);
                if ((t.S("url") ?? "").Length > 0)
                {
                    Merge(extra, ListResult(t));
                    extra["at"] = t.S("at") ?? "";
                }
                FromListInto(extra, t);
                Put(outp.ContainsKey(key) ? $"{key}~{t.S("variant")}" : key, extra);
            }
        }
        var byArtist = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var (key, entries) in known.Tracks)
        {
            var a = key.Contains('|') ? key[..key.IndexOf('|')] : key;
            if (!byArtist.TryGetValue(a, out var l)) byArtist[a] = l = new List<Dictionary<string, object?>>();
            l.AddRange(entries);
        }
        var run = LastRun();
        foreach (var row in outp.Values)
        {
            row["last_run"] = run.Contains(row.T("key"));
            if (!(row.B("list_title") || row.B("list_link")))   // nothing under this exact entry: the closest one
            {
                var guess = Closest(byArtist.GetValueOrDefault(row.T("tidy_artist")) ?? new(), row);
                if (guess != null)
                {
                    FromListInto(row, guess);
                    row["list_guess"] = true;
                }
            }
            if (!row.B("list_artist")) row["list_artist"] = known.Artists.GetValueOrDefault(row.T("tidy_artist")) ?? "";
            row["review"] = ReviewReason(row);
            row["result"] = row.B("status") ? StatusText.GetValueOrDefault(row.T("status")) ?? row.T("status") : "Not looked up yet";
        }
        return order.Select(k => outp[k])
            .OrderBy(r => r.B("searched") ? 0 : 1).ThenBy(r => -Stamp(r.T("at")))
            .ThenBy(r => r.T("tidy_artist"), StringComparer.Ordinal).ThenBy(r => r.T("tidy_title"), StringComparer.Ordinal).ToList();
    }

    static bool KeepRow(JsonObject row, string which)
    {
        var status = row.T("status");
        return which switch
        {
            "lastrun" => row.B("last_run"),
            "review" => row.B("review"),
            "missing" => NotFound.Contains(status),
            "found" => FoundStatuses.Contains(status),
            "manual" => status == "manual",
            "list" => row.B("list_title") || row.B("list_link"),
            "nospelling" => !(row.B("list_title") || row.B("list_artist")),
            "unsearched" => !row.B("searched"),
            _ => true,
        };
    }

    static readonly Dictionary<string, Func<JsonObject, string>> SavedSorts = new()
    {
        ["artist"] = r => $"{r.T("tidy_artist")}\u0000{r.T("tidy_title")}\u0000{r.T("version")}",
        ["title"] = r => $"{r.T("tidy_title")}\u0000{r.T("version")}\u0000{r.T("tidy_artist")}",
        ["list_artist"] = r => Names.Fold(r.T("list_artist")),
        ["list_title"] = r => Names.Fold(r.T("list_title")),
        ["link"] = r =>
        {
            var u = r.T("url");
            u = u.Contains("://") ? u[(u.LastIndexOf("://", StringComparison.Ordinal) + 3)..] : u;
            return (u.StartsWith("www.") ? u[4..] : u).ToLowerInvariant();
        },
        ["year"] = r => r.T("year"),
        ["result"] = r => (r.B("review") ? "0" : "1") + "\u0000" + r.T("result").ToLowerInvariant(),     // rows to review lead
        ["saved"] = r => r.B("searched") ? r.T("at") : "",
    };

    static List<JsonObject> SortedRows(List<JsonObject> rows, string sort, bool desc)
    {
        if (!SavedSorts.TryGetValue(sort, out var key)) return rows;
        bool Blank(JsonObject r)
        {
            var k = key(r);
            return k == "" || k == "0\u0000" || k == "1\u0000" || k == "\u0000\u0000";
        }
        var filled = rows.Where(r => !Blank(r));
        filled = desc ? filled.OrderByDescending(key, StringComparer.Ordinal) : filled.OrderBy(key, StringComparer.Ordinal);
        return filled.Concat(rows.Where(Blank)).ToList();
    }

    /// <summary>Saved results: filtered, searched, sorted, a page at a time.</summary>
    public (int Total, Dictionary<string, int> Counts, List<JsonObject> Rows) SavedListing(string q = "", string which = "", int limit = 200, int offset = 0, string sort = "", bool desc = false)
    {
        var rows = AllSavedRows();
        var counts = SavedFilters.ToDictionary(f => f, f => rows.Count(r => KeepRow(r, f)));
        var words = Names.Fold(q ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
        {
            string Text(JsonObject r) => Names.Fold(string.Join(" ", new[] { "tidy_artist", "tidy_title", "version", "artist", "title", "list_artist",
                "list_title", "url", "note", "list_note", "found", "release" }.Select(r.T)));
            rows = rows.Where(r => { var t = Text(r); return words.All(t.Contains); }).ToList();
        }
        rows = SortedRows(rows.Where(r => KeepRow(r, which)).ToList(), sort, desc);
        var page = limit > 0 ? rows.Skip(offset).Take(limit).ToList() : rows;
        return (rows.Count, counts, page);
    }

    public string SavedCsv(string q = "", string which = "", string sort = "", bool desc = false)
    {
        var sb = new StringBuilder("﻿");
        sb.Append(Csv.Line(new[] { "artist (tidied)", "title (tidied)", "version", "artist (as typed)", "title (as typed)",
                                   "artist (your spelling)", "title (your spelling)", "year", "link", "needs review", "link note",
                                   "your list's link", "site", "release", "result", "found as", "note", "saved" })).Append("\r\n");
        foreach (var r in SavedListing(q, which, 0, 0, sort, desc).Rows)
            sb.Append(Csv.Line(new[]
            {
                r.T("tidy_artist"), r.T("tidy_title"), r.T("version"), r.T("artist"), r.T("title"), r.T("list_artist"), r.T("list_title"),
                r.T("year"), r.T("url"), r.B("review") ? "REVIEW: " + r.T("review") : "", r.T("list_note"), r.T("list_link"),
                r.T("site"), r.T("release"), r.T("result"), r.T("found"), r.T("note"), r.T("at"),
            })).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Drop what is saved for one track: the search's answer and any link you set. Your list is kept.</summary>
    public int ForgetSaved(string rest)
    {
        var before = (int)Db.Query("SELECT COUNT(*) AS n FROM lookup_cache WHERE key IN (?,?)", $"{CacheVersion}|{rest}", $"manual|{rest}")[0].L("n");
        Db.Execute("DELETE FROM lookup_cache WHERE key IN (?,?)", $"{CacheVersion}|{rest}", $"manual|{rest}");
        return before;
    }
}
