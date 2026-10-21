using System.Text.Json;
using ArtistFeed.Core;
using Xunit;

namespace ArtistFeed.Tests;

/// <summary>Names tidied exactly as the web app tidies them (cases written out by the web app itself),
/// so Focus and YouTube ticks saved there match here.</summary>
public class ParityTests
{
    static List<List<JsonElement>> Cases(string file) =>
        JsonSerializer.Deserialize<List<List<JsonElement>>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", file)))!;

    [Fact]
    public void NormMatchesTheWebApp()
    {
        var wrong = new List<string>();
        foreach (var c in Cases("norm_cases.json"))
        {
            var input = c[0].GetString()!;
            var want = c[1].GetString()!;
            var got = Names.Norm(input);
            if (got != want) wrong.Add($"{input} -> {got} (web app: {want})");
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void SplitNamesMatchesTheWebApp()
    {
        foreach (var c in Cases("split_cases.json"))
            Assert.Equal(c[1].EnumerateArray().Select(x => x.GetString()!), Names.SplitNames(c[0].GetString()));
    }
}

/// <summary>A database written by the web app gives the same feed and list here, for every filter and sort.</summary>
public class DatabaseParityTests
{
    [Fact]
    public void FeedAndListMatchTheWebApp()
    {
        var folder = Path.Combine(Path.GetTempPath(), "af-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "feed.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Data", "webapp.db"), path);
        var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "webapp_expected.json"))).RootElement;
        using (var db = new Db(path))
        {
            var on = expected.GetProperty("on").EnumerateArray().Select(x => x.GetString()!).ToList();
            var facets = db.GetFacets();
            var ef = expected.GetProperty("facets");
            Assert.Equal(ef.GetProperty("years").EnumerateArray().Select(x => x.GetString()), facets.Years);
            Assert.Equal(ef.GetProperty("labels").EnumerateArray().Select(x => x.GetString()), facets.Labels);
            Assert.Equal(ef.GetProperty("hidden").GetInt32(), facets.Hidden);
            int compared = 0;
            foreach (var c in expected.GetProperty("cases").EnumerateArray())
            {
                var f = c.GetProperty("filter");
                string? S(string k) => f.TryGetProperty(k, out var v) ? v.GetString() : null;
                bool B(string k) => f.TryGetProperty(k, out var v) && v.GetBoolean();
                var filter = new FeedFilter
                {
                    Source = S("source"), ArtistId = f.TryGetProperty("artist_id", out var a) ? a.GetInt64() : null, Query = S("q"),
                    Year = S("year"), Label = S("label"), Kind = S("kind"), Length = S("length"), ShowHidden = B("hidden"),
                    PreviewOnly = B("preview"), Since = S("since"), Until = S("until"), DateByReleased = S("date_by") == "released",
                    FocusIds = f.TryGetProperty("focus_ids", out var fi) ? fi.EnumerateArray().Select(x => x.GetInt64()).ToList() : null,
                };
                var feed = db.Feed(filter, on, 500, 0);
                var want = c.GetProperty("feed").EnumerateArray().Select(x => x.ToString()).ToList();
                var got = feed.Select(p => JsonSerializer.Serialize(new object?[]
                    { p.Id, p.Played ? 1 : 0, p.TrackCount, p.Duration, p.Hidden ? 1 : 0 })).ToList();
                Assert.Equal(Normalise(want), Normalise(got));
                foreach (var t in c.GetProperty("tracks").EnumerateObject())
                {
                    var parts = t.Name.Split(':');
                    var rows = db.Tracks(filter, on, parts[0], parts[1] == "asc");
                    var wantRows = t.Value.EnumerateArray().Select(x => x.ToString()).ToList();
                    var gotRows = rows.Select(r => JsonSerializer.Serialize(new object?[]
                        { r.PostId, r.TrackId, r.Artist, r.Label, r.Release, r.Track, r.Played ? 1 : 0, r.Hidden ? 1 : 0, r.TrackKey })).ToList();
                    Assert.True(Normalise(wantRows).SequenceEqual(Normalise(gotRows)), $"{f} {t.Name}");
                    compared += rows.Count;
                }
            }
            Assert.True(compared > 1000);
        }

        // Importing it: copied for the next start, then put in place of the database in use.
        var mine = Path.Combine(folder, "mine", "feed.db");
        using (var db = new Db(mine)) db.AddArtist("Only here");
        Assert.Throws<SourceException>(() => Engine.PrepareImport(Path.Combine(AppContext.BaseDirectory, "Data", "norm_cases.json"), mine));
        Engine.PrepareImport(path, mine);
        Assert.True(Engine.FinishImport(mine));
        Assert.False(Engine.FinishImport(mine));
        using (var db = new Db(mine))
        {
            Assert.Equal(12, db.ListArtists().Count);
            Assert.DoesNotContain("Only here", db.ListArtists().Select(a => a.Name));
        }
        Assert.True(File.Exists(mine + ".before-import"));
        Directory.Delete(folder, true);
    }

    /// <summary>The same JSON text either way: numbers as numbers, text with its accents.</summary>
    static List<string> Normalise(List<string> rows) => rows.Select(r =>
        JsonSerializer.Serialize(JsonDocument.Parse(r).RootElement.EnumerateArray().Select(e => e.ValueKind switch
        {
            JsonValueKind.Number => (object?)e.GetDouble(),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Null => null,
            _ => e.ToString(),
        }).ToList(), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })).ToList();
}
