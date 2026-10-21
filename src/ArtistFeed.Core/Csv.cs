using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ArtistFeed.Core;

public static class Csv
{
    /// <summary>One CSV line, quoting fields that need it (like Python's csv.writer).</summary>
    public static string Line(IEnumerable<string?> fields) => string.Join(",", fields.Select(f =>
    {
        f ??= "";
        return f.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + f.Replace("\"", "\"\"") + "\"" : f;
    }));

    /// <summary>Rows of a CSV text; rows with nothing in them are left out.</summary>
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n') { row.Add(cell.ToString()); rows.Add(row); row = new(); cell.Clear(); }
            else if (ch != '\r') cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows.Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
    }
}

/// <summary>Filling Tracked Artists from a CSV file or a pasted list.</summary>
public static class TableImport
{
    /// <summary>Rows from a CSV file. With a header row, columns are matched by name ("name"/"artist", the
    /// site's key or label); otherwise they are taken in table order (name, then each site).</summary>
    public static List<TableRow> FromCsv(string text, IReadOnlyList<ISource> columnsOn)
    {
        var rows = Csv.Parse(text.TrimStart('﻿'));
        var result = new List<TableRow>();
        if (rows.Count == 0) return result;
        var cols = new List<string> { "name" };
        cols.AddRange(columnsOn.Select(s => s.Key));
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var map = cols.Select((_, i) => i).ToList();
        if (header.Contains("name") || header.Contains("artist"))
        {
            map = cols.Select(c => header.FindIndex(h => c == "name"
                ? h is "name" or "artist"
                : h == c || h == columnsOn.First(s => s.Key == c).Label.ToLowerInvariant())).ToList();
            rows = rows.Skip(1).ToList();
        }
        foreach (var r in rows)
        {
            var row = new TableRow();
            for (int i = 0; i < cols.Count; i++)
            {
                if (map[i] < 0 || map[i] >= r.Count) continue;
                Set(row, cols[i], r[map[i]].Trim());
            }
            result.Add(row);
        }
        return result;
    }

    static void Set(TableRow row, string col, string value)
    {
        if (col == "name") row.Name = value; else row.Cells[col] = value;
    }

    static string Get(TableRow row, string col) => col == "name" ? row.Name : row.Cells.GetValueOrDefault(col) ?? "";

    static string? SiteOf(string v) =>
        Regex.IsMatch(v, "bandcamp\\.com", RegexOptions.IgnoreCase) ? "bandcamp"
        : Regex.IsMatch(v, "soundcloud\\.com", RegexOptions.IgnoreCase) ? "soundcloud"
        : Regex.IsMatch(v, "youtube\\.com|youtu\\.be", RegexOptions.IgnoreCase) ? "youtube" : null;

    /// <summary>Rows from a pasted list: one artist or label per line. A name, a link, or name and links
    /// separated by tabs or commas. Links go to their own site's column wherever they sit in the line.</summary>
    public static List<TableRow> FromPaste(string text, IReadOnlyList<ISource> columnsOn)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var cols = new List<string> { "name" };
        cols.AddRange(columnsOn.Select(s => s.Key));
        var result = new List<TableRow>();
        for (int n = 0; n < lines.Count; n++)
        {
            var line = lines[n];
            var parts = (line.Contains('\t') ? line.Split('\t') : line.Split(',')).Select(p => p.Trim()).ToList();
            if (n == 0 && parts[0].ToLowerInvariant() is "name" or "artist") continue;     // a header row
            var row = new TableRow();
            var loose = new List<string>();
            foreach (var v in parts)
            {
                var site = v.Length > 0 ? SiteOf(v) : null;
                if (site != null && cols.Contains(site))
                    row.Cells[site] = row.Cells.TryGetValue(site, out var had) && had.Length > 0 ? had + ", " + v : v;
                else loose.Add(v);
            }
            for (int i = 0; i < cols.Count; i++)
            {
                var v = i < loose.Count ? loose[i] : "";
                if (v.Length > 0 && Get(row, cols[i]).Length == 0) Set(row, cols[i], v);
            }
            if (!IsBlank(row)) result.Add(row);
        }
        return result;
    }

    public static bool IsBlank(TableRow row) =>
        row.Name.Trim().Length == 0 && row.Cells.Values.All(v => (v ?? "").Trim().Length == 0);
}

/// <summary>How times and durations are shown.</summary>
public static class Show
{
    public static string Clock(double? seconds)
    {
        if (seconds == null) return "";
        var total = (long)Math.Round(seconds.Value, MidpointRounding.ToEven);
        long hours = total / 3600, rest = total % 3600;
        return hours > 0 ? $"{hours}:{rest / 60:00}:{rest % 60:00}" : $"{rest / 60}:{rest % 60:00}";
    }

    public static DateTimeOffset? ParseIso(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    /// <summary>"3h ago", "5d ago", or a date.</summary>
    public static string Ago(string? iso, DateTimeOffset? now = null)
    {
        var t = ParseIso(iso);
        if (t == null) return "";
        var s = ((now ?? DateTimeOffset.UtcNow) - t.Value).TotalSeconds;
        var local = t.Value.ToLocalTime();
        if (s < -86400) return "out " + local.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        if (s < 60) return "just now";
        if (s < 3600) return Math.Max(1, (int)Math.Round(s / 60)) + "m ago";
        if (s < 86400) return (int)Math.Round(s / 3600) + "h ago";
        if (s < 86400 * 14) return (int)Math.Round(s / 86400) + "d ago";
        return local.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }
}

/// <summary>The list view as a spreadsheet.</summary>
public static class FeedExport
{
    public static string TracksCsv(IEnumerable<TrackRow> rows, Sites sites)
    {
        var sb = new StringBuilder();
        sb.Append(Csv.Line(new[] { "artist", "label", "release", "track", "duration", "year", "released", "posted", "site", "url" })).Append("\r\n");
        foreach (var r in rows)
        {
            var day = (r.PublishedAt ?? "").Length >= 10 ? r.PublishedAt![..10] : r.PublishedAt ?? "";
            var site = sites.Get(r.Source)?.Label ?? r.Source;
            var posted = (r.Posted ?? "").Length >= 10 ? r.Posted![..10] : r.Posted ?? "";
            sb.Append(Csv.Line(new[] { r.Artist, r.Label, r.Release, r.Track, Show.Clock(r.Duration),
                day.Length >= 4 ? day[..4] : day, day, posted, site, r.Url })).Append("\r\n");
        }
        return sb.ToString();
    }
}
