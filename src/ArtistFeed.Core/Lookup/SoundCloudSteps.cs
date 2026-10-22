using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArtistFeed.Core;

/// <summary>A SoundCloud profile someone follows.</summary>
public sealed record Profile(string Username, string Permalink, string Url);

public sealed partial class LinkFinder
{
    /// <summary>Python's urllib.parse.quote: like EscapeDataString, but "/" is left as it is.</summary>
    public static string Quote(string text) => Uri.EscapeDataString(text ?? "").Replace("%2F", "/");

    // ---------- SoundCloud: which profile goes with a name ----------
    // Learned from solid evidence (a track of theirs found there, or a link you set) and reused when only the name is left.

    /// <summary>https://soundcloud.com/&lt;profile&gt; for any SoundCloud profile or track address, else "".</summary>
    public static string ProfileUrl(string? url)
    {
        var m = Regex.Match(url ?? "", @"^https?://(?:www\.|m\.)?soundcloud\.com/([A-Za-z0-9_-]+)");
        if (!m.Success) return "";
        var p = m.Groups[1].Value.ToLowerInvariant();
        return p is "search" or "discover" or "you" ? "" : $"https://soundcloud.com/{p}";
    }

    JsonObject Profiles() => J.Parse(Db.KvGet("soundcloud_profiles")) ?? new JsonObject();

    /// <summary>Remember that these (tidied) names go with this profile. A link you set outweighs the rest.</summary>
    public void LearnProfile(IEnumerable<string> names, string? url, int weight = 1)
    {
        var profile = ProfileUrl(url);
        var list = names.Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
        if (profile.Length == 0 || list.Count == 0) return;
        lock (_indexLock)
        {
            var known = Profiles();
            foreach (var n in list)
            {
                if (known[n] is not JsonObject entry) known[n] = entry = new JsonObject();
                entry[profile] = (entry.N(profile) ?? 0) + weight;
            }
            Db.KvSet("soundcloud_profiles", J.Dump(known));
        }
    }

    /// <summary>The profile learned for the first of these names that has one.</summary>
    public string KnownProfile(IEnumerable<string> names)
    {
        var known = Profiles();
        foreach (var n in names)
            if (known[n] is JsonObject entry && entry.Count > 0)
            {
                string best = ""; double top = double.MinValue;
                foreach (var (k, v) in entry)
                {
                    var w = Html.AsNumber(v) ?? 0;
                    if (w > top) { top = w; best = k; }            // the first of the highest, as Python's max
                }
                return best;
            }
        return "";
    }

    /// <summary>Once: learn from what earlier lookups already found.</summary>
    public void SeedProfiles()
    {
        if (Db.KvGet("soundcloud_profiles_seeded") == "2") return;
        foreach (var row in Db.Query("SELECT key, result FROM lookup_cache"))
        {
            if (J.Parse(row.S("result")) is not JsonObject saved) continue;
            var all = new List<JsonObject> { saved };
            if (saved.O("options") is { } opts) all.AddRange(opts.Select(kv => kv.Value).OfType<JsonObject>());
            foreach (var r in all)
                if (r.S("status") == "soundcloud_track" && r.B("found"))
                    LearnProfile(new[] { Matcher.Norm(r.T("found").Split(" — ")[0]) }, r.S("url"));
        }
        Db.KvSet("soundcloud_profiles_seeded", "2");
    }

    /// <summary>Whose page a row falls back to: whoever made the version, else the artist.</summary>
    public List<string> WantedNames(string artist, string title)
    {
        var forms = M.RemixCredits(title);
        if (forms.Count == 0) forms = new List<string> { artist };
        return forms.SelectMany(M.ArtistParts).Distinct().ToList();
    }

    /// <summary>A saved answer whose SoundCloud profile was only matched by name, corrected to the profile
    /// since learned for that name. No request needed.</summary>
    public JsonObject WithKnownProfile(JsonObject saved, string artist, string title)
    {
        JsonNode? Fix(JsonNode? node)
        {
            if (node is not JsonObject r || r.S("status") != "soundcloud" || !r.T("note").ToLowerInvariant().Contains("name only")) return node?.DeepClone();
            var url = KnownProfile(WantedNames(artist, title));
            if (url.Length == 0 || url == ProfileUrl(r.S("url"))) return r.DeepClone();
            return r.With(("url", url), ("found", url[(url.LastIndexOf('/') + 1)..]), ("note", "Profile learned from an earlier result"));
        }
        var outp = (JsonObject)Fix(saved)!;
        if (saved.O("options") is { } opts)
        {
            var fixedOpts = new JsonObject();
            foreach (var (k, v) in opts) fixedOpts[k] = Fix(v);
            outp["options"] = fixedOpts;
        }
        return outp;
    }

    // ---------- SoundCloud: the profiles your account follows ----------

    const int FollowsHours = 12;

    public string SoundCloudMe() => (Db.KvGet("soundcloud_me") ?? Environment.GetEnvironmentVariable("SOUNDCLOUD_USER") ?? "").Trim();

    /// <summary>Set the SoundCloud account whose followed profiles are preferred: a username or its address.</summary>
    public string SetSoundCloudMe(string text)
    {
        var me = (text ?? "").Trim().TrimEnd('/');
        if (me.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase))
            me = new Uri(me.Contains("://") ? me : "https://" + me).AbsolutePath.Trim('/').Split('/')[0];
        me = me.TrimStart('@').ToLowerInvariant();
        if (me.Length > 0 && !Regex.IsMatch(me, "^[a-z0-9_-]{1,100}$")) throw new ArgumentException("That doesn't look like a SoundCloud username.");
        Db.KvSet("soundcloud_me", me);
        Db.KvSet("soundcloud_follows", "");
        return me;
    }

    JsonObject FollowsSaved() => J.Parse(Db.KvGet("soundcloud_follows")) ?? new JsonObject();

    public JsonObject FollowsStatus()
    {
        var saved = FollowsSaved();
        var me = SoundCloudMe();
        var mine = saved.S("user") == me.ToLowerInvariant();
        return new JsonObject
        {
            ["user"] = me, ["count"] = mine ? saved.Objs("users").Count : 0,
            ["read_at"] = mine ? saved.S("read_at") : null, ["error"] = mine ? saved.T("error") : "",
        };
    }

    /// <summary>Every profile the account set in Settings follows; read at most every 12 hours. On failure the
    /// last list read is kept.</summary>
    public async Task<List<Profile>> FollowedProfilesAsync(bool force = false, CancellationToken ct = default)
    {
        var me = SoundCloudMe().ToLowerInvariant();
        if (me.Length == 0) return new();
        var saved = FollowsSaved();
        var mine = saved.S("user") == me;
        static List<Profile> From(JsonObject o) => o.Objs("users").Select(u => new Profile(u.T("username"), u.T("permalink"), u.T("url")))
            .Where(p => p.Permalink.Length > 0).ToList();
        if (mine && !force && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (saved.N("at") ?? 0) < FollowsHours * 3600) return From(saved);
        try
        {
            var found = await _engine.Follows.SoundCloudAsync(me, ct);
            var fresh = new JsonObject
            {
                ["user"] = me,
                ["users"] = new JsonArray(found.Select(f => (JsonNode?)new JsonObject { ["username"] = f.Name, ["permalink"] = f.Handle, ["url"] = f.Url }).ToArray()),
                ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["error"] = "", ["read_at"] = Db.Now(),
            };
            Db.KvSet("soundcloud_follows", J.Dump(fresh));
            return From(fresh);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            var error = e is FollowsException f ? f.Message : $"Couldn't read who {me} follows ({e.GetType().Name}).";
            var keep = mine ? saved.Copy() : new JsonObject { ["user"] = me, ["users"] = new JsonArray() };
            keep["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            keep["error"] = error;
            Db.KvSet("soundcloud_follows", J.Dump(keep));
            return mine ? From(saved) : new();
        }
    }

    /// <summary>(tier, similarity): how well a SoundCloud user fits the wanted names. names[0] is the whole credit;
    /// matching it (tier 2) beats matching one person in it (tier 1).</summary>
    public static (int Tier, double Sim) IsProfileOf(string? username, string? permalink, IReadOnlyList<string> names)
    {
        var shown = Matcher.Norm(username ?? "");
        var address = Matcher.Plain(permalink ?? "").Replace(" ", "");
        (int, double) best = (0, 0.0);
        for (int n = 0; n < names.Count; n++)
        {
            var squashed = names[n].Replace(" ", "");
            var sim = Math.Max(Matcher.NameSim(names[n], shown), Math.Max(Matcher.Ratio(squashed, address),
                address.Contains("and") ? Matcher.Ratio(squashed, address.Replace("and", "")) : 0.0));
            if (sim >= 0.85)
            {
                var cand = (n == 0 ? 2 : 1, Math.Round(sim, 2, MidpointRounding.ToEven));
                if (cand.CompareTo(best) > 0) best = cand;
            }
        }
        return best;
    }

    /// <summary>The best followed profile for these names, or null.</summary>
    public static Profile? FollowedMatch(IEnumerable<Profile> follows, IReadOnlyList<string> names, ICollection<string>? bad = null)
    {
        (int, double)? best = null;
        Profile? who = null;
        foreach (var u in follows)
        {
            if (bad != null && bad.Contains(u.Permalink)) continue;
            var fit = IsProfileOf(u.Username, u.Permalink, names);
            if (fit.Tier > 0 && (best == null || fit.CompareTo(best.Value) > 0)) { best = fit; who = u; }
        }
        return who;
    }

    /// <summary>The SoundCloud profile to link when the track itself wasn't found (or the track, when it is there).</summary>
    public async Task<JsonObject?> FromSoundCloudAsync(Net net, string artist, List<string> steps, string title = "", IReadOnlyList<string>? credits = null, CancellationToken ct = default)
    {
        credits ??= Array.Empty<string>();
        var forms = credits.Count > 0 ? credits.ToList() : new List<string> { artist };
        var nameSets = forms.Select(M.ArtistParts).Where(p => p.Count > 0).ToList();
        if (nameSets.Count == 0) return null;
        var every = nameSets.SelectMany(n => n).ToList();
        var whose = credits.Count > 0 ? "Remixer's profile" : "";
        foreach (var acc in Db.Query("SELECT a.url, a.display_name, ar.name FROM accounts a JOIN artists ar ON ar.id=a.artist_id WHERE a.source='soundcloud'"))
        {
            var theirs = M.ArtistParts(acc.S("name")).Append(Matcher.Norm(acc.S("display_name") ?? "")).ToHashSet();
            if (theirs.Overlaps(every))
                return new JsonObject
                {
                    ["status"] = "soundcloud", ["site"] = "SoundCloud", ["url"] = acc.S("url"),
                    ["found"] = acc.S("display_name") is { Length: > 0 } d ? d : acc.S("name"), ["note"] = "A page you track",
                };
        }
        var sc = _engine.SoundCloud;
        try
        {
            // 1. the track itself on SoundCloud: its uploader
            if (title.Length > 0)
            {
                var queries = new List<string> { $"{artist} {title}".Trim() };
                if (credits.Count > 0) queries.Add($"{credits[0]} {Matcher.BaseTitle(title)}");
                ((int, double) Fit, double Same, JsonObject User, string Name, JsonObject Tr)? best = null;
                foreach (var text in queries.Distinct())
                {
                    var data = await sc.ApiAsync(net.Client, "/search/tracks", new() { ["q"] = text, ["limit"] = "20" }, ct) as JsonObject;
                    foreach (var tr in data.Objs("collection"))
                    {
                        var user = tr.O("user") ?? new JsonObject();
                        var name = tr.T("title");
                        if (!user.B("permalink_url") || !tr.B("permalink_url")) continue;
                        var same = Math.Max(Matcher.TitleSim(Matcher.Norm(title), Matcher.Norm(name)), Matcher.TitleSim(Matcher.Norm(title), Matcher.Norm(After(name))));
                        if (same < 0.85) continue;
                        if (!M.SameVip(title, name)) continue;
                        if (credits.Count > 0)       // must be this version: its maker named in the title or as the uploader
                        {
                            var told = $" {Matcher.Plain(name)} {Matcher.Plain(user.T("username"))} ";
                            if (!M.RemixerNames(title).Any(m => told.Contains($" {m} "))) continue;
                        }
                        var fit = nameSets.Select(n => IsProfileOf(user.S("username"), user.S("permalink"), n)).Max();
                        if (fit.Tier > 0 && (best == null || (fit, same).CompareTo((best.Value.Fit, best.Value.Same)) > 0))
                            best = (fit, same, user, name, tr);
                    }
                    if (best != null) break;
                }
                if (best != null)                          // the track itself: link straight to it
                {
                    var (_, _, user, name, tr) = best.Value;
                    steps.Add($"SoundCloud: \"{name}\" was uploaded by {user.S("username")}");
                    LearnProfile(new[] { Matcher.Norm(user.T("username")) }
                        .Concat(nameSets.Where(n => IsProfileOf(user.S("username"), user.S("permalink"), n).Tier > 0).Select(n => n[0])), user.S("permalink_url"));
                    var free = tr.B("downloadable") || tr.T("purchase_title").ToLowerInvariant().Contains("free");
                    var when = tr.S("release_date") is { Length: > 0 } rd ? rd : tr.S("display_date") is { Length: > 0 } dd ? dd : tr.T("created_at");
                    var y = when.Length >= 4 ? when[..4] : when;
                    return new JsonObject
                    {
                        ["status"] = "soundcloud_track", ["site"] = "SoundCloud", ["url"] = tr.S("permalink_url"),
                        ["found"] = $"{user.T("username")} — {name}", ["release"] = "",
                        ["year"] = Regex.IsMatch(y, "^(19|20)[0-9]{2}$") ? y : "",
                        ["note"] = (free ? "Free download on " : "On ") + (credits.Count > 0 ? "the remixer's SoundCloud" : "the artist's SoundCloud"),
                    };
                }
                steps.Add("SoundCloud: the track isn't there under a matching profile");
            }
            // 2. a profile already learned for this name, from a track of theirs or a link you set
            var learned = KnownProfile(every);
            if (learned.Length > 0)
            {
                steps.Add($"SoundCloud: {learned.Replace("https://", "")}, learned from an earlier result");
                return new JsonObject
                {
                    ["status"] = "soundcloud", ["site"] = "SoundCloud", ["url"] = learned, ["found"] = learned[(learned.LastIndexOf('/') + 1)..],
                    ["note"] = whose.Length > 0 ? whose + ", learned from an earlier result" : "Profile learned from an earlier result",
                };
            }
            // 3. a profile you follow with the right name; the whole credit before the people in it
            var follows = await FollowedProfilesAsync(ct: ct);
            foreach (var names in nameSets)
            {
                var mine = FollowedMatch(follows, names);
                if (mine != null)
                {
                    steps.Add($"SoundCloud: profile \"{mine.Username}\", one you follow, matched by name");
                    return new JsonObject
                    {
                        ["status"] = "soundcloud", ["site"] = "SoundCloud", ["url"] = mine.Url, ["found"] = mine.Username,
                        ["note"] = whose.Length > 0 ? whose + ", a profile you follow" : "A profile you follow",
                    };
                }
            }
            // 4. a profile with the right name; the whole credit before the people in it
            (((int Tier, double Sim) Fit, double Followers) Key, JsonObject User)? top = null;
            for (int i = 0; i < Math.Min(forms.Count, nameSets.Count); i++)     // as Python's zip(forms, name_sets)
            {
                var names = nameSets[i];
                foreach (var text in new[] { forms[i] }.Concat(names.Skip(1).Take(2)).Distinct())
                {
                    var data = await sc.ApiAsync(net.Client, "/search/users", new() { ["q"] = text, ["limit"] = "20" }, ct) as JsonObject;
                    foreach (var u in data.Objs("collection"))
                    {
                        var fit = IsProfileOf(u.S("username"), u.S("permalink"), names);
                        if (fit.Tier > 0 && u.B("permalink_url"))
                        {
                            var key = (fit, u.N("followers_count") ?? 0);
                            if (top == null || key.CompareTo(top.Value.Key) > 0) top = (key, u);
                        }
                    }
                    if (top != null && top.Value.Key.Fit.Tier == 2) break;   // the whole credit matched: no need to try parts
                }
                if (top != null) break;
            }
            if (top != null)
            {
                var u = top.Value.User;
                steps.Add($"SoundCloud: profile \"{u.S("username")}\", matched by name only");
                return new JsonObject
                {
                    ["status"] = "soundcloud", ["site"] = "SoundCloud", ["url"] = u.S("permalink_url"), ["found"] = u.T("username"),
                    ["note"] = whose.Length > 0 ? whose + ", matched by name only" : "Matched by name only",
                };
            }
            steps.Add("SoundCloud: no profile with that name");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            steps.Add($"SoundCloud: search failed ({PyName(e)})");
        }
        return new JsonObject
        {
            ["status"] = "soundcloud_search", ["site"] = "SoundCloud", ["found"] = "",
            ["url"] = "https://soundcloud.com/search/people?q=" + Quote(forms[0]),
            ["note"] = "No matching profile; this opens a SoundCloud search",
        };
    }

    /// <summary>A short name for what went wrong, for the steps list.</summary>
    static string PyName(Exception e) => e switch
    {
        SourceException => "SourceError",
        HttpRequestException or TaskCanceledException => "HTTPError",
        _ => e.GetType().Name,
    };
}
