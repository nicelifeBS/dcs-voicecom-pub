using System.Text.Json;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.Tests;

public class PhraseMatcherTests
{
    private const double Threshold = 85; // the engine's default

    private static PhraseMatcher Make(params string[] phrases)
    {
        var m = new PhraseMatcher();
        m.Load(phrases);
        return m;
    }

    [Fact]
    public void Ratio_is_rapidfuzz_ratio()
    {
        Assert.Equal(100, PhraseMatcher.Ratio("", ""));
        Assert.Equal(0, PhraseMatcher.Ratio("abc", ""));
        Assert.Equal(50, PhraseMatcher.Ratio("gear", "keer"), 9); // LCS "er": 100 * 2 * 2 / 8, as rapidfuzz gives
    }

    [Fact]
    public void Exact_and_fuzzy_match()
    {
        var m = Make("gear up", "gear down", "hook down");
        var best = m.Match("Gear up.")[0];
        Assert.Equal(("gear up", 100.0), (best.Phrase, best.Score));
        Assert.Equal("gear up", m.Match("gear uhp")[0].Phrase);
    }

    [Fact]
    public void Slot_words_are_stripped_and_returned()
    {
        var best = Make("radio one {freq}", "radio one", "gear up").Match("radio one two five one decimal zero")[0];
        Assert.Equal(("radio one {freq}", 100.0, "two five one decimal zero"), (best.Phrase, best.Score, best.Slots));
    }

    [Fact]
    public void Slot_template_needs_slot_words() =>
        Assert.Equal("radio one", Make("radio one {freq}", "radio one").Match("radio one")[0].Phrase);

    [Fact]
    public void More_literal_words_win_ties()
    {
        var matches = Make("tacan {channel:1-126}", "tacan mode {mode:off|on}").Match("tacan mode off");
        Assert.Equal(("tacan mode {mode:off|on}", "off"), (matches[0].Phrase, matches[0].Slots));
        Assert.Equal("tacan {channel:1-126}", matches[1].Phrase);
    }

    [Fact]
    public void Suffix_literals()
    {
        var best = Make("turn {deg} degrees").Match("turn two seven zero degrees")[0];
        Assert.Equal((100.0, "two seven zero"), (best.Score, best.Slots));
    }

    [Fact]
    public void Slot_only_phrase_never_matches() => Assert.Equal(0, Make("{anything}").Match("hello there")[0].Score);

    [Fact]
    public void Empty_transcript() => Assert.Empty(Make("gear up").Match(""));

    [Fact]
    public void Literal_runs_cut_out_slots() =>
        Assert.Equal(["tacan mode", "gear up"], Make("tacan mode {mode:t r|a a}", "gear up", "Gear up").LiteralRuns());

    [Fact]
    public void Sound_key_merges_confusable_consonants()
    {
        Assert.All(new[] { "gear", "keer", "cheer", "care" }, w => Assert.Equal("KR", PhraseMatcher.SoundKey(w)));
        Assert.Equal(PhraseMatcher.SoundKey("flaps"), PhraseMatcher.SoundKey("flips"));
        Assert.Equal(PhraseMatcher.SoundKey("bandit"), PhraseMatcher.SoundKey("banded"));
        Assert.Equal("_P", PhraseMatcher.SoundKey("up"));
        Assert.Equal("251", PhraseMatcher.SoundKey("251"));
    }

    [Fact]
    public void Sound_rescues_a_misheard_command()
    {
        var d = Make("gear up", "gear down").Decide("Keer up.");
        Assert.Equal(("gear up", 100.0, (string?)null), (d.Matches[0].Phrase, d.Matches[0].Score, d.Reject));
    }

    [Fact]
    public void Word_not_heard_is_rejected() =>
        Assert.Contains("not heard", Make("jester radar on", "jester radar off").Decide("jester radar high").Reject);

    [Theory]
    [InlineData("jester target 1", "Jester, target one.")]
    [InlineData("jester target one", "jester target 1")]
    public void Number_heard_as_word_or_digit_counts(string phrase, string said)
    {
        var d = Make(phrase, "jester target 2").Decide(said);
        Assert.Equal((phrase, 100.0, (string?)null), (d.Matches[0].Phrase, d.Matches[0].Score, d.Reject));
    }

    [Theory]
    [InlineData("jester music on")]
    [InlineData("jester music off")]
    public void Near_tie_settled_by_the_words_that_differ(string said)
    {
        var d = Make("jester music on", "jester music off").Decide(said);
        Assert.Equal((said, (string?)null), (d.Matches[0].Phrase, d.Reject));
    }

    [Fact]
    public void Near_tie_with_slots_prefers_the_phrase_whose_own_words_were_heard()
    {
        var m = Make("tacan {channel:1-126}", "tacan mode {mode:off|on}");
        var d = m.Decide("tacan mode off");
        Assert.Equal(("tacan mode {mode:off|on}", "off", (string?)null), (d.Matches[0].Phrase, d.Matches[0].Slots, d.Reject));
        d = m.Decide("tacan seventy three x-ray");
        Assert.Equal(("tacan {channel:1-126}", (string?)null), (d.Matches[0].Phrase, d.Reject));
    }

    [Fact]
    public void Unsettled_near_tie_is_rejected()
    {
        Assert.StartsWith("too close to call", Make("radar on", "radar one").Decide("radar won").Reject);
        Assert.StartsWith("too close to call", Make("chaff left", "chaff lift").Decide("chaff loft").Reject);
    }

    [Fact]
    public void Single_letter_must_be_heard() => Assert.Contains("not heard", Make("mode a", "mode b").Decide("mode c").Reject);

    [Fact]
    public void Phrases_of_one_command_do_not_compete()
    {
        var m = new PhraseMatcher();
        m.Load(["master arm on", "arm master on", "master arm off"], ["Master arm on", "Master arm on", "Master arm off"]);
        var d = m.Decide("master arm on");
        Assert.Equal(("master arm on", (string?)null), (d.Matches[0].Phrase, d.Reject));
    }

    [Fact]
    public void Groups_must_match_phrases() => Assert.Throws<ArgumentException>(() => new PhraseMatcher().Load(["a", "b"], ["A"]));

    /// <summary>
    /// Whistle transcripts from a real VoiceAttack session (2026-10-23, no keyword biasing). At the default threshold
    /// nothing wrong may fire; the sound skeleton rescues some misheard takes ("Care up", "Clips down").
    /// </summary>
    [Fact]
    public void Recorded_session_never_runs_a_wrong_command()
    {
        var m = Make("gear up", "gear down", "lock bandit", "flaps up", "flaps down");
        int right = 0, wrong = 0;
        foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "data", "va_session_2026-10-23.csv")).Skip(1))
        {
            var (heard, said) = (line[..line.LastIndexOf(',')], line[(line.LastIndexOf(',') + 1)..].ToLowerInvariant());
            var d = m.Decide(heard.Trim('"'));
            var top = d.Matches[0];
            if (d.Reject is not null || top.Score < Threshold) continue;
            if (top.Phrase == said) right++;
            else wrong++;
        }
        Assert.Equal(0, wrong);
        Assert.True(right >= 21, $"only {right} right");
    }

    [Theory]
    [InlineData("Say again"), InlineData("Copy that"), InlineData("Roger"), InlineData("Two engaged"), InlineData("Bandit bandit")]
    [InlineData("Clear up there"), InlineData("Get down"), InlineData("Look out"), InlineData("Flaps check"), InlineData("Give it up")]
    [InlineData("Check six"), InlineData("What's up")]
    public void Radio_chatter_does_not_fire(string said)
    {
        var d = Make("gear up", "gear down", "lock bandit", "flaps up", "flaps down").Decide(said);
        Assert.True(d.Reject is not null || d.Matches[0].Score < Threshold);
    }

    /// <summary>
    /// Decisions recorded from the Python sidecar's matcher (the one this class was ported from) on 393 transcripts:
    /// its own test cases, a real VoiceAttack session, WhistleAttack's spike transcripts and perturbed F-14 profile
    /// phrases. The port must rank, score, split slots and reject exactly as it did.
    /// </summary>
    [Fact]
    public void Matches_the_python_matcher_it_was_ported_from()
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "matcher_golden.json")));
        var checkedCount = 0;
        foreach (var c in golden.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString();
            var phrases = c.GetProperty("phrases").EnumerateArray().Select(p => p.GetString()!).ToList();
            var groups = c.GetProperty("groups").ValueKind == JsonValueKind.Null ? null
                : c.GetProperty("groups").EnumerateArray().Select(p => p.GetString()!).ToList();
            var m = new PhraseMatcher();
            m.Load(phrases, groups);
            Assert.Equal(c.GetProperty("literalRuns").EnumerateArray().Select(r => r.GetString()), m.LiteralRuns());
            foreach (var t in c.GetProperty("transcripts").EnumerateArray())
            {
                var text = t.GetProperty("text").GetString()!;
                var d = m.Decide(text);
                var where = $"{name}: \"{text}\"";
                Assert.True(t.GetProperty("reject").GetString() == d.Reject, $"{where}: reject {d.Reject} != {t.GetProperty("reject")}");
                var expected = t.GetProperty("matches").EnumerateArray().ToList();
                Assert.True(expected.Count == d.Matches.Count, $"{where}: {d.Matches.Count} matches, expected {expected.Count}");
                for (var i = 0; i < expected.Count; i++)
                {
                    var (e, a) = (expected[i], d.Matches[i]);
                    Assert.True(e.GetProperty("phrase").GetString() == a.Phrase && e.GetProperty("slots").GetString() == a.Slots
                                && Math.Abs(e.GetProperty("score").GetDouble() - a.Score) < 1e-9,
                        $"{where} #{i}: {a.Phrase} {a.Score} [{a.Slots}], expected {e}");
                }
                checkedCount++;
            }
        }
        Assert.Equal(393, checkedCount);
    }
}
