using System.Text;
using System.Text.RegularExpressions;

namespace DcsVoiceCom.Speech;

/// <summary>
/// Fuzzy matching of a transcript against the active profile's phrases.
///
/// Phrases arrive already normalized and may contain typed slots such as <c>radio one {freq}</c> or
/// <c>tacan {channel:1-126} {band:X|Y}</c>. Slot words are stripped before fuzzy matching: the literal words before
/// the first slot and after the last slot are compared, and whatever lies between them is returned as the slot words
/// for the engine to parse into values.
///
/// A phrase scores the better of two similarities: spelling, and a sound skeleton in which consonants a recognizer
/// mixes up share a letter and vowels drop out, so "cheer dell" lands near "gear down". The engine applies the score
/// threshold; the matcher itself turns down two cases a score alone gets wrong (<see cref="MatchDecision.Reject"/>):
/// <list type="bullet">
/// <item>a word of the phrase was not heard at all: "jester radar high" scores high against "jester radar on", but
/// "on" is missing;</item>
/// <item>another command scored within the margin and the words that tell the two apart don't settle it: "gear up"
/// and "gear down" are one short word apart, so that word has to be heard.</item>
/// </list>
/// Phrases of one command (groups) never compete with each other.
/// </summary>
public sealed partial class PhraseMatcher
{
    public const double DefaultMargin = 10.0;

    // A phrase word counts as heard if some transcript word is this close, by spelling or by sound.
    internal const double WordHeard = 60.0;
    // Near-ties: the winner's own words must be heard at least this well, and this much better than the rival's own
    // words. A phrase with no words of its own sits at NoOwnWords, so it can never win a tie, but its rival's extra
    // words still have to be heard to beat it.
    internal const double OwnWordsMin = 70.0;
    internal const double OwnWordsLead = 20.0;
    internal const double NoOwnWords = 60.0;

    private List<MatchTemplate> _templates = [];

    public PhraseMatcher(double margin = DefaultMargin) => Margin = margin;

    public double Margin { get; }

    public int Count => _templates.Count;

    /// <summary>Loads the phrases; <paramref name="groups"/> names each phrase's command, and phrases of one command never compete.</summary>
    public int Load(IReadOnlyList<string> phrases, IReadOnlyList<string>? groups = null)
    {
        if (groups is not null && groups.Count != phrases.Count)
            throw new ArgumentException($"{groups.Count} groups for {phrases.Count} phrases", nameof(groups));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var templates = new List<MatchTemplate>();
        for (var i = 0; i < phrases.Count; i++)
        {
            var phrase = phrases[i];
            if (string.IsNullOrWhiteSpace(phrase) || !seen.Add(phrase)) continue;
            templates.Add(MatchTemplate.Parse(phrase, groups?[i]));
        }
        _templates = templates;
        return templates.Count;
    }

    /// <summary>
    /// The words of every phrase with slots cut out, as keywords for the recognizer:
    /// <c>tacan mode {mode:t r|a a}</c> gives <c>tacan mode</c>; the slot values are spoken free-form.
    /// </summary>
    public IReadOnlyList<string> LiteralRuns() =>
        _templates.SelectMany(t => SlotPattern().Split(t.Phrase)).Select(Normalize).Where(r => r.Length > 0).Distinct().ToList();

    /// <summary>Best matches first. Ties prefer the phrase with more literal words, then one without slots.</summary>
    public IReadOnlyList<PhraseMatch> Match(string transcript, int limit = 5)
    {
        var tokens = Tokens(transcript);
        if (tokens.Length == 0) return [];
        return _templates.Select(t => Score(t, tokens)).OfType<PhraseMatch>()
            .OrderByDescending(m => m.Score).ThenByDescending(m => m.LiteralCount).ThenByDescending(m => m.Slots.Length == 0)
            .Take(limit).ToList();
    }

    /// <summary>Ranks the phrases, then checks the best one was fully heard and clearly beats other commands.</summary>
    public MatchDecision Decide(string transcript, int limit = 5)
    {
        var ranked = Match(transcript, _templates.Count);
        if (ranked.Count == 0) return new MatchDecision([]);
        var top = ranked[0];
        if (MissingWord(top) is { } missing)
            return new MatchDecision(ranked.Take(limit).ToList(), $"'{missing}' of '{top.Phrase}' not heard");

        // The best phrase of every other command that came within the margin and was fully heard.
        var rivals = new List<PhraseMatch>();
        foreach (var m in ranked.Skip(1))
        {
            if (top.Score - m.Score >= Margin) break;
            if (m.Group != top.Group && rivals.All(r => r.Group != m.Group) && MissingWord(m) is null) rivals.Add(m);
        }
        if (rivals.Count == 0) return new MatchDecision(ranked.Take(limit).ToList());

        var tokens = Tokens(transcript);
        List<PhraseMatch> close = [top, .. rivals];
        var winner = close.FirstOrDefault(c => WinsOnOwnWords(c, close.Where(r => !ReferenceEquals(r, c)), tokens));
        if (winner is null)
            return new MatchDecision(ranked.Take(limit).ToList(), "too close to call: " + string.Join(" / ", close.Select(m => $"'{m.Phrase}'")));
        return new MatchDecision(ranked.Where(m => !ReferenceEquals(m, winner)).Prepend(winner).Take(limit).ToList());
    }

    private static PhraseMatch? Score(MatchTemplate template, string[] tokens)
    {
        if (!template.HasSlots)
            return new PhraseMatch(template.Phrase, Similarity(tokens, template.LiteralText, template.LiteralKey), "", template.LiteralCount, template.Group, tokens, template);

        int k = template.Prefix.Length, m = template.Suffix.Length;
        var literal = template.Literals;
        PhraseMatch? best = null;
        // The recognizer may merge or split a word, so allow one token either way at each end.
        foreach (var kp in Around(k))
        {
            foreach (var mp in Around(m))
            {
                if (kp + mp >= tokens.Length) continue; // at least one word must be left for the slots
                var heard = tokens[..kp].Concat(mp > 0 ? tokens[^mp..] : []).ToArray();
                var score = literal.Length > 0 ? Similarity(heard, template.LiteralText, template.LiteralKey) : 0.0; // a slot-only phrase would match anything
                if (best is null || score > best.Score)
                {
                    var slots = string.Join(' ', tokens[kp..(tokens.Length - mp)]);
                    best = new PhraseMatch(template.Phrase, score, slots, template.LiteralCount, template.Group, heard, template);
                }
            }
        }
        return best;
    }

    // n first, then n-1 and n+1 (never below 0).
    private static IEnumerable<int> Around(int n) => n == 0 ? [0, 1] : [n, n - 1, n + 1];

    /// <summary>The first content word of the phrase that nothing in the transcript resembles.</summary>
    private static string? MissingWord(PhraseMatch match)
    {
        foreach (var word in match.Template.Literals)
        {
            // A filler in a phrase may be dropped by the recognizer; a single letter is a name ("mode a").
            if ((!Fillers.Contains(word) || word.Length == 1) && WordHeardScore(word, match.Template.SoundKeys[word], match.Heard) < WordHeard) return word;
        }
        return null;
    }

    /// <summary>How well the words only <paramref name="match"/>'s phrase has (not <paramref name="rival"/>'s) were heard, 0-100.</summary>
    private static double OwnWordsScore(PhraseMatch match, PhraseMatch rival, string[] tokens)
    {
        var theirs = rival.Template.Literals.ToHashSet(StringComparer.Ordinal);
        var own = match.Template.Literals.Where(w => !theirs.Contains(w)).ToList();
        return own.Count == 0 ? NoOwnWords : own.Sum(w => WordHeardScore(w, match.Template.SoundKeys[w], tokens)) / own.Count;
    }

    private static bool WinsOnOwnWords(PhraseMatch candidate, IEnumerable<PhraseMatch> rivals, string[] tokens)
    {
        foreach (var rival in rivals)
        {
            var own = OwnWordsScore(candidate, rival, tokens);
            var theirs = OwnWordsScore(rival, candidate, tokens);
            if (own < OwnWordsMin || own - theirs < OwnWordsLead) return false;
        }
        return true;
    }

    // --- text ------------------------------------------------------------------

    /// <summary>
    /// Lower-case, drop punctuation (keeping '.'/',' between digits), collapse whitespace. The same as
    /// <see cref="Phrases.PhraseText.Normalize"/> for text outside slots.
    /// </summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch is '.' or ',' && i > 0 && i < text.Length - 1 && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1])) sb.Append(ch);
            else sb.Append(' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string[] Tokens(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"\{[^{}]*\}")]
    internal static partial Regex SlotPattern();

    // --- sound skeleton --------------------------------------------------------

    // Spellings of one sound, longest first, replaced before single letters.
    private static readonly (string Spelling, string Letter)[] Spellings =
        [("tsch", "s"), ("sch", "s"), ("ch", "k"), ("ck", "k"), ("sh", "s"), ("ph", "f"), ("th", "t"), ("qu", "k"), ("dg", "j")];

    // Letters a recognizer confuses share one class letter.
    private static readonly Dictionary<char, char> ClassOf = new (char Class, string Letters)[]
        {
            ('K', "gkcq"), ('T', "dt"), ('P', "bp"), ('F', "fv"), ('S', "szxß"), ('N', "mn"), ('J', "j"), ('L', "l"), ('R', "r"),
        }
        .SelectMany(c => c.Letters.Select(l => (l, c.Class))).ToDictionary(p => p.l, p => p.Class);

    /// <summary>Words recognizers insert or split off ("get your down", "give it up"); left out of the skeleton only.</summary>
    public static readonly IReadOnlySet<string> Fillers =
        new HashSet<string>(["a", "an", "the", "it", "i", "you", "your", "her", "to", "uh", "um", "oh"], StringComparer.Ordinal);

    /// <summary>
    /// Consonant skeleton of one word: gear, keer and cheer give KR; down gives TN; up gives _P.
    /// A leading vowel leaves a '_' so "up" and "p" differ; numbers stay as they are.
    /// </summary>
    public static string SoundKey(string word)
    {
        if (word.Length > 0 && word.All(char.IsDigit)) return word;
        foreach (var (spelling, letter) in Spellings) word = word.Replace(spelling, letter, StringComparison.Ordinal);
        var key = new StringBuilder(word.Length == 0 || "aeiouäöü".Contains(word[0]) ? "_" : "");
        foreach (var ch in word)
        {
            char cls;
            if (ClassOf.TryGetValue(ch, out var c)) cls = c;
            else if (char.IsDigit(ch)) cls = ch;
            else continue; // vowels, y, w, h: they colour the sound more than they change it
            if (key.Length == 0 || key[^1] != cls) key.Append(cls);
        }
        return key.Length > 0 ? key.ToString() : "_";
    }

    public static string PhraseKey(IEnumerable<string> words) => string.Join(' ', words.Where(w => !Fillers.Contains(w)).Select(SoundKey));

    /// <summary>0-100: the better of spelling and sound similarity between two word lists.</summary>
    public static double Similarity(IReadOnlyList<string> heard, IReadOnlyList<string> words) =>
        Similarity(heard, string.Join(' ', words), PhraseKey(words));

    /// <summary><see cref="Similarity(IReadOnlyList{string}, IReadOnlyList{string})"/> with the phrase side worked out already.</summary>
    internal static double Similarity(IReadOnlyList<string> heard, string text, string key)
    {
        var spelling = Ratio(string.Join(' ', heard), text);
        var heardKey = PhraseKey(heard);
        var sound = heardKey.Length > 0 && key.Length > 0 ? Ratio(heardKey, key) : 0.0;
        return Math.Max(spelling, sound);
    }

    /// <summary>How well one phrase word shows up among the heard words, 0-100.</summary>
    public static double WordHeardScore(string word, IReadOnlyList<string> heard) => WordHeardScore(word, SoundKey(word), heard);

    internal static double WordHeardScore(string word, string key, IReadOnlyList<string> heard) =>
        heard.Count == 0 ? 0.0 : heard.Max(h => Math.Max(Ratio(word, h), Ratio(key, SoundKey(h))));

    /// <summary>
    /// rapidfuzz's <c>fuzz.ratio</c>: the normalized Indel similarity, 100 * 2 * LCS / (len1 + len2); 100 for two
    /// empty strings.
    /// </summary>
    public static double Ratio(string a, string b)
    {
        var total = a.Length + b.Length;
        if (total == 0) return 100.0;
        // Same arithmetic as rapidfuzz (1 - distance / total), so scores and their rounding agree to the last bit.
        var distance = total - 2 * LongestCommonSubsequence(a, b);
        return (1.0 - (double)distance / total) * 100.0;
    }

    private static int LongestCommonSubsequence(string a, string b)
    {
        if (a.Length < b.Length) (a, b) = (b, a);
        Span<int> row = b.Length < 256 ? stackalloc int[b.Length + 1] : new int[b.Length + 1];
        row.Clear();
        foreach (var ca in a)
        {
            var diagonal = 0;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = ca == b[j - 1] ? diagonal + 1 : Math.Max(row[j], row[j - 1]);
                diagonal = above;
            }
        }
        return row[b.Length];
    }
}

/// <summary>A phrase parsed for matching: the literal words before the first slot and after the last one.</summary>
internal sealed record MatchTemplate(string Phrase, string[] Prefix, string[] Suffix, bool HasSlots, int LiteralCount, string Group)
{
    public string[] Literals { get; } = [.. Prefix, .. Suffix];

    // Worked out once here, as every recognition compares against them.
    public string LiteralText { get; } = string.Join(' ', Prefix.Concat(Suffix));
    public string LiteralKey { get; } = PhraseMatcher.PhraseKey(Prefix.Concat(Suffix));
    public IReadOnlyDictionary<string, string> SoundKeys { get; } =
        Prefix.Concat(Suffix).Distinct(StringComparer.Ordinal).ToDictionary(w => w, PhraseMatcher.SoundKey, StringComparer.Ordinal);

    public static MatchTemplate Parse(string phrase, string? group = null)
    {
        var segments = new List<(bool IsSlot, string Text)>();
        var last = 0;
        foreach (Match m in PhraseMatcher.SlotPattern().Matches(phrase))
        {
            segments.AddRange(Words(phrase[last..m.Index]).Select(w => (false, w)));
            segments.Add((true, m.Value));
            last = m.Index + m.Length;
        }
        segments.AddRange(Words(phrase[last..]).Select(w => (false, w)));

        group = string.IsNullOrEmpty(group) ? phrase : group;
        var literals = segments.Where(s => !s.IsSlot).Select(s => s.Text).ToArray();
        var first = segments.FindIndex(s => s.IsSlot);
        if (first < 0) return new MatchTemplate(phrase, literals, [], false, literals.Length, group);
        var lastSlot = segments.FindLastIndex(s => s.IsSlot);
        return new MatchTemplate(phrase, segments.Take(first).Select(s => s.Text).ToArray(),
            segments.Skip(lastSlot + 1).Select(s => s.Text).ToArray(), true, literals.Length, group);
    }

    private static string[] Words(string text) => PhraseMatcher.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>One scored phrase: <see cref="Heard"/> holds the transcript words its literal words were compared with.</summary>
public sealed record PhraseMatch(string Phrase, double Score, string Slots, int LiteralCount, string Group, string[] Heard)
{
    internal PhraseMatch(string phrase, double score, string slots, int literalCount, string group, string[] heard, MatchTemplate template)
        : this(phrase, score, slots, literalCount, group, heard) => Template = template;

    internal MatchTemplate Template { get; } = null!;
}

/// <summary>The ranked matches, the pick first, and why the pick must not run despite its score (null when it may).</summary>
public sealed record MatchDecision(IReadOnlyList<PhraseMatch> Matches, string? Reject = null);
