using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ArtistFeed.Core;

/// <summary>
/// Tidying names and titles for comparing them, exactly as the web app does (its lookup.norm), so
/// the Focus and YouTube-search ticks it saved under tidied names still match here.
/// </summary>
public static class Names
{
    const string RemixWord = @"remix|rmx|re-?mix|edit|re-?edit|bootleg|boot|flip|refix|re-?fix|rework|re-?work|re-?rub|" +
                             @"remake|re-?lick|mash-?up|redo|re-?twist|re-?fit|cover|treatment|version|mix|dub|vip";

    static readonly Regex Wip = new(
        @"[\(\[]\s*(?:wip|w\.i\.p\.?|work in progress|forthcoming|forth\.?(?=\s))[^\)\]]*[\)\]]|(?<=\S)\s+[-–]?\s*\b(?:wip|w\.i\.p\.?)(?=\s*(?:$|[\(\[]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly Regex AnyTag = new(
        @"[\(\[][^\)\]]*(?:mix|edit|version|remaster|bonus|free|dub|\b(?:" + RemixWord + @")\b)[^\)\]]*[\)\]]",
        RegexOptions.CultureInvariant);

    static readonly HashSet<string> Generic = new("""
        original extended radio club instrumental vocal dub vip album single full short long clean
        dirty explicit main alternate alt demo live final new old digital vinyl bonus free download unreleased
        dubplate special mini intro outro acapella remastered remaster mono stereo official unofficial exclusive
        the a an his her their own self dubstep garage jungle dnb bass uk us deep dark heavy 140 2step ukg
        halftime breaks breakbeat techno house drill grime footwork juke trap amapiano riddim hardcore rave
        ambient acid electro bassline speed slow sped slowed reverb pitched festival tool dj edit refix flip
        bootleg remix rework mix version unofficial steppers stepper roller rollers sound system soundsystem
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static readonly HashSet<string> Format = new("""
        digital download downloads dl wav wave mp3 flac aiff 16bit 24bit bit hq hd master mastered premaster
        preorder pre order out now forthcoming soon premiere preview clip clips snippet teaser promo limited edition
        repress press pressing lp ep 7 10 12 inch only bandcamp soundcloud release released track audio stream
        streaming name your price nyp buy purchase link in bio description
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static readonly Regex Bracket = new(@"[\(\[]([^\)\]]*)[\)\]]", RegexOptions.CultureInvariant);
    static readonly Regex NotAz09 = new("[^a-z0-9]+", RegexOptions.CultureInvariant);
    static readonly Regex NonWord = new(@"[\W_]+", RegexOptions.CultureInvariant);
    static readonly Regex Feat = new(@"\b(feat|ft|featuring)\b.*$", RegexOptions.CultureInvariant);

    // Letters that are not "a letter plus an accent", so stripping accents leaves them alone.
    static readonly Dictionary<char, string> Letters = new()
    {
        ['ø'] = "o", ['Ø'] = "O", ['đ'] = "d", ['Đ'] = "D", ['ł'] = "l", ['Ł'] = "L", ['æ'] = "ae", ['Æ'] = "AE",
        ['œ'] = "oe", ['Œ'] = "OE", ['ð'] = "d", ['Ð'] = "D", ['þ'] = "th", ['Þ'] = "TH", ['ı'] = "i", ['ħ'] = "h",
        // trademark, copyright, sound recording and degree signs
        ['™'] = " ", ['©'] = " ", ['®'] = " ", ['℗'] = " ", ['°'] = " ",
    };

    /// <summary>No accents, lower case (Unicode case folding).</summary>
    public static string Fold(string? text)
    {
        var sb = new StringBuilder((text ?? "").Length);
        foreach (var ch in text ?? "")
            if (Letters.TryGetValue(ch, out var s)) sb.Append(s); else sb.Append(ch);
        var decomposed = sb.ToString().Normalize(NormalizationForm.FormKD);
        var outp = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) continue;
            outp.Append(ch);
        }
        return CaseFold(outp.ToString());
    }

    static string CaseFold(string s)
    {
        var lower = s.ToLowerInvariant();
        if (lower.IndexOfAny(new[] { 'ß', 'ς', 'ẞ' }) < 0) return lower;
        return lower.Replace("ß", "ss").Replace("ẞ", "ss").Replace("ς", "σ");
    }

    static string DropDescribing(string text) => Bracket.Replace(text, m =>
    {
        var words = NotAz09.Replace(m.Groups[1].Value, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.All(w => Generic.Contains(w) || Format.Contains(w)) ? " " : m.Value;
    });

    static bool IsAlnum(Rune r)
    {
        var cat = Rune.GetUnicodeCategory(r);
        return cat is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    }

    static bool IsSymbol(Rune r) => Rune.GetUnicodeCategory(r) is UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol
        or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;

    /// <summary>The letters and digits of a tidied name, of any alphabet, with everything else as a space.
    /// A symbol written as part of a word stays ("∑1", "Myca$h"); punctuation never does.</summary>
    static string Words(string text)
    {
        bool ascii = text.All(c => c < 128);
        if (ascii && !text.Contains('$')) return NonWord.Replace(text, " ").Trim();
        var runes = text.EnumerateRunes().ToArray();
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < runes.Length; i++)
        {
            var ch = runes[i];
            if (IsAlnum(ch)) sb.Append(ch.ToString());
            else if ((ch.Value == '$' || (ch.Value >= 128 && IsSymbol(ch)))
                     && ((i > 0 && IsAlnum(runes[i - 1])) || (i + 1 < runes.Length && IsAlnum(runes[i + 1]))))
                sb.Append(ch.ToString());
            else sb.Append(' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Like Norm, but keeps everything, including version tags.</summary>
    public static string Plain(string? text) => Words(Fold(text));

    /// <summary>How alike two strings are, as Python's difflib.SequenceMatcher(None, a, b).ratio() measures it:
    /// twice the matched characters over the total length. The web version's matching thresholds rely on it.</summary>
    public static double Ratio(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        // difflib's cheap upper bounds first; below 0.5 never matters to the callers.
        var counts = new Dictionary<char, int>();
        foreach (var c in b) counts[c] = counts.GetValueOrDefault(c) + 1;
        int common = 0;
        foreach (var c in a)
            if (counts.TryGetValue(c, out var n) && n > 0) { counts[c] = n - 1; common++; }
        double total = a.Length + b.Length;
        if (2.0 * Math.Min(a.Length, b.Length) / total < 0.5 || 2.0 * common / total < 0.5) return 0;
        return 2.0 * Matched(a, 0, a.Length, b, 0, b.Length) / total;
    }

    /// <summary>Characters matched by difflib's method: the longest common block (earliest in a, then in b),
    /// then the same on either side of it.</summary>
    static int Matched(string a, int alo, int ahi, string b, int blo, int bhi)
    {
        if (alo >= ahi || blo >= bhi) return 0;
        int besti = alo, bestj = blo, bestsize = 0;
        var len = new int[bhi - blo + 1];
        for (int i = alo; i < ahi; i++)
        {
            var next = new int[bhi - blo + 1];
            for (int j = blo; j < bhi; j++)
            {
                if (a[i] != b[j]) continue;
                var k = next[j - blo + 1] = len[j - blo] + 1;
                if (k > bestsize) { besti = i - k + 1; bestj = j - k + 1; bestsize = k; }
            }
            len = next;
        }
        if (bestsize == 0) return 0;
        return bestsize + Matched(a, alo, besti, b, blo, bestj) + Matched(a, besti + bestsize, ahi, b, bestj + bestsize, bhi);
    }

    /// <summary>How alike two tidied names are; one being the other plus extra words counts as close
    /// ("somah" / "somah uk").</summary>
    public static double NameSim(string a, string b)
    {
        var r = Ratio(a, b);
        if (r < 0.9 && Math.Min(a.Length, b.Length) >= 4)
        {
            var ta = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var tb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            if (ta.IsSubsetOf(tb) || tb.IsSubsetOf(ta)) r = 0.9;
        }
        return r;
    }

    /// <summary>For comparing titles and names: no accents, case, punctuation, "feat. X", or version tags
    /// such as "(Original Mix)".</summary>
    public static string Norm(string? text)
    {
        var t = Wip.Replace(Fold(text), " ");
        t = AnyTag.Replace(t, " ");
        var dropped = DropDescribing(t);
        if (NonWord.Replace(dropped, "").Length > 0) t = dropped;
        t = Feat.Replace(t, " ");
        return Words(t);
    }

    /// <summary>The names in a Tracked Artists name cell: the artist's name, then any others they go by,
    /// separated by commas. A name that has a comma of its own goes in double quotes.</summary>
    public static List<string> SplitNames(string? text)
    {
        var outp = new Dictionary<string, string>();
        foreach (var raw in CsvLine(text ?? ""))
        {
            var n = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (n.Length == 0) continue;
            var key = Norm(n);
            if (key.Length == 0) key = n;
            outp.TryAdd(key, n);
        }
        return outp.Values.ToList();
    }

    public static string JoinNames(IEnumerable<string> names) =>
        string.Join(", ", names.Where(n => !string.IsNullOrEmpty(n)).Select(n => n.Contains(',') ? $"\"{n}\"" : n));

    /// <summary>One line split like a CSV record (commas, double quotes, spaces after a comma skipped).</summary>
    public static List<string> CsvLine(string line)
    {
        var fields = new List<string>();
        var cur = new StringBuilder();
        int i = 0;
        bool atStart = true;
        while (i <= line.Length)
        {
            if (i == line.Length) { fields.Add(cur.ToString()); break; }
            char c = line[i];
            if (atStart && c == ' ') { i++; continue; }
            if (atStart && c == '"')
            {
                atStart = false;
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    cur.Append(line[i++]);
                }
                continue;
            }
            atStart = false;
            if (c == ',') { fields.Add(cur.ToString()); cur.Clear(); atStart = true; i++; continue; }
            cur.Append(c);
            i++;
        }
        if (fields.Count == 1 && fields[0].Length == 0 && line.Length == 0) return new List<string>();
        return fields;
    }
}
