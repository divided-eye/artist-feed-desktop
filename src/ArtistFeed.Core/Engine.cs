using System.Text.Json.Nodes;
namespace ArtistFeed.Core;

/// <summary>Everything the app runs on, wired together: storage, sites, the refresh and Tracked Artists.</summary>
public sealed class Engine : IDisposable
{
    public const string Version = "1";

    public Db Db { get; }
    public Pace Pace { get; }
    public Sites Sites { get; }
    public Refresher Refresher { get; }
    public ArtistTable Table { get; }
    public Bandcamp Bandcamp { get; }
    public SoundCloud SoundCloud { get; }
    public YouTube YouTube { get; }
    public Follows Follows { get; }
    public Suggester Suggester { get; }
    public LinkFinder LinkFinder { get; }
    readonly Func<HttpClient> _makeClient;
    readonly CancellationTokenSource _stop = new();

    public Engine(string dbPath, HttpMessageHandler? handler = null, Pace? pace = null)
    {
        Db = new Db(dbPath);
        Pace = pace ?? new Pace();
        Bandcamp = new Bandcamp(Db, Pace);
        SoundCloud = new SoundCloud(Db);
        YouTube = new YouTube();
        Sites = new Sites(Db, new ISource[] { Bandcamp, SoundCloud, YouTube });
        // A test's handler is shared by every client; the real one gets a fresh handler per client.
        _makeClient = handler != null ? () => new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(25) }
                                      : () => Http.Create();
        Refresher = new Refresher(Db, Sites, Pace, _makeClient);
        Table = new ArtistTable(Db, Sites);
        Follows = new Follows(this);
        Suggester = new Suggester(this);
        LinkFinder = new LinkFinder(this);
        Sites.Upgrade();
    }

    public HttpClient MakeClient() => _makeClient();

    /// <summary>The default place for the database: a folder of its own in the user's application data.</summary>
    public static string DefaultDataFolder()
    {
        var env = Environment.GetEnvironmentVariable("ARTIST_FEED_DATA");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ArtistFeed");
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        return Path.Combine(root, "ArtistFeed");     // Windows: %APPDATA%; Linux: ~/.config
    }

    /// <summary>Copy another Artist Feed database (the web version's data/feed.db) to be used from the next
    /// start. SQLite's own backup is used, so changes still waiting in the web version's journal come too.
    /// Throws SourceException when the file isn't an Artist Feed database.</summary>
    public static void PrepareImport(string sourcePath, string dbPath)
    {
        var pending = dbPath + ".import";
        if (File.Exists(pending)) File.Delete(pending);
        using (var src = new Microsoft.Data.Sqlite.SqliteConnection(
                   new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sourcePath, Pooling = false }.ToString()))
        {
            try
            {
                src.Open();
                using var cmd = src.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('artists','accounts','posts')";
                if (Convert.ToInt32(cmd.ExecuteScalar()) != 3) throw new SourceException("That file isn't an Artist Feed database (feed.db).");
                using var dst = new Microsoft.Data.Sqlite.SqliteConnection(
                    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = pending, Pooling = false }.ToString());
                dst.Open();
                src.BackupDatabase(dst);
            }
            catch (Microsoft.Data.Sqlite.SqliteException e)
            {
                if (File.Exists(pending)) File.Delete(pending);
                throw new SourceException("That file couldn't be read as a database: " + e.Message);
            }
        }
    }

    static string Conn(string path) =>
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    /// <summary>At start, before the database is opened: put an imported copy in place of the current one.
    /// The current one is kept beside it as feed.db.before-import.</summary>
    public static bool FinishImport(string dbPath)
    {
        var pending = dbPath + ".import";
        if (!File.Exists(pending)) return false;
        var keep = dbPath + ".before-import";
        if (File.Exists(dbPath))
        {
            // The current one, journal and all, kept as one file in case the import wasn't wanted.
            if (File.Exists(keep)) File.Delete(keep);
            try
            {
                using var cur = new Microsoft.Data.Sqlite.SqliteConnection(Conn(dbPath));
                using var copy = new Microsoft.Data.Sqlite.SqliteConnection(Conn(keep));
                cur.Open();
                copy.Open();
                cur.BackupDatabase(copy);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                File.Copy(dbPath, keep, overwrite: true);
            }
        }
        foreach (var extra in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            if (File.Exists(extra)) File.Delete(extra);
        File.Move(pending, dbPath);
        return true;
    }

    /// <summary>Start the regular refresh (every hour, with catch-up visits in between).</summary>
    /// <summary>Started with the app: the refresh schedule, and Link Finder's background work (reading purchases'
    /// full track lists, and new purchases for the Downloads button).</summary>
    public void StartSchedule()
    {
        try
        {
            LinkFinder.RekeyOldAnswers();                     // answers saved under older keys, as the web version does at start
            LinkFinder.RekeyKnown();
        }
        catch (Exception) { }
        _ = Task.Run(() => Refresher.RunScheduleAsync(_stop.Token));
        LinkFinder.StartBackground(Refresher.Every);
    }

    public const int RelatedHours = 24;

    /// <summary>A SoundCloud track's "Related tracks", read only when asked (the + beside a row in the list),
    /// kept for a day, and handed out a few at a time.</summary>
    public async Task<(List<JsonObject> Tracks, int Total)> RelatedAsync(string trackId, int offset = 0, int limit = 3, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackId) || !trackId.All(char.IsAsciiDigit))
            throw new SourceException("Related tracks are only available for SoundCloud tracks.");
        var rows = (LinkFinder.CacheGet("sc_related", trackId, RelatedHours / 24.0) as JsonArray)?.OfType<JsonObject>().ToList();
        if (rows == null)
        {
            if (Sites.Get("soundcloud") is not SoundCloud sc) throw new SourceException("SoundCloud isn't available.");
            using var client = MakeClient();
            try { rows = await sc.RelatedAsync(client, trackId, ct: ct); }
            catch (SourceException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { throw new SourceException("Couldn't reach SoundCloud for related tracks."); }
            LinkFinder.CachePut("sc_related", trackId, new JsonArray(rows.Select(r => (JsonNode?)r.DeepClone()).ToArray()));
        }
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 50);
        return (rows.Skip(offset).Take(limit).Select(r => (JsonObject)r.DeepClone()).ToList(), rows.Count);
    }

    /// <summary>Tracked Artists' "Check now": read one artist's pages straight away.</summary>
    public async Task CheckArtistAsync(long artistId)
    {
        var ids = Db.ListAccounts(artistId: artistId).Select(a => a.Id).ToList();
        if (ids.Count == 0) throw new SourceException("That artist has no pages to check.");
        await Refresher.RefreshAsync(ids);
    }

    public FeedFilter WithFocus(FeedFilter f, bool focus) => f with { FocusIds = focus ? Sites.FocusIds() : null };

    /// <summary>Only the artists in one workspace (null or empty: everyone).</summary>
    public FeedFilter WithWorkspace(FeedFilter f, string? workspace) => f with { FocusIds = Sites.WorkspaceIds(workspace) };

    public void Dispose()
    {
        _stop.Cancel();
        LinkFinder.Stop();
        Db.Dispose();
    }
}
