using DcsVoiceCom.Speech;

namespace DcsVoiceCom.Tests;

public class MicCheckTests
{
    private const string Phrase = "Jester set radar range to 200";

    /// <summary>A sine tone at <paramref name="amplitude"/> (1 = full scale).</summary>
    private static float[] Tone(double seconds, double amplitude) =>
        Enumerable.Range(0, (int)(seconds * WhistleModel.SampleRate))
            .Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 300 * i / WhistleModel.SampleRate))).ToArray();

    [Theory]
    [InlineData("Jester, set radar range to 200.")]
    [InlineData("jester set radar range to two hundred")]
    public void Understood_phrase_at_a_good_level_is_good(string heard)
    {
        var result = MicCheck.Grade(Phrase, heard, Tone(2, 0.3), 85);

        Assert.Equal(MicGrade.Good, result.Grade);
        Assert.True(result.Score >= 85);
        Assert.Empty(result.Hints);
        Assert.Equal(2, result.Seconds);
    }

    [Fact]
    public void Wrong_words_are_poor()
    {
        var result = MicCheck.Grade(Phrase, "lester said raider", Tone(2, 0.3), 85);

        Assert.Equal(MicGrade.Poor, result.Grade);
        Assert.Contains(result.Hints, h => h.StartsWith("Not understood", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_heard_says_so()
    {
        var result = MicCheck.Grade(Phrase, "", new float[WhistleModel.SampleRate], 85);

        Assert.Equal(MicGrade.Poor, result.Grade);
        Assert.Equal(0, result.Score);
        Assert.Contains(result.Hints, h => h.StartsWith("Nothing was heard", StringComparison.Ordinal));
        Assert.Contains(result.Hints, h => h.StartsWith("Very quiet", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1.0, 2.0, "Too loud")]
    [InlineData(0.005, 2.0, "Very quiet")]
    [InlineData(0.3, 0.3, "Very short")]
    public void Understood_but_level_off_is_fair(double amplitude, double seconds, string hint)
    {
        var result = MicCheck.Grade(Phrase, "jester set radar range to 200", Tone(seconds, amplitude), 85);

        Assert.Equal(MicGrade.Fair, result.Grade);
        Assert.Contains(result.Hints, h => h.StartsWith(hint, StringComparison.Ordinal));
    }

    [Fact]
    public void Keywords_are_the_phrase_words()
    {
        Assert.Contains(MicCheck.Keywords(Phrase), k => k.Contains("radar range", StringComparison.Ordinal));
    }
}

public class MicTestSessionTests
{
    private sealed class Mic(float[] clip) : DcsVoiceCom.Speech.IMicrophone
    {
        public string DeviceName => "Desk mic";
        public int Starts { get; private set; }
        public bool Disposed { get; private set; }
        public void Start() => Starts++;
        public float[] Stop() => clip;
        public void Dispose() => Disposed = true;
    }

    private sealed class Recognizer(string text) : ISpeechToText
    {
        public IReadOnlyList<string> Keywords { get; private set; } = [];

        public string Transcribe(float[] samples, string language, IReadOnlyList<string> keywords)
        {
            Keywords = keywords;
            return text;
        }
    }

    [Fact]
    public async Task Press_and_release_grade_the_clip_with_the_phrase_words_as_keywords()
    {
        var clip = Enumerable.Range(0, 32000).Select(i => (float)(0.3 * Math.Sin(i / 10.0))).ToArray();
        var mic = new Mic(clip);
        var recognizer = new Recognizer("jester set radar range to 200");
        var speech = new SpeechService(recognizer, mic);
        var closed = false;
        var test = new DcsVoiceCom.Runtime.MicTestSession(speech, mic, ownsMicrophone: true, "en", 85, _ => closed = true)
        {
            Phrase = "Jester set radar range to 200",
        };
        var done = new TaskCompletionSource<MicCheckResult>();
        var listening = new List<bool>();
        test.ListeningChanged += listening.Add;
        test.Completed += done.SetResult;

        test.Release(); // not pressed: ignored
        test.Press();
        test.Release();
        var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([true, false], listening);
        Assert.Equal(MicGrade.Good, result.Grade);
        Assert.Same(clip, result.Audio);
        Assert.Contains(recognizer.Keywords, k => k.Contains("radar range", StringComparison.Ordinal));

        test.Dispose();
        Assert.True(mic.Disposed);
        Assert.True(closed);
        test.Press();
        Assert.Equal(1, mic.Starts);
    }

    [Fact]
    public void A_tap_reports_too_short()
    {
        var mic = new Mic(new float[800]);
        var test = new DcsVoiceCom.Runtime.MicTestSession(new SpeechService(new Recognizer(""), mic), mic, false, "en", 85, _ => { });
        string? failed = null;
        test.Failed += m => failed = m;

        test.Press();
        test.Release();

        Assert.StartsWith("Too short", failed);
        test.Dispose();
        Assert.False(mic.Disposed); // the runtime's own microphone stays open
    }
}
