using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArtistFeed.Core;

public sealed class TracklistException(string message) : Exception(message);

/// <summary>One row of a tracklist to look up.</summary>
public sealed record WantedTrack(int N, string Artist, string Title);

/// <summary>Reading the tracklist or list you give Link Finder: an .xlsx file, a CSV file, or pasted rows.</summary>
public static class Tracklist
{
    /// <summary>Rows of cells from an .xlsx file, a CSV file, or text pasted from a spreadsheet.</summary>
    public static List<List<string>> ReadTable(byte[] data)
    {
        if (data.Length >= 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4)
        {
            try { return ReadXlsx(data); }
            catch (Exception) { throw new TracklistException("Couldn't read that Excel file. Try saving it as CSV."); }
        }
        if (data.Length >= 8 && data.Take(8).SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }))
            throw new TracklistException("Old-style .xls files aren't supported. Save as .xlsx or CSV, or copy the rows and paste them.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); if (text.StartsWith('﻿')) text = text[1..]; }
        catch (DecoderFallbackException) { text = Latin(data); }
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return new();
        var sample = string.Join("\n", lines.Take(20));
        if (sample.Contains('\t')) return lines.Select(l => l.Split('\t').Select(c => c.Trim()).ToList()).ToList();
        var delimiter = sample.Count(c => c == ';') > sample.Count(c => c == ',') ? ';' : ',';
        return lines.Select(l => CsvLine(l, delimiter).Select(c => c.Trim()).ToList()).ToList();
    }

    public static List<List<string>> ReadTable(string text) => ReadTable(new UTF8Encoding(false).GetBytes(text));

    /// <summary>Windows-1252, as the web version falls back to for files that aren't UTF-8.</summary>
    static string Latin(byte[] data)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(data);
        }
        catch (Exception) { return Encoding.Latin1.GetString(data); }
    }

    /// <summary>One line split as Python's csv.reader splits it.</summary>
    public static List<string> CsvLine(string line, char delimiter = ',')
    {
        var fields = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, atStart = true;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else quoted = false;
                }
                else cur.Append(c);
                continue;
            }
            if (c == '"' && atStart) { quoted = true; atStart = false; continue; }
            if (c == delimiter) { fields.Add(cur.ToString()); cur.Clear(); atStart = true; continue; }
            cur.Append(c);
            atStart = false;
        }
        fields.Add(cur.ToString());
        return fields;
    }

    /// <summary>The first worksheet of an .xlsx file, as text (formulas give their saved values).</summary>
    static List<List<string>> ReadXlsx(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rns = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";
        XDocument Load(string path) => XDocument.Load(zip.GetEntry(path)!.Open());
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") != null)
            foreach (var si in Load("xl/sharedStrings.xml").Root!.Elements(ns + "si"))
                shared.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));
        var sheetPath = "xl/worksheets/sheet1.xml";
        var workbook = Load("xl/workbook.xml");
        var first = workbook.Root!.Element(ns + "sheets")?.Elements(ns + "sheet").FirstOrDefault();
        if (first?.Attribute(rns + "id")?.Value is { } rid && zip.GetEntry("xl/_rels/workbook.xml.rels") != null)
        {
            var target = Load("xl/_rels/workbook.xml.rels").Root!.Elements(pr + "Relationship")
                .FirstOrDefault(r => r.Attribute("Id")?.Value == rid)?.Attribute("Target")?.Value;
            if (target != null) sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        }
        var rows = new List<List<string>>();
        foreach (var row in Load(sheetPath).Descendants(ns + "row"))
        {
            var cells = new List<string>();
            foreach (var c in row.Elements(ns + "c"))
            {
                var col = ColumnIndex(c.Attribute("r")?.Value ?? "");
                while (col >= 0 && cells.Count < col) cells.Add("");
                var type = c.Attribute("t")?.Value;
                var v = c.Element(ns + "v")?.Value;
                string text = type switch
                {
                    "s" => int.TryParse(v, out var i) && i < shared.Count ? shared[i] : "",
                    "inlineStr" => string.Concat(c.Descendants(ns + "t").Select(t => t.Value)),
                    "b" => v == "1" ? "True" : "False",
                    _ => NumberText(v),
                };
                cells.Add(text.Trim());
            }
            rows.Add(cells);
        }
        return rows;
    }

    static int ColumnIndex(string reference)
    {
        int n = 0;
        foreach (var ch in reference.TakeWhile(char.IsLetter)) n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        return n - 1;
    }

    /// <summary>A number as the web version's spreadsheet reader writes it: 2023, not 2023.0.</summary>
    static string NumberText(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);
        return v;
    }

    public static readonly string[] TitleHeaders = { "title", "track title", "track name", "song", "song title", "name", "track" };

    static string Head(string c) => c.ToLowerInvariant().Trim(' ', ':', '#');

    /// <summary>[{n, artist, title}] from the table, whatever other columns it has.</summary>
    public static List<WantedTrack> PickTracks(List<List<string>> rows, int maxRows = 500)
    {
        rows = rows.Where(r => r.Any(c => c.Length > 0)).ToList();
        int? artistCol = null, titleCol = null;
        int start = 0;
        for (int i = 0; i < Math.Min(15, rows.Count); i++)
        {
            var heads = rows[i].Select(Head).ToList();
            if (heads.Contains("artist") || heads.Contains("artists"))
            {
                artistCol = heads.Contains("artist") ? heads.IndexOf("artist") : heads.IndexOf("artists");
                foreach (var want in TitleHeaders)
                {
                    var found = Enumerable.Range(0, heads.Count).Where(j => heads[j] == want && j != artistCol).ToList();
                    if (found.Count > 0) { titleCol = found[0]; break; }
                }
                if (titleCol == null) throw new TracklistException("Found an \"Artist\" column but no \"Title\" column.");
                start = i + 1;
                break;
            }
        }
        var outp = new List<WantedTrack>();
        foreach (var row in rows.Skip(start))
        {
            string Cell(int? j) => j != null && j < row.Count ? row[j.Value].Trim() : "";
            string artist, title;
            if (artistCol != null) { artist = Cell(artistCol); title = Cell(titleCol); }
            else if (row.Count >= 3 && Regex.IsMatch(row[0].Trim().Length > 0 ? row[0].Trim() : "x", @"^\d+[.)]?$"))
            { artist = row[1].Trim(); title = row[2].Trim(); }                     // number, artist, title
            else if (row.Count >= 2 && row[1].Trim().Length > 0) { artist = row[0].Trim(); title = row[1].Trim(); }
            else                                                                    // "Artist - Title"
            {
                var line = Regex.Replace(row[0], @"^\s*\d+[.)]?\s+", "");
                var parts = Regex.Split(line, @"\s+[-–—]\s+");
                if (parts.Length >= 2)
                {
                    var m = Regex.Match(line, @"\s+[-–—]\s+");
                    artist = line[..m.Index].Trim();
                    title = line[(m.Index + m.Length)..].Trim();
                }
                else { artist = ""; title = line.Trim(); }
            }
            if (artist.Length > 0 || title.Length > 0) outp.Add(new WantedTrack(outp.Count + 1, artist, title));
        }
        if (outp.Count == 0) throw new TracklistException("No tracks found. The list needs an artist and a title for each track.");
        if (outp.Count > maxRows) throw new TracklistException($"That list has {outp.Count} tracks; the limit is {maxRows} at a time.");
        return outp;
    }

    // ---------- your own list: how names should be written, and links you already have ----------

    public static readonly Regex Url = new(@"(?:https?://[^\s<>""']+|(?:[\w-]+\.)?(?:bandcamp|soundcloud)\.com/[^\s<>""']*|[\w-]+\.bandcamp\.com\b)",
                                           RegexOptions.IgnoreCase);
    static readonly string[] LinkHeaders = { "link", "links", "url", "urls", "bandcamp", "soundcloud", "buy", "download", "release link", "bandcamp link",
                                             "soundcloud link", "bc", "sc", "where" };
    static readonly string[] NoteHeaders = { "note", "notes", "comment", "comments", "info", "link note", "remarks" };
    static readonly string[] YearHeaders = { "year", "released", "release year", "release date", "date" };

    /// <summary>A cell that should hold a link, taken apart: ([links], whatever else was written there).</summary>
    public static (List<string> Links, string Other) SplitLinkCell(string text)
    {
        var links = new List<string>();
        foreach (Match m in Url.Matches(text ?? ""))
        {
            var link = m.Value.TrimEnd(')', '.', ',', ';', ']', '>');
            if (!link.Contains("://")) link = "https://" + link;
            if (!links.Contains(link)) links.Add(link);
        }
        var rest = Url.Replace(text ?? "", " ");
        var notes = Regex.Split(rest, @"[\(\)\[\]]").Select(n => n.Trim(' ', '-', '–', '—', '|', ',', ';', ':')).Where(n => n.Length > 0).Distinct();
        return (links, string.Join("; ", notes));
    }

    /// <summary>How much a link says about where a track is (lower is better).</summary>
    public static int LinkRank(string? url)
    {
        if (string.IsNullOrEmpty(url)) return 9;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return 2;
        var host = u.Authority.ToLowerInvariant();
        var path = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (host.Contains("bandcamp.com")) return path.Length > 0 && path[0] is "album" or "track" ? 0 : 3;
        if (host.Contains("soundcloud.com")) return path.Length >= 2 ? 1 : 4;
        return 2;
    }

    public static string BestLink(List<string> links) =>
        links.Count == 0 ? "" : links.Select((u, i) => (u, i)).OrderBy(x => LinkRank(x.u)).ThenBy(x => x.i).First().u;

    /// <summary>One entry of your list.</summary>
    public sealed record Known(string Artist, string Title, string Year, string Url, string Others, string Note);

    /// <summary>[{artist, title, year, url, others, note}] from your list.</summary>
    public static List<Known> ReadKnown(List<List<string>> rows)
    {
        rows = rows.Where(r => r.Any(c => c.Length > 0)).ToList();
        int? artistCol = null, titleCol = null, yearCol = null;
        var linkCols = new List<int>();
        var noteCols = new List<int>();
        var blankCols = new HashSet<int>();
        int start = 0;
        for (int i = 0; i < Math.Min(15, rows.Count); i++)
        {
            var heads = rows[i].Select(Head).ToList();
            if (heads.Contains("artist") || heads.Contains("artists"))
            {
                artistCol = heads.Contains("artist") ? heads.IndexOf("artist") : heads.IndexOf("artists");
                foreach (var want in TitleHeaders)
                {
                    var found = Enumerable.Range(0, heads.Count).Where(j => heads[j] == want && j != artistCol).ToList();
                    if (found.Count > 0) { titleCol = found[0]; break; }
                }
                linkCols = Enumerable.Range(0, heads.Count).Where(j => LinkHeaders.Contains(heads[j])).ToList();
                noteCols = Enumerable.Range(0, heads.Count).Where(j => NoteHeaders.Contains(heads[j])).ToList();
                var years = Enumerable.Range(0, heads.Count).Where(j => YearHeaders.Contains(heads[j]) && j != artistCol && j != titleCol).ToList();
                yearCol = years.Count > 0 ? years[0] : null;
                blankCols = Enumerable.Range(0, heads.Count).Where(j => heads[j].Length == 0).ToHashSet();
                start = i + 1;
                break;
            }
        }
        var width = rows.Count > 0 ? rows.Max(r => r.Count) : 0;
        if (artistCol != null)
            for (int j = rows[start - 1].Count; j < width; j++) blankCols.Add(j);
        var outp = new List<Known>();
        foreach (var row in rows.Skip(start))
        {
            string Cell(int? j) => j != null && j < row.Count ? row[j.Value].Trim() : "";
            string artist, title, year;
            HashSet<int> used;
            if (artistCol != null)
            {
                artist = Cell(artistCol); title = Cell(titleCol);
                used = new HashSet<int>(blankCols);
                foreach (var j in new[] { artistCol, titleCol, yearCol }) if (j != null) used.Add(j.Value);
                year = Cell(yearCol);
            }
            else
            {
                var first = row.Count >= 3 && Regex.IsMatch(row[0].Trim().Length > 0 ? row[0].Trim() : "x", @"^\d+[.)]?$") ? 1 : 0;
                artist = Cell(first); title = Cell(first + 1);
                used = new HashSet<int> { first, first + 1 };
                if (first == 1) used.Add(0);
                if (Url.IsMatch(title))                     // artist, link: no title column at all
                {
                    title = "";
                    used = new HashSet<int> { first };
                    if (first == 1) used.Add(0);
                }
                year = Enumerable.Range(0, row.Count).Where(j => !used.Contains(j) && Regex.IsMatch(row[j].Trim(), "^(19|20)[0-9][0-9]$"))
                    .Select(j => row[j].Trim()).FirstOrDefault() ?? "";
            }
            var foundYear = Regex.Match(year, @"\b(19|20)\d\d\b");
            var links = new List<string>();
            var notes = new List<string>();
            for (int j = 0; j < row.Count; j++)
            {
                var text = row[j];
                if (used.Contains(j) || text.Trim().Length == 0) continue;
                if (noteCols.Contains(j)) notes.Add(text.Trim());
                else if (linkCols.Contains(j) || Url.IsMatch(text))
                {
                    var (found, rest) = SplitLinkCell(text);
                    links.AddRange(found.Where(u => !links.Contains(u)));
                    if (rest.Length > 0) notes.Add(rest);
                }
            }
            if (artist.Length > 0 || title.Length > 0)
            {
                var best = BestLink(links);
                outp.Add(new Known(artist, title, foundYear.Success ? foundYear.Value : "", best,
                    string.Join(" ", links.Where(u => u != best)), string.Join("; ", notes.Distinct())));
            }
        }
        if (outp.Count == 0) throw new TracklistException("Nothing to import. The list needs an artist column and, for tracks, a title column.");
        return outp;
    }
}
