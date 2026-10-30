using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ArtistFeed.Core;

/// <summary>A candidate for a wanted track: a name, the artists credited, free text around it, and what it came from.</summary>
public sealed class Cand
{
    public string Name { get; init; } = "";
    public List<string?> Artists { get; init; } = new();
    public List<string?> Context { get; init; } = new();
    public object? Data { get; init; }                       // the release, purchase or search result behind it
}

/// <summary>
/// Telling whether a track found somewhere is the one wanted: the web version's matching rules
/// (lookup.py "matching"), with the same thresholds, so both versions agree on every answer.
/// </summary>
public sealed class Matcher(Db db)
{
    static readonly RegexOptions Opt = RegexOptions.CultureInvariant;
    static readonly Regex RemixTag = new(@"[\(\[]\s*([^\)\]]*?)\s*\b(" + Names.RemixWord + @")\b\s*[\)\]]", Opt | RegexOptions.IgnoreCase);
    static readonly Regex VipTag = new(@"[\(\[]([^\)\]]*)\bvip\b([^\)\]]*)[\)\]]", Opt | RegexOptions.IgnoreCase);
    static readonly Regex Splitter = new(@"\s*(?:,|&|\+|/|\bx\b|\bvs\.?\b|\band\b|\bfeat\.?\b|\bft\.?\b|\bfeaturing\b|\bwith\b)\s*",
                                         Opt | RegexOptions.IgnoreCase);

    readonly ConcurrentDictionary<string, List<string>> _credits = new();
    readonly ConcurrentDictionary<string, List<string>> _remixers = new();
    readonly ConcurrentDictionary<string, List<string>?> _vips = new();
    Dictionary<string, List<string>>? _aka;
    readonly object _akaLock = new();

    public static string Norm(string? text) => Names.Norm(text);
    public static string Plain(string? text) => Names.Plain(text);

    /// <summary>No accents, lower case: the web version's fold from before letters such as "ø" were understood.</summary>
    static string FoldOld(string text)
    {
        var d = text.Normalize(System.Text.NormalizationForm.FormKD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var ch in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) is not (System.Globalization.UnicodeCategory.NonSpacingMark
                or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.EnclosingMark)) sb.Append(ch);
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>The text as it was tidied before "ø" and similar letters were understood and other alphabets were
    /// dropped: used only to find answers saved back then.</summary>
    public static string OldSpelling(string? text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in (text ?? "").EnumerateRunes())
        {
            var cat = System.Text.Rune.GetUnicodeCategory(r);
            bool alnum = char.IsLetterOrDigit(r.ToString(), 0) || cat is System.Globalization.UnicodeCategory.LetterNumber or System.Globalization.UnicodeCategory.OtherNumber;
            bool symbol = cat is System.Globalization.UnicodeCategory.MathSymbol or System.Globalization.UnicodeCategory.CurrencySymbol
                or System.Globalization.UnicodeCategory.ModifierSymbol or System.Globalization.UnicodeCategory.OtherSymbol;
            if ((alnum && !Regex.IsMatch(FoldOld(r.ToString()), "[a-z0-9]")) || symbol) sb.Append(' ');
            else sb.Append(r.ToString());
        }
        return sb.ToString();
    }

    /// <summary>Call when the tracked artists change.</summary>
    public void ForgetAliases()
    {
        lock (_akaLock) _aka = null;
        _remixers.Clear();
    }

    static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsDigit);

    /// <summary>Who made this version, as written: "Temperature (River Dub Edit)" -> ["River Dub", "River"]... the
    /// bare name first. Empty for "(Original Mix)", "(VIP)" and the like.</summary>
    public List<string> RemixCredits(string? title) => _credits.GetOrAdd(title ?? "", t =>
    {
        var outp = new List<string>();
        foreach (Match m in RemixTag.Matches(t))
        {
            if (m.Groups[2].Value.Equals("vip", StringComparison.OrdinalIgnoreCase)) continue;
            var words = m.Groups[1].Value.Replace("&", " & ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !IsDigits(w.Trim('.', ',', '\''))).ToList();
            var trimmed = new List<string>(words);
            while (trimmed.Count > 0 && (Names.Generic.Contains(Plain(trimmed[^1])) || Plain(trimmed[^1]).Length == 0)) trimmed.RemoveAt(trimmed.Count - 1);
            while (trimmed.Count > 0 && (Names.Generic.Contains(Plain(trimmed[0])) || Plain(trimmed[0]).Length == 0)) trimmed.RemoveAt(0);
            if (trimmed.Count == 0) continue;
            foreach (var form in new[] { trimmed, words })
            {
                var text = string.Join(' ', form).Trim(' ', '-', '&', ',');
                if (text.Length > 0 && !outp.Contains(text)) outp.Add(text);
            }
        }
        return outp;
    });

    /// <summary>Tidied names of the people in RemixCredits, each form and each person.</summary>
    public List<string> RemixerNames(string? title) => _remixers.GetOrAdd(title ?? "", t =>
        RemixCredits(t).SelectMany(ArtistParts).Distinct().ToList());

    /// <summary>Null unless the title is a VIP version; then the words that name which VIP it is.</summary>
    public List<string>? VipWords(string? title) => _vips.GetOrAdd(title ?? "", t =>
    {
        if (!Regex.IsMatch(Plain(t), @"\bvip\b")) return null;
        var words = new List<string>();
        foreach (Match m in VipTag.Matches(t))
            words.AddRange(Plain(m.Groups[1].Value + " " + m.Groups[2].Value).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !Names.Generic.Contains(w) && !IsDigits(w)));
        return words.Distinct().ToList();
    });

    /// <summary>Is `name` the VIP version asked for in `title`? (True when `title` isn't a VIP.)</summary>
    public bool SameVip(string title, string name)
    {
        var want = VipWords(title);
        if (want == null) return true;
        var told = $" {Plain(name)} ";
        return VipWords(name) != null && want.All(w => told.Contains($" {w} "));
    }

    /// <summary>Asked for a particular version (someone's remix, a VIP) rather than the plain track.</summary>
    public bool Versioned(string title) => RemixerNames(title).Count > 0 || VipWords(title) != null;

    public static string BaseTitle(string? title) => RemixTag.Replace(title ?? "", " ").Trim();

    public static List<string> SplitNames(string? text) => Names.SplitNames(text);
    public static string JoinNames(IEnumerable<string> names) => Names.JoinNames(names);

    /// <summary>{tidied name: every tidied name of that tracked artist}, for artists given more than one.</summary>
    public Dictionary<string, List<string>> AlsoKnownAs()
    {
        lock (_akaLock)
        {
            if (_aka != null) return _aka;
            var groups = new Dictionary<string, List<string>>();
            foreach (var r in db.Query("SELECT name, aliases FROM artists WHERE aliases != ''"))
            {
                var names = new[] { (string)r["name"]! }.Concat(SplitNames((string?)r["aliases"])).Select(Norm).Where(n => n.Length > 0).Distinct().ToList();
                foreach (var n in names)
                    groups[n] = (groups.GetValueOrDefault(n) ?? new()).Concat(names).Distinct().ToList();
            }
            return _aka = groups;
        }
    }

    /// <summary>Tidied names in an artist credit: the whole thing, then each artist in it, then any other
    /// names a tracked artist among them goes by.</summary>
    public List<string> ArtistParts(string? text)
    {
        var pieces = Splitter.Split(text ?? "");
        var parts = new[] { text ?? "" }.Concat(pieces).Select(Norm).Where(p => p.Length > 0).Distinct().ToList();
        var aka = AlsoKnownAs();
        if (aka.Count > 0)
            parts = parts.Concat(parts.SelectMany(p => aka.GetValueOrDefault(p) ?? new List<string>())).Distinct().ToList();
        return parts;
    }

    public static double Ratio(string a, string b) => Names.Ratio(a, b);
    public static double NameSim(string a, string b) => Names.NameSim(a, b);

    /// <summary>The same numbers in both, allowing for "0538"/"538" and "10,000"/"10000".</summary>
    static bool SameNumbers(string a, string b)
    {
        static List<System.Numerics.BigInteger> Groups(string t) =>
            Regex.Matches(t, "[0-9]+").Select(m => System.Numerics.BigInteger.Parse(m.Value)).OrderBy(x => x).ToList();
        static string Run(string t) => string.Concat(Regex.Matches(t, "[0-9]+").Select(m => m.Value)).TrimStart('0');
        return Groups(a).SequenceEqual(Groups(b)) || Run(a) == Run(b);
    }

    /// <summary>How alike two tidied titles are. Numbers must agree exactly.</summary>
    public static double TitleSim(string want, string have)
    {
        var r = Ratio(want, have);
        if (r > 0 && !SameNumbers(want, have)) r = Math.Min(r, 0.7);
        return r;
    }

    /// <summary>The readable part of a Bandcamp address: ".../album/dno021-kercha-open-the-door-lp" -> "dno021 kercha open the door lp".</summary>
    public static string SlugWords(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url.Split('?')[0];
        path = path.TrimEnd('/');
        var i = path.LastIndexOf('/');
        return (i >= 0 ? path[(i + 1)..] : path).Replace("-", " ");
    }

    /// <summary>Places besides the artist credit where a release names its artist: its title and its address.</summary>
    public static List<string?> ReleaseContext(System.Text.Json.Nodes.JsonObject rel) => new()
    {
        rel.S("title"), rel.S("release"), SlugWords(rel.S("url")), SlugWords(rel.S("asked")), SlugWords(rel.S("release_url")),
    };

    static readonly Regex BareVersionWord = new(@"\b(?:remix|rmx|bootleg|boot|edit|re ?edit|flip|refix|rework|re ?rub|mash ?up)\b", Opt);

    /// <summary>A candidate's tidied title without what it repeats about the request: "by &lt;the artist asked
    /// for&gt;" ("Oblivion bootleg (by Astor Piazzolla as performed by ...)"), and, when a version was asked
    /// for, a bare "bootleg"/"remix"/"edit" word written outside brackets.</summary>
    static string Restated(string have, List<string> wanted, bool version)
    {
        var text = $" {have} ";
        foreach (var w in wanted.OrderByDescending(w => w.Length))
            if (w.Length >= 2) text = text.Replace($" by {w} ", " ");
        if (version) text = BareVersionWord.Replace(text, " ");
        return string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    static HashSet<string> VersionWords(string normalised) =>
        BareVersionWord.Matches(normalised).Select(m => m.Value).ToHashSet();

    /// <summary>(title score, artist score) for a candidate.</summary>
    public (double T, double A) Score(string artist, string title, Cand cand)
    {
        var wantTitle = Norm(title);
        var wanted = ArtistParts(artist);
        var name = cand.Name ?? "";
        var dash = name.IndexOf(" - ", StringComparison.Ordinal);       // label pages often title tracks "Artist - Title"
        var prefix = dash >= 0 ? name[..dash] : name;
        var rest = dash >= 0 ? name[(dash + 3)..] : "";
        var t = Math.Max(TitleSim(wantTitle, Norm(name)), rest.Length > 0 ? TitleSim(wantTitle, Norm(rest)) : 0.0);
        var askedVersion = RemixerNames(title).Count > 0;
        foreach (var text in rest.Length > 0 ? new[] { name, rest } : new[] { name })   // the same title, worded around what was asked
        {
            var cleaned = Restated(Norm(text), wanted, askedVersion);
            if (cleaned.Length > 0) t = Math.Max(t, TitleSim(wantTitle, cleaned));
        }
        var have = cand.Artists.SelectMany(n => ArtistParts(n ?? "")).ToList();
        if (rest.Length > 0) have.AddRange(ArtistParts(prefix));
        double a = 0;
        foreach (var w in wanted)
            foreach (var h in have)
                a = Math.Max(a, NameSim(w, h));
        if (a < 0.9 && wanted.Count > 0)
        {
            var around = " " + string.Join(" ", cand.Context.Where(c => !string.IsNullOrEmpty(c)).Select(c => Norm(c))) + " ";
            if (wanted.Any(w => w.Length >= 2 && around.Contains($" {w} "))) a = 0.9;
        }
        if (wanted.Count == 0) a = 0.6;                     // no artist given: the title alone is a weak match
        var makers = RemixerNames(title);
        if (makers.Count > 0)
        {
            var told = " " + string.Join(" ", new[] { name }.Concat(cand.Artists).Concat(cand.Context)
                .Where(x => !string.IsNullOrEmpty(x)).Select(x => Plain(x))) + " ";
            if (makers.Any(m => told.Contains($" {m} "))) a = Math.Max(a, 0.9);
            else t = Math.Min(t, 0.7);
        }
        else if (RemixerNames(name).Count > 0) t = Math.Min(t, 0.8);
        else if (VersionWords(Norm(name)).Except(VersionWords(wantTitle)).Any())
            t = Math.Min(t, 0.8);                           // "Oblivion bootleg (...)": someone's version, not the track
        if (!SameVip(title, name)) t = Math.Min(t, 0.7);
        else if (VipWords(title) == null && VipWords(name) != null) t = Math.Min(t, 0.8);
        return (t, a);
    }

    public static string Judge(double t, double a)
    {
        if (t >= 0.88 && a >= 0.85) return "bandcamp";
        if ((t >= 0.88 && a >= 0.55) || (t >= 0.75 && a >= 0.85)) return "bandcamp_check";
        return "";
    }

    /// <summary>The best acceptable candidate and its verdict ("bandcamp", "bandcamp_check" or "").</summary>
    public (string Verdict, Cand? Cand) BestCandidate(string artist, string title, IEnumerable<Cand> cands)
    {
        (string, Cand?, double) best = ("", null, -1.0);
        foreach (var cand in cands)
        {
            var (t, a) = Score(artist, title, cand);
            var verdict = Judge(t, a);
            var rank = (verdict == "bandcamp" ? 2 : verdict.Length > 0 ? 1 : 0) * 10 + t + a;
            if (verdict.Length > 0 && rank > best.Item3) best = (verdict, cand, rank);
        }
        return (best.Item1, best.Item2);
    }

    /// <summary>Long enough that the title alone says something ("Dragon Language", not "Air" or "02").</summary>
    public static bool Distinctive(string title)
    {
        var n = Norm(title);
        return n.Length >= 8 || n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
    }
}
