using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ArtistFeed.Core;

/// <summary>One row of Tracked Artists: an artist or label, and its pages on each site.</summary>
public sealed class TableRow
{
    public long? Id { get; set; }
    public string Name { get; set; } = "";
    public Dictionary<string, string> Cells { get; set; } = new();
    public bool? YoutubeLookup { get; set; }          // Link Finder may search YouTube for this artist or label
    public bool KeepSeparate { get; set; }            // shares a name with another row on purpose (different artists)
    public bool SameName { get; set; }
    public bool Focus { get; set; }                   // in the Focus workspace (older form of Groups = ["focus"])
    public List<string> Groups { get; set; } = new(); // the workspaces this artist is in, by id
    public bool Remove { get; set; }                  // marked for removal: stops being tracked, like a blank row
    public long PostCount { get; set; }
    public List<string> Problems { get; set; } = new();
    public List<string> Skip { get; set; } = new();   // cells cleared on purpose (kept for Find missing links)
    public Dictionary<string, List<string>> Rejected { get; set; } = new();
    public List<string> Searched { get; set; } = new(); // sites Find missing links has already searched for this artist
}

public sealed record RowError(int Row, string Message, string Kind = "");

public sealed class TableSaveException(List<RowError> errors) : Exception("Some rows need fixing; nothing was saved.")
{
    public List<RowError> Errors { get; } = errors;
}

/// <summary>The artist list as one table: a row per artist, a column per site.</summary>
public sealed class ArtistTable(Db db, Sites sites)
{
    Dictionary<string, JsonObject> LeftEmpty()
    {
        try
        {
            if (Html.ParseJson(db.KvGet("table_left_empty") ?? "{}") is JsonObject o)
                return o.Where(kv => kv.Value is JsonObject).ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value!);
        }
        catch (JsonException) { }
        return new();
    }

    HashSet<string> SameNameOk() => db.KvList("same_name_ok").ToHashSet();

    // Find missing links remembers which sites it has already searched for each artist, so later runs can
    // leave those rows alone ("Skip rows already searched"). Kept by tidied name: {name: {site: when}}
    // (the same "suggest_searched" entry as the web version).
    public Dictionary<string, Dictionary<string, string>> Searched()
    {
        var all = new Dictionary<string, Dictionary<string, string>>();
        try
        {
            if (JsonNode.Parse(db.KvGet("suggest_searched") ?? "{}") is JsonObject saved)
                foreach (var (name, sitesDone) in saved)
                    if (sitesDone is JsonObject o)
                        all[name] = o.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => Html.AsString(kv.Value) ?? "");
        }
        catch (System.Text.Json.JsonException) { }
        return all;
    }

    /// <summary>The key a row is remembered by: its first name tidied, or its links when it has no name yet.</summary>
    public static string SearchKey(string name, IReadOnlyDictionary<string, string> cells)
    {
        var first = Names.SplitNames(name ?? "").FirstOrDefault() ?? "";
        if (first.Trim().Length > 0) return Names.Norm(first);
        return "~" + string.Join("|", cells.Where(kv => (kv.Value ?? "").Trim().Length > 0)
            .Select(kv => $"{kv.Key}={kv.Value.Trim()}").OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>Note that these sites were searched for this row (under the name typed, and the name a page gave).</summary>
    public void RememberSearched(string name, IReadOnlyDictionary<string, string> cells, string foundName, IReadOnlyCollection<string> looked)
    {
        if (looked.Count == 0) return;
        var all = Searched();
        var now = Db.Now();
        var keys = new HashSet<string> { SearchKey(name, cells) };
        if (foundName.Trim().Length > 0) keys.Add(SearchKey(foundName, new Dictionary<string, string>()));
        keys.Remove("~");
        foreach (var key in keys)
        {
            if (!all.TryGetValue(key, out var done)) all[key] = done = new();
            foreach (var site in looked) done[site] = now;
        }
        var json = new JsonObject();
        foreach (var (k, v) in all)
        {
            var o = new JsonObject();
            foreach (var (site, when) in v) o[site] = when;
            json[k] = o;
        }
        db.KvSet("suggest_searched", json.ToJsonString());
    }

    public List<TableRow> Rows()
    {
        var rows = new List<TableRow>();
        var remembered = LeftEmpty();
        var on = sites.Enabled();
        var youtube = sites.YoutubeNames();
        var same = SameNameOk();
        var groups = sites.Workspaces();
        var searched = Searched();
        foreach (var a in db.ListArtists())
        {
            var cells = on.ToDictionary(k => k, _ => new List<string>());
            var problems = new List<string>();
            foreach (var acc in a.Accounts)
            {
                var source = sites.Get(acc.Source);
                if (source == null || !cells.ContainsKey(source.Key)) continue;    // a site switched off: kept, not shown
                var shortName = source.Short(acc.Handle);
                cells[source.Key].Add(shortName);
                if (!string.IsNullOrEmpty(acc.LastError)) problems.Add($"{source.Label} {shortName}: {acc.LastError}");
                else if (string.IsNullOrEmpty(acc.LastChecked)) problems.Add($"{source.Label} {shortName}: checking…");
            }
            var key = Names.Norm(a.Name);
            remembered.TryGetValue(key, out var note);
            var skip = (note?["skip"] as JsonArray)?.Select(Html.AsString).Where(k => k != null && cells.ContainsKey(k) && cells[k!].Count == 0)
                .Select(k => k!).ToList() ?? new();
            var rejected = new Dictionary<string, List<string>>();
            if (note?["rejected"] is JsonObject rej)
                foreach (var (k, v) in rej)
                    if (v is JsonArray arr) rejected[k] = arr.Select(Html.AsString).Where(x => x != null).Select(x => x!).ToList();
            rows.Add(new TableRow
            {
                Id = a.Id,
                Name = Names.JoinNames(new[] { a.Name }.Concat(Names.SplitNames(a.Aliases))),
                PostCount = a.PostCount,
                YoutubeLookup = youtube.Contains(key),
                Groups = groups.Where(w => w.Members.Contains(key)).Select(w => w.Id).ToList(),
                Focus = groups.Any(w => w.Id == "focus" && w.Members.Contains(key)),
                SameName = same.Contains(key),
                Cells = cells.ToDictionary(kv => kv.Key, kv => string.Join(", ", kv.Value)),
                Problems = problems,
                Skip = skip,
                Rejected = rejected,
                Searched = searched.TryGetValue(key, out var done) ? done.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList() : new(),
            });
        }
        return rows;
    }

    /// <summary>Replace the whole artist list with these rows. Nothing changes unless every row is valid.
    /// Returns the ids of the pages added, which want reading straight away.</summary>
    /// <param name="spaces">The workspaces as they should be (id, name), in order; null leaves them as they are.</param>
    public List<long> Save(IReadOnlyList<TableRow> rows, IReadOnlyList<(string Id, string Name)>? spaces = null)
    {
        var groups = (spaces ?? sites.Workspaces().Select(w => (w.Id, w.Name)).ToList())
            .Select(w => (Id: w.Id.Trim(), Name: w.Name.Trim())).ToList();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in groups)
        {
            if (w.Id.Length == 0 || w.Name.Length == 0)
                throw new TableSaveException(new() { new RowError(-1, "Every workspace needs a name.") });
            if (!seenNames.Add(w.Name))
                throw new TableSaveException(new() { new RowError(-1, $"Two workspaces are called \"{w.Name}\"; rename one.") });
        }
        var members = groups.ToDictionary(w => w.Id, _ => new HashSet<string>());
        var existing = db.ListArtists().ToDictionary(a => a.Id);
        var errors = new List<RowError>();
        var claimed = new Dictionary<(string, string), int>();
        var plan = new List<(long? Id, string Name, List<(string Source, string Handle, string Url)> Pages, string Aliases)>();
        var remember = new JsonObject();
        var youtube = new HashSet<string>();
        var claimedNames = new Dictionary<string, List<int>>();
        var allowed = SameNameOk();
        var on = sites.Enabled().ToHashSet();
        for (int index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Remove) continue;                       // left out: an existing artist stops being tracked
            var pages = new List<(string, string, string)>();
            foreach (var (key, text) in row.Cells)
            {
                if (sites.Get(key) != null && !on.Contains(key)) continue;   // a site switched off: its pages are left as they are
                foreach (var piece in Regex.Split(text ?? "", "[,;\n]+").Select(t => t.Trim()).Where(t => t.Length > 0))
                {
                    var source = sites.Get(key);
                    if (source == null)
                    {
                        errors.Add(new RowError(index, $"Unknown site '{key}'."));
                        continue;
                    }
                    (string Handle, string Url) norm;
                    try { norm = source.Normalize(piece); }
                    catch (SourceException e)
                    {
                        errors.Add(new RowError(index, $"{source.Label} \"{piece}\": {e.Message}"));
                        continue;
                    }
                    if (claimed.TryGetValue((key, norm.Handle), out var other) && other != index)
                    {
                        errors.Add(new RowError(index, $"{norm.Url} is also on row {other + 1}."));
                        continue;
                    }
                    if (!claimed.ContainsKey((key, norm.Handle)))
                    {
                        claimed[(key, norm.Handle)] = index;
                        pages.Add((key, norm.Handle, norm.Url));
                    }
                }
            }
            var names = Names.SplitNames(row.Name);       // the artist's name, then other names they go by
            var name = names.Count > 0 ? names[0] : "";
            var aliases = Names.JoinNames(names.Skip(1));
            if (name.Length == 0 && pages.Count == 0) continue;    // a blank row; for an existing artist this removes them
            foreach (var n in names.Select(Names.Norm).Distinct())
            {
                if (n.Length == 0) continue;
                if (!claimedNames.TryGetValue(n, out var list)) claimedNames[n] = list = new List<int>();
                list.Add(index);
                if (row.KeepSeparate) allowed.Add(n);
            }
            var finalName = name.Length > 0 ? name : pages[0].Item2;
            plan.Add((row.Id != null && existing.ContainsKey(row.Id.Value) ? row.Id : null, finalName, pages, aliases));
            var filled = pages.Select(p => p.Item1).ToHashSet();
            var skip = row.Skip.Where(k => sites.Get(k) != null && !filled.Contains(k)).ToList();
            var rejected = row.Rejected.Where(kv => sites.Get(kv.Key) != null && kv.Value.Count > 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().Take(20).ToList());
            if (skip.Count > 0 || rejected.Count > 0)
            {
                var r = new JsonObject();
                foreach (var (k, v) in rejected) r[k] = new JsonArray(v.Select(x => (JsonNode?)x).ToArray());
                remember[Names.Norm(finalName)] = new JsonObject
                {
                    ["skip"] = new JsonArray(skip.Select(x => (JsonNode?)x).ToArray()),
                    ["rejected"] = r,
                };
            }
            foreach (var gid in row.Groups.Concat(row.Focus ? new[] { "focus" } : Array.Empty<string>()).Distinct())
                if (members.TryGetValue(gid, out var set)) set.Add(Names.Norm(finalName));
            if (row.YoutubeLookup == true)
                foreach (var n in new[] { finalName }.Concat(Names.SplitNames(aliases))) youtube.Add(Names.Norm(n));
        }
        // Two rows for one artist (by any of their names) have to be merged, renamed, or kept apart on purpose.
        var shared = claimedNames.Where(kv => kv.Value.Count > 1).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (n, list) in shared)
        {
            if (allowed.Contains(n)) continue;
            foreach (var i in list)
            {
                var others = string.Join(", ", list.Where(j => j != i).Select(j => (j + 1).ToString()));
                errors.Add(new RowError(i, $"Same name as row {others}: merge the rows (keep every page in one), rename one, " +
                                           "or keep both if they are different artists.", "same_name"));
            }
        }
        if (errors.Count > 0) throw new TableSaveException(errors);

        var keep = new List<long>();
        var dropAccounts = new List<long>();
        var renames = new List<(long, string, string)>();
        var newAccounts = new List<(long, string, string, string)>();
        var newArtists = new List<(string, string, List<(string, string, string)>)>();
        foreach (var (artistId, name, pages, aliases) in plan)
        {
            if (artistId == null || keep.Contains(artistId.Value))
            {
                newArtists.Add((name, aliases, pages));
                continue;
            }
            keep.Add(artistId.Value);
            var current = existing[artistId.Value];
            if (name != current.Name || aliases != (current.Aliases ?? "")) renames.Add((artistId.Value, name, aliases));
            var have = current.Accounts.Where(a => on.Contains(a.Source)).ToDictionary(a => (a.Source, a.Handle), a => a.Id);
            var wanted = pages.Select(p => (p.Source, p.Handle)).ToHashSet();
            dropAccounts.AddRange(have.Where(kv => !wanted.Contains(kv.Key)).Select(kv => kv.Value));
            newAccounts.AddRange(pages.Where(p => !have.ContainsKey((p.Source, p.Handle))).Select(p => (artistId.Value, p.Source, p.Handle, p.Url)));
        }
        List<long> added;
        try
        {
            added = db.ApplyTable(keep, dropAccounts, renames, newAccounts, newArtists);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)     // a page tracked twice (UNIQUE)
        {
            throw new TableSaveException(new() { new RowError(-1, "A page is listed twice; nothing was saved.") });
        }
        db.KvSet("table_left_empty", remember.ToJsonString());
        db.KvSetList("same_name_ok", shared.Keys.Where(allowed.Contains).OrderBy(x => x, StringComparer.Ordinal));
        sites.SetWorkspaces(groups.Select(w => new Workspace(w.Id, w.Name, members[w.Id].ToList())));
        if (rows.Any(r => r.YoutubeLookup != null)) sites.SetYoutubeNames(youtube);    // sent only while YouTube is on
        return added;
    }

    /// <summary>Tracked Artists as a spreadsheet: name, then a column per site switched on.</summary>
    public string ExportCsv()
    {
        var on = sites.Enabled();
        var sb = new StringBuilder();
        sb.Append(Csv.Line(new[] { "name" }.Concat(on))).Append("\r\n");
        foreach (var row in Rows())
            sb.Append(Csv.Line(new[] { row.Name }.Concat(on.Select(k => row.Cells.GetValueOrDefault(k) ?? "")))).Append("\r\n");
        return sb.ToString();
    }
}
