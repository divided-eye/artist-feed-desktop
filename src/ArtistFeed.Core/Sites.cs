using System.Text.Json.Nodes;

namespace ArtistFeed.Core;

/// <summary>
/// Which sites are switched on, and the per-artist ticks of Tracked Artists. Stored in the same
/// places as the web app, so a copied database keeps its settings.
/// A site that is switched off is gone from the app: no button, no column, no refreshes, its posts
/// left out of the feed. Pages tracked on it are kept, so switching it back on brings them back.
/// </summary>
public sealed class Sites(Db db, IReadOnlyList<ISource> sources)
{
    public IReadOnlyList<ISource> All => sources;

    public ISource? Get(string key) => sources.FirstOrDefault(s => s.Key == key);

    public bool IsOn(string key) => Get(key) != null && !db.KvList("sites_off").Contains(key);

    /// <summary>The keys of the sites switched on, in the order they are shown.</summary>
    public List<string> Enabled()
    {
        var off = db.KvList("sites_off").ToHashSet();
        return sources.Where(s => !off.Contains(s.Key)).Select(s => s.Key).ToList();
    }

    public void SetOn(string key, bool on)
    {
        if (Get(key) == null) throw new ArgumentException($"Unknown site '{key}'.");
        var off = db.KvList("sites_off").ToHashSet();
        if (on) off.Remove(key); else off.Add(key);
        if (off.Count >= sources.Count) throw new InvalidOperationException("At least one site has to stay on.");
        db.KvSetList("sites_off", off.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>Tidied names of the artists and labels Link Finder may search YouTube for.</summary>
    public HashSet<string> YoutubeNames() => db.KvList("youtube_lookup").ToHashSet();

    public void SetYoutubeNames(IEnumerable<string> names) =>
        db.KvSetList("youtube_lookup", names.Where(n => n.Length > 0).Distinct().OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>Tidied names of the artists ticked in the Focus column of Tracked Artists.</summary>
    public HashSet<string> FocusNames() => db.KvList("focus_names").ToHashSet();

    public void SetFocusNames(IEnumerable<string> names) =>
        db.KvSetList("focus_names", names.Where(n => n.Length > 0).Distinct().OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>The ids of the artists ticked in the Focus column.</summary>
    public List<long> FocusIds() => WorkspaceIds("focus") ?? new();

    // ---------- workspaces: named groups of artists, each a filter for the feed (Focus is the first) ----------
    // Kept as the web app keeps them: kv "workspaces" = [{"id", "name", "members": [tidied names]}].

    public List<Workspace> Workspaces()
    {
        if (Html.ParseJson(db.KvGet("workspaces")) is not JsonArray saved)      // from before workspaces: Focus becomes the first
            return new() { new Workspace("focus", "Focus", FocusNames().OrderBy(x => x, StringComparer.Ordinal).ToList()) };
        var list = new List<Workspace>();
        foreach (var w in saved)
        {
            if (w is not JsonObject o || Html.AsString(o["id"]) is not { Length: > 0 } id || Html.AsString(o["name"]) is not { Length: > 0 } name) continue;
            var members = (o["members"] as JsonArray)?.Select(Html.AsString).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList() ?? new();
            list.Add(new Workspace(id, name, members));
        }
        return list;
    }

    public void SetWorkspaces(IEnumerable<Workspace> groups)
    {
        var list = groups.Select(w => new Workspace(w.Id, w.Name.Trim(),
            w.Members.Where(n => n.Length > 0).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList())).ToList();
        var arr = new JsonArray();
        foreach (var w in list)
            arr.Add(new JsonObject
            {
                ["id"] = w.Id, ["name"] = w.Name,
                ["members"] = new JsonArray(w.Members.Select(m => (JsonNode?)m).ToArray()),
            });
        db.KvSet("workspaces", arr.ToJsonString());
        SetFocusNames(list.FirstOrDefault(w => w.Id == "focus")?.Members ?? new List<string>());   // for older versions
    }

    /// <summary>The ids of the artists in a workspace; null when none is chosen.</summary>
    public List<long>? WorkspaceIds(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var names = Workspaces().FirstOrDefault(w => w.Id == id)?.Members.ToHashSet() ?? new HashSet<string>();
        return db.Query("SELECT id, name FROM artists").Where(r => names.Contains(Names.Norm((string)r["name"]!)))
            .Select(r => (long)r["id"]!).ToList();
    }

    /// <summary>The workspaces with how many tracked artists each holds.</summary>
    public List<(Workspace Space, int Count)> WorkspaceCounts()
    {
        var names = db.Query("SELECT name FROM artists").Select(r => Names.Norm((string)r["name"]!)).ToHashSet();
        return Workspaces().Select(w => (w, w.Members.Count(names.Contains))).ToList();
    }
}

public sealed record Workspace(string Id, string Name, List<string> Members);
