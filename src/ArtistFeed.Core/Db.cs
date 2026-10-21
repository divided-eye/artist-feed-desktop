using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace ArtistFeed.Core;

/// <summary>
/// SQLite storage: one file on disk. The tables are the same as the Artist Feed web app's
/// (data/feed.db), so a copy of that file opens here with everything in it.
/// </summary>
public sealed class Db : IDisposable
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS artists(
          id INTEGER PRIMARY KEY,
          name TEXT NOT NULL,
          created_at TEXT NOT NULL,
          aliases TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE IF NOT EXISTS accounts(
          id INTEGER PRIMARY KEY,
          artist_id INTEGER NOT NULL REFERENCES artists(id) ON DELETE CASCADE,
          source TEXT NOT NULL,
          handle TEXT NOT NULL,
          url TEXT NOT NULL,
          display_name TEXT,
          avatar TEXT,
          meta TEXT NOT NULL DEFAULT '{}',
          last_checked TEXT,
          last_error TEXT,
          UNIQUE(source, handle)
        );
        CREATE TABLE IF NOT EXISTS posts(
          id INTEGER PRIMARY KEY,
          account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
          external_id TEXT NOT NULL,
          title TEXT NOT NULL,
          url TEXT NOT NULL,
          image TEXT,
          kind TEXT,
          author TEXT,
          published_at TEXT,
          first_seen TEXT NOT NULL,
          label TEXT,
          release TEXT,
          embed TEXT,
          detailed INTEGER NOT NULL DEFAULT 0,
          hidden INTEGER NOT NULL DEFAULT 0,
          posted_at TEXT,
          UNIQUE(account_id, external_id)
        );
        CREATE TABLE IF NOT EXISTS tracks(
          id INTEGER PRIMARY KEY,
          post_id INTEGER NOT NULL REFERENCES posts(id) ON DELETE CASCADE,
          position INTEGER NOT NULL,
          title TEXT NOT NULL,
          artist TEXT,
          duration REAL,
          ext_id TEXT,
          url TEXT
        );
        CREATE INDEX IF NOT EXISTS tracks_post ON tracks(post_id, position);
        CREATE INDEX IF NOT EXISTS posts_url ON posts(url);
        CREATE INDEX IF NOT EXISTS posts_when ON posts(COALESCE(published_at, first_seen) DESC);
        CREATE TABLE IF NOT EXISTS kv(key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS played(post_id INTEGER NOT NULL REFERENCES posts(id) ON DELETE CASCADE,
          track TEXT NOT NULL DEFAULT '', at TEXT NOT NULL, PRIMARY KEY(post_id, track));
        CREATE TABLE IF NOT EXISTS hidden_tracks(post_id INTEGER NOT NULL REFERENCES posts(id) ON DELETE CASCADE,
          track TEXT NOT NULL, at TEXT NOT NULL, PRIMARY KEY(post_id, track));
        CREATE TABLE IF NOT EXISTS lookup_cache(key TEXT PRIMARY KEY, result TEXT NOT NULL, at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS web_cache(kind TEXT NOT NULL, key TEXT NOT NULL, data TEXT NOT NULL, at TEXT NOT NULL,
          PRIMARY KEY(kind, key));
        CREATE TABLE IF NOT EXISTS collection_items(key TEXT PRIMARY KEY, fan TEXT NOT NULL, title TEXT NOT NULL,
          band TEXT NOT NULL, url TEXT NOT NULL, kind TEXT NOT NULL,
          preorder INTEGER NOT NULL DEFAULT 0, release_date TEXT NOT NULL DEFAULT '', checked_at TEXT NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS download_alerts(key TEXT PRIMARY KEY, reason TEXT NOT NULL, at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS collection_tracks(item_key TEXT NOT NULL, position INTEGER, title TEXT NOT NULL, artist TEXT);
        CREATE INDEX IF NOT EXISTS collection_tracks_item ON collection_tracks(item_key);
        CREATE TABLE IF NOT EXISTS known_tracks(key TEXT NOT NULL, variant TEXT NOT NULL DEFAULT '', artist TEXT NOT NULL,
          title TEXT NOT NULL, year TEXT NOT NULL DEFAULT '', url TEXT NOT NULL DEFAULT '', others TEXT NOT NULL DEFAULT '',
          note TEXT NOT NULL DEFAULT '', at TEXT NOT NULL, PRIMARY KEY(key, variant));
        CREATE TABLE IF NOT EXISTS known_artists(key TEXT PRIMARY KEY, name TEXT NOT NULL, at TEXT NOT NULL);
        """;

    /// <summary>Raise when the sources start collecting something new: posts stored with a lower
    /// number are read again (a few per refresh) to pick it up. Same number as the web app.</summary>
    public const int DetailVersion = 3;

    static readonly (string Name, string Kind)[] AddedPostColumns =
    {
        ("label", "TEXT"), ("release", "TEXT"), ("embed", "TEXT"),
        ("detailed", "INTEGER NOT NULL DEFAULT 0"), ("posted_at", "TEXT"), ("hidden", "INTEGER NOT NULL DEFAULT 0"),
    };

    readonly object _lock = new();
    readonly SqliteConnection _conn;
    public string Path { get; }

    public Db(string path)
    {
        Path = path;
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _conn.Open();
        Exec("PRAGMA foreign_keys=ON");
        Exec("PRAGMA journal_mode=WAL");
        var cols = Columns("known_tracks");
        if (cols.Count > 0 && !cols.Contains("variant")) Exec("DROP TABLE known_tracks");   // an earlier layout
        Exec(Schema);
        Migrate();
        Exec("CREATE INDEX IF NOT EXISTS posts_embed ON posts(embed)");
    }

    public void Dispose()
    {
        lock (_lock) _conn.Dispose();
    }

    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    HashSet<string> Columns(string table) =>
        Query($"PRAGMA table_info({table})").Select(r => (string)r["name"]!).ToHashSet();

    void Migrate()
    {
        var have = Columns("posts");
        foreach (var (name, kind) in AddedPostColumns)
            if (!have.Contains(name)) Exec($"ALTER TABLE posts ADD COLUMN {name} {kind}");
        var ci = Columns("collection_items");
        foreach (var (name, kind) in new[] { ("preorder", "INTEGER NOT NULL DEFAULT 0"), ("release_date", "TEXT NOT NULL DEFAULT ''"),
                                             ("checked_at", "TEXT NOT NULL DEFAULT ''") })
            if (ci.Count > 0 && !ci.Contains(name)) Exec($"ALTER TABLE collection_items ADD COLUMN {name} {kind}");
        // Once: Bandcamp releases saved with no tracks are read again, so one that is only shown on an
        // artist's page and sold from another (a label's) gets linked to the page that sells it.
        if (Query("SELECT 1 FROM kv WHERE key='reread_trackless'").Count == 0)
        {
            Exec("UPDATE posts SET detailed=0 WHERE id IN (SELECT p.id FROM posts p JOIN accounts a ON a.id=p.account_id" +
                 " WHERE a.source='bandcamp' AND NOT EXISTS(SELECT 1 FROM tracks t WHERE t.post_id=p.id))");
            Exec("INSERT OR REPLACE INTO kv(key, value) VALUES('reread_trackless', '1')");
        }
        var ar = Columns("artists");
        if (ar.Count > 0 && !ar.Contains("aliases")) Exec("ALTER TABLE artists ADD COLUMN aliases TEXT NOT NULL DEFAULT ''");
    }

    // ---------- plumbing ----------

    static void Bind(SqliteCommand cmd, IReadOnlyList<object?> args)
    {
        for (int i = 0; i < args.Count; i++)
            cmd.Parameters.AddWithValue("$" + (i + 1), args[i] ?? DBNull.Value);
    }

    /// <summary>"?" placeholders, like the web app's SQL, become $1, $2 ...</summary>
    static string Numbered(string sql)
    {
        var sb = new System.Text.StringBuilder(sql.Length + 16);
        int n = 0;
        bool inString = false;
        foreach (var ch in sql)
        {
            if (ch == '\'') inString = !inString;
            if (ch == '?' && !inString) sb.Append('$').Append(++n);
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    public List<Dictionary<string, object?>> Query(string sql, params object?[] args)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = Numbered(sql);
            Bind(cmd, args);
            using var r = cmd.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (r.Read())
            {
                var row = new Dictionary<string, object?>(r.FieldCount);
                for (int i = 0; i < r.FieldCount; i++)
                    row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
    }

    public long Execute(string sql, params object?[] args)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = Numbered(sql);
            Bind(cmd, args);
            cmd.ExecuteNonQuery();
            using var last = _conn.CreateCommand();
            last.CommandText = "SELECT last_insert_rowid()";
            return (long)last.ExecuteScalar()!;
        }
    }

    void Exec(string sql)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Several statements as one all-or-nothing change.</summary>
    public T InTransaction<T>(Func<Tx, T> work)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                var result = work(new Tx(_conn, tx));
                tx.Commit();
                return result;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    public sealed class Tx(SqliteConnection conn, SqliteTransaction tx)
    {
        public long Execute(string sql, params object?[] args)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Numbered(sql);
            Bind(cmd, args);
            cmd.ExecuteNonQuery();
            using var last = conn.CreateCommand();
            last.Transaction = tx;
            last.CommandText = "SELECT last_insert_rowid()";
            return (long)last.ExecuteScalar()!;
        }

        public object? Scalar(string sql, params object?[] args)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Numbered(sql);
            Bind(cmd, args);
            return cmd.ExecuteScalar();
        }
    }

    // ---------- key/value ----------

    public string? KvGet(string key) =>
        Query("SELECT value FROM kv WHERE key=?", key).FirstOrDefault()?["value"] as string;

    public void KvSet(string key, string value) =>
        Execute("INSERT INTO kv(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value);

    public void KvDelete(string key) => Execute("DELETE FROM kv WHERE key=?", key);

    /// <summary>A JSON list of strings stored under a key ("[]" when missing or unreadable).</summary>
    public List<string> KvList(string key, IEnumerable<string>? fallback = null)
    {
        var raw = KvGet(key);
        if (raw != null)
        {
            try
            {
                if (JsonNode.Parse(raw) is JsonArray arr)
                    return arr.Select(x => x?.GetValueKind() == JsonValueKind.String ? x.GetValue<string>() : x?.ToJsonString() ?? "").ToList();
            }
            catch (JsonException) { }
        }
        return fallback?.ToList() ?? new List<string>();
    }

    public void KvSetList(string key, IEnumerable<string> values) =>
        KvSet(key, JsonSerializer.Serialize(values.ToList()));

    // ---------- artists and accounts ----------

    public List<Account> ListAccounts(IReadOnlyCollection<long>? accountIds = null, long? artistId = null)
    {
        string sql = "SELECT * FROM accounts";
        object?[] args = Array.Empty<object?>();
        if (accountIds != null)
        {
            if (accountIds.Count == 0) return new();
            sql += " WHERE id IN (" + string.Join(",", accountIds.Select(_ => "?")) + ")";
            args = accountIds.Cast<object?>().ToArray();
        }
        else if (artistId != null)
        {
            sql += " WHERE artist_id=?";
            args = new object?[] { artistId };
        }
        return Query(sql + " ORDER BY id", args).Select(Account.From).ToList();
    }

    public List<Artist> ListArtists()
    {
        var artists = Query("SELECT * FROM artists ORDER BY name COLLATE NOCASE").Select(r => new Artist
        {
            Id = (long)r["id"]!, Name = (string)r["name"]!, Aliases = r["aliases"] as string ?? "",
        }).ToList();
        var byArtist = ListAccounts().GroupBy(a => a.ArtistId).ToDictionary(g => g.Key, g => g.ToList());
        var counts = Query("SELECT a.artist_id, COUNT(*) n FROM posts p JOIN accounts a ON a.id=p.account_id GROUP BY a.artist_id")
            .ToDictionary(r => (long)r["artist_id"]!, r => (long)r["n"]!);
        foreach (var a in artists)
        {
            a.Accounts = byArtist.GetValueOrDefault(a.Id) ?? new();
            a.PostCount = counts.GetValueOrDefault(a.Id);
        }
        return artists;
    }

    public long AddArtist(string name) => Execute("INSERT INTO artists(name, created_at) VALUES(?,?)", name, Now());

    public long AddAccount(long artistId, string source, string handle, string url) =>
        Execute("INSERT INTO accounts(artist_id, source, handle, url) VALUES(?,?,?,?)", artistId, source, handle, url);

    /// <summary>Make the artist list match an edited table, as one all-or-nothing change.
    /// Returns the ids of the pages that were added.</summary>
    public List<long> ApplyTable(IReadOnlyList<long> keepIds, IEnumerable<long> removeAccountIds,
        IEnumerable<(long Id, string Name, string Aliases)> renames,
        IEnumerable<(long ArtistId, string Source, string Handle, string Url)> newAccounts,
        IEnumerable<(string Name, string Aliases, List<(string Source, string Handle, string Url)> Pages)> newArtists)
    {
        return InTransaction(tx =>
        {
            var added = new List<long>();
            var marks = string.Join(",", keepIds.Select(_ => "?"));
            tx.Execute($"DELETE FROM artists WHERE id NOT IN ({marks})", keepIds.Cast<object?>().ToArray());
            foreach (var id in removeAccountIds) tx.Execute("DELETE FROM accounts WHERE id=?", id);
            foreach (var (id, name, aliases) in renames) tx.Execute("UPDATE artists SET name=?, aliases=? WHERE id=?", name, aliases, id);
            const string insert = "INSERT INTO accounts(artist_id, source, handle, url) VALUES(?,?,?,?)";
            foreach (var (artistId, source, handle, url) in newAccounts) added.Add(tx.Execute(insert, artistId, source, handle, url));
            foreach (var (name, aliases, pages) in newArtists)
            {
                var artistId = tx.Execute("INSERT INTO artists(name, aliases, created_at) VALUES(?,?,?)", name, aliases, Now());
                foreach (var (source, handle, url) in pages) added.Add(tx.Execute(insert, artistId, source, handle, url));
            }
            return added;
        });
    }

    public HashSet<string> KnownIds(long accountId) =>
        Query("SELECT external_id FROM posts WHERE account_id=? AND detailed>=?", accountId, DetailVersion)
            .Select(r => (string)r["external_id"]!).ToHashSet();

    /// <summary>Store what a source returned for one account.</summary>
    public void SaveResult(Account account, FetchResult result)
    {
        var seen = Now();
        InTransaction(tx =>
        {
            foreach (var p in result.Posts)
            {
                tx.Execute(
                    "INSERT INTO posts(account_id, external_id, title, url, image, kind, author," +
                    " published_at, first_seen, label, release, embed, posted_at, detailed)" +
                    " VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?) " +
                    "ON CONFLICT(account_id, external_id) DO UPDATE SET title=excluded.title, url=excluded.url," +
                    " image=COALESCE(excluded.image, image)," +
                    " author=COALESCE(excluded.author, author)," +
                    " published_at=COALESCE(excluded.published_at, published_at)," +
                    " label=COALESCE(excluded.label, label)," +
                    " release=COALESCE(excluded.release, release)," +
                    " embed=COALESCE(excluded.embed, embed)," +
                    " posted_at=COALESCE(excluded.posted_at, posted_at), detailed=excluded.detailed",
                    account.Id, p.ExternalId, p.Title, p.Url, p.Image, p.Kind, p.Author,
                    p.PublishedAt, seen, p.Label, p.Release, p.Embed, p.PostedAt, DetailVersion);
                if (p.Tracks != null)
                {
                    var postId = (long)tx.Scalar("SELECT id FROM posts WHERE account_id=? AND external_id=?", account.Id, p.ExternalId)!;
                    tx.Execute("DELETE FROM tracks WHERE post_id=?", postId);
                    for (int i = 0; i < p.Tracks.Count; i++)
                    {
                        var t = p.Tracks[i];
                        tx.Execute("INSERT INTO tracks(post_id, position, title, artist, duration, ext_id, url) VALUES(?,?,?,?,?,?,?)",
                            postId, i + 1, t.Title, t.Artist, t.Duration, t.ExtId, t.Url);
                    }
                }
            }
            tx.Execute("UPDATE accounts SET display_name=COALESCE(?, display_name), avatar=COALESCE(?, avatar), meta=?," +
                       " last_checked=?, last_error=? WHERE id=?",
                result.DisplayName, result.Avatar, account.Meta.ToJsonString(), seen, result.Warning, account.Id);
            // An artist added without a name takes the name the site reports.
            if (!string.IsNullOrEmpty(result.DisplayName))
                tx.Execute("UPDATE artists SET name=? WHERE id=? AND name IN (SELECT handle FROM accounts WHERE artist_id=?)",
                    result.DisplayName, account.ArtistId, account.ArtistId);
            return 0;
        });
    }

    public void SaveError(long accountId, string message) =>
        Execute("UPDATE accounts SET last_checked=?, last_error=? WHERE id=?", Now(),
            message.Length > 300 ? message[..300] : message, accountId);

    // ---------- the feed ----------

    const string When = "COALESCE(p.published_at, p.first_seen)";
    const string Posted = "p.posted_at";
    const string Joins = "FROM posts p JOIN accounts a ON a.id=p.account_id JOIN artists ar ON ar.id=a.artist_id ";
    const string TArtist = "COALESCE(NULLIF(t.artist,''), NULLIF(p.author,''), ar.name)";
    const string TLabel = "COALESCE(p.label,'')";
    const string TRelease = "COALESCE(NULLIF(p.release,''), CASE WHEN p.kind='album' THEN p.title ELSE '' END)";
    const string TTrack = "COALESCE(t.title, CASE WHEN p.kind='album' THEN '' ELSE p.title END)";
    const string TrackKey = "COALESCE(t.ext_id, t.title, '')";

    static readonly Dictionary<string, string> TrackSorts = new()
    {
        ["artist"] = TArtist, ["label"] = TLabel, ["release"] = TRelease, ["track"] = TTrack,
    };

    public static readonly Dictionary<string, (int Low, int? High)> Lengths = new()
    {
        ["short"] = (0, 180), ["medium"] = (180, 360), ["long"] = (360, 600), ["extended"] = (600, null),
    };

    static string Like(string q) => "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    /// <summary>WHERE clause shared by the grid, the list and the export.</summary>
    (string Where, List<object?> Args) Filters(FeedFilter f, IReadOnlyList<string> enabledSites, string[] fields, bool perTrack)
    {
        var where = new List<string>();
        var args = new List<object?>();
        where.Add("a.source IN (" + string.Join(",", enabledSites.Select(_ => "?")) + ")");      // a site switched off is left out
        args.AddRange(enabledSites);
        if (!f.ShowHidden)
        {
            where.Add("p.hidden=0");
            if (perTrack) where.Add($"NOT EXISTS(SELECT 1 FROM hidden_tracks h WHERE h.post_id=p.id AND h.track={TrackKey})");
        }
        // The same release can reach the feed through two tracked pages: show it once, the copy with the most detail.
        var same = "SELECT p2.id FROM posts p2 JOIN accounts a2 ON a2.id=p2.account_id WHERE a2.source=a.source AND (p2.url=p.url OR p2.embed=p.embed)";
        if (f.ArtistId != null)
        {
            same += " AND a2.artist_id=?";
            args.Add(f.ArtistId);
        }
        where.Add($"p.id=({same} ORDER BY (COALESCE(p2.label,'')='') ASC, p2.detailed DESC, p2.id ASC LIMIT 1)");
        if (!string.IsNullOrEmpty(f.Source)) { where.Add("a.source=?"); args.Add(f.Source); }
        if (f.ArtistId != null) { where.Add("a.artist_id=?"); args.Add(f.ArtistId); }
        if (f.FocusIds != null)
        {
            where.Add(f.FocusIds.Count > 0 ? "a.artist_id IN (" + string.Join(",", f.FocusIds.Select(_ => "?")) + ")" : "0");
            args.AddRange(f.FocusIds.Cast<object?>());
        }
        if (!string.IsNullOrEmpty(f.Year)) { where.Add($"substr({When},1,4)=?"); args.Add(f.Year); }
        if (f.Label == "-") where.Add("COALESCE(p.label,'')=''");
        else if (!string.IsNullOrEmpty(f.Label)) { where.Add("p.label=?"); args.Add(f.Label); }
        if (f.Since != null || f.Until != null)
        {
            var when = f.DateByReleased ? When : $"COALESCE({Posted}, {When})";
            if (f.Since != null) { where.Add($"substr({when},1,10)>=?"); args.Add(f.Since); }
            if (f.Until != null) { where.Add($"substr({when},1,10)<=?"); args.Add(f.Until); }
        }
        if (f.Kind is "album" or "track") { where.Add("p.kind=?"); args.Add(f.Kind); }
        if (f.PreviewOnly)
        {
            // A Bandcamp track with no length has no stream (the unreleased tracks of a pre-order).
            where.Add(perTrack
                ? "NOT (a.source='bandcamp' AND t.id IS NOT NULL AND t.duration IS NULL)"
                : "NOT (a.source='bandcamp' AND EXISTS(SELECT 1 FROM tracks WHERE post_id=p.id)" +
                  " AND NOT EXISTS(SELECT 1 FROM tracks WHERE post_id=p.id AND duration IS NOT NULL))");
        }
        if (f.Length != null && Lengths.TryGetValue(f.Length, out var len))
        {
            string Test(string d) => $"{d}>=?" + (len.High != null ? $" AND {d}<?" : "");
            where.Add(perTrack ? "(" + Test("t.duration") + ")" : "EXISTS(SELECT 1 FROM tracks WHERE post_id=p.id AND " + Test("duration") + ")");
            args.Add(len.Low);
            if (len.High != null) args.Add(len.High);
        }
        foreach (var word in (f.Query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            where.Add("(" + string.Join(" OR ", fields.Select(x => $"{x} LIKE ? ESCAPE '\\'")) + ")");
            foreach (var _ in fields) args.Add(Like(word));
        }
        return ("WHERE " + string.Join(" AND ", where) + " ", args);
    }

    /// <summary>One row per post (release or upload), newest first.</summary>
    public List<FeedPost> Feed(FeedFilter f, IReadOnlyList<string> enabledSites, int limit = 60, int offset = 0)
    {
        var (where, args) = Filters(f, enabledSites, new[]
        {
            "p.title", "COALESCE(p.author,'')", "ar.name", "COALESCE(p.label,'')", "COALESCE(p.release,'')",
            "(SELECT COALESCE(GROUP_CONCAT(title, ' '), '') FROM tracks WHERE post_id=p.id)",
        }, false);
        var sql = "SELECT p.id, p.title, p.url, p.image, p.kind, p.author, p.published_at, p.first_seen," +
                  " p.label, p.release, p.embed, p.hidden, a.source, a.avatar, a.display_name," +
                  " EXISTS(SELECT 1 FROM played WHERE post_id=p.id) AS played," +
                  " ar.id AS artist_id, ar.name AS artist," +
                  " (SELECT COUNT(*) FROM tracks WHERE post_id=p.id) AS track_count," +
                  " (SELECT SUM(duration) FROM tracks WHERE post_id=p.id) AS duration " +
                  Joins + where + $"ORDER BY COALESCE({Posted}, {When}) DESC, p.id DESC LIMIT ? OFFSET ?";
        args.Add(limit);
        args.Add(offset);
        return Query(sql, args.ToArray()).Select(FeedPost.From).ToList();
    }

    /// <summary>One row per track, for the list view and its export.</summary>
    public List<TrackRow> Tracks(FeedFilter f, IReadOnlyList<string> enabledSites, string sort = "date", bool ascending = false,
        int? limit = null, int offset = 0)
    {
        var (where, args) = Filters(f, enabledSites, new[] { TArtist, TLabel, TRelease, TTrack, "p.title", "ar.name" }, true);
        var way = ascending ? "ASC" : "DESC";
        var newest = $"{When} DESC, p.id DESC, t.position ASC";
        string order;
        if (TrackSorts.TryGetValue(sort, out var expr)) order = $"({expr}='') ASC, {expr} COLLATE NOCASE {way}, {newest}";
        else if (sort == "posted") order = $"({Posted} IS NULL) ASC, {Posted} {way}, {newest}";
        else if (sort == "duration") order = $"(t.duration IS NULL) ASC, t.duration {way}, {newest}";
        else order = $"{When} {way}, p.id {way}, t.position ASC";
        var sql =
            $"SELECT p.id AS post_id, t.id AS track_id, {TArtist} AS artist, {TLabel} AS label," +
            $" {TRelease} AS release, {TTrack} AS track, t.duration, t.position," +
            $" t.ext_id AS track_ext_id, {When} AS released, p.published_at, {Posted} AS posted," +
            " COALESCE(t.url, p.url) AS url, p.url AS post_url, p.embed, p.image, p.kind, p.hidden AS release_hidden," +
            $" EXISTS(SELECT 1 FROM hidden_tracks h WHERE h.post_id=p.id AND h.track={TrackKey}) AS track_hidden," +
            $" (p.hidden OR EXISTS(SELECT 1 FROM hidden_tracks h WHERE h.post_id=p.id AND h.track={TrackKey})) AS hidden," +
            " COALESCE(t.ext_id, t.title, '') AS track_key," +
            " EXISTS(SELECT 1 FROM played pl WHERE pl.post_id=p.id AND pl.track IN ('', COALESCE(t.ext_id, t.title, ''))) AS played," +
            " p.title AS post_title, a.source, ar.id AS artist_id, ar.name AS tracked_artist " +
            Joins + "LEFT JOIN tracks t ON t.post_id=p.id " + where + "ORDER BY " + order;
        if (limit != null)
        {
            sql += " LIMIT ? OFFSET ?";
            args.Add(limit);
            args.Add(offset);
        }
        return Query(sql, args.ToArray()).Select(TrackRow.From).ToList();
    }

    /// <summary>The years and labels present, for the filter menus, and how many items are hidden.</summary>
    public Facets GetFacets()
    {
        var years = Query($"SELECT DISTINCT substr({When},1,4) AS y FROM posts p ORDER BY y DESC")
            .Select(r => r["y"] as string).Where(y => !string.IsNullOrEmpty(y)).Select(y => y!).ToList();
        var labels = Query("SELECT DISTINCT label FROM posts WHERE COALESCE(label,'')<>'' ORDER BY label COLLATE NOCASE")
            .Select(r => (string)r["label"]!).ToList();
        var hidden = (long)Query("SELECT COUNT(DISTINCT COALESCE(embed, url)) AS n FROM posts WHERE hidden=1")[0]["n"]!
                     + (long)Query("SELECT COUNT(DISTINCT COALESCE(p.embed, p.url) || '|' || h.track) AS n FROM hidden_tracks h " +
                                   "JOIN posts p ON p.id=h.post_id WHERE p.hidden=0")[0]["n"]!;
        return new Facets(years, labels, (int)hidden);
    }

    /// <summary>Hide a release from the feed (or put it back). Copies that arrived through another tracked page follow.</summary>
    public bool SetHidden(long postId, bool hidden)
    {
        var r = Query("SELECT p.url, p.embed, a.source FROM posts p JOIN accounts a ON a.id=p.account_id WHERE p.id=?", postId).FirstOrDefault();
        if (r == null) return false;
        Execute("UPDATE posts SET hidden=? WHERE id IN (SELECT p.id FROM posts p JOIN accounts a ON a.id=p.account_id " +
                "WHERE a.source=? AND (p.url=? OR (p.embed IS NOT NULL AND p.embed=?)))",
            hidden ? 1 : 0, r["source"], r["url"], r["embed"]);
        return true;
    }

    /// <summary>Hide one track of a release from the list (or put it back). A release of one track is hidden whole.</summary>
    public bool SetTrackHidden(long postId, string? track, bool hidden)
    {
        var r = Query("SELECT p.url, p.embed, a.source, (SELECT COUNT(*) FROM tracks WHERE post_id=p.id) AS n " +
                      "FROM posts p JOIN accounts a ON a.id=p.account_id WHERE p.id=?", postId).FirstOrDefault();
        if (r == null) return false;
        if ((long)r["n"]! <= 1 || string.IsNullOrEmpty(track)) return SetHidden(postId, hidden);
        var copies = Query("SELECT p.id FROM posts p JOIN accounts a ON a.id=p.account_id WHERE a.source=? " +
                           "AND (p.url=? OR (p.embed IS NOT NULL AND p.embed=?))", r["source"], r["url"], r["embed"])
            .Select(c => (long)c["id"]!).ToList();
        if (copies.Count == 0) copies.Add(postId);
        foreach (var pid in copies)
        {
            if (hidden) Execute("INSERT OR IGNORE INTO hidden_tracks(post_id, track, at) VALUES(?,?,?)", pid, track, Now());
            else Execute("DELETE FROM hidden_tracks WHERE post_id=? AND track=?", pid, track);
        }
        return true;
    }

    public bool SetPlayed(long postId, string? track, bool played = true)
    {
        if (Query("SELECT 1 FROM posts WHERE id=?", postId).Count == 0) return false;
        if (played) Execute("INSERT OR IGNORE INTO played(post_id, track, at) VALUES(?,?,?)", postId, track ?? "", Now());
        else if (!string.IsNullOrEmpty(track)) Execute("DELETE FROM played WHERE post_id=? AND track=?", postId, track);
        else Execute("DELETE FROM played WHERE post_id=?", postId);
        return true;
    }
}
