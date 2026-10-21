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
    public List<long> FocusIds()
    {
        var names = FocusNames();
        return db.Query("SELECT id, name FROM artists").Where(r => names.Contains(Names.Norm((string)r["name"]!)))
            .Select(r => (long)r["id"]!).ToList();
    }
}
