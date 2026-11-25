namespace DcsVoiceCom.Speech;

public enum MicGrade
{
    /// <summary>Understood, and the level is fine.</summary>
    Good,
    /// <summary>Understood, but the level is off (too quiet, clipping, cut short).</summary>
    Fair,
    /// <summary>Not understood.</summary>
    Poor,
}

/// <summary>One Test Coms try: what was said, what the speech engine heard, the score, the level and the clip for playback.</summary>
public sealed record MicCheckResult
{
    public required string Expected { get; init; }
    public required string Heard { get; init; }
    /// <summary>How well <see cref="Heard"/> matches <see cref="Expected"/>, 0-100, by the command matcher.</summary>
    public double Score { get; init; }
    /// <summary>The score a command needs.</summary>
    public int Threshold { get; init; }
    public MicGrade Grade { get; init; }
    public double PeakDb { get; init; }
    public double RmsDb { get; init; }
    public double Seconds { get; init; }
    /// <summary>What to fix, in plain words; empty when all is well.</summary>
    public IReadOnlyList<string> Hints { get; init; } = [];
    /// <summary>The 16 kHz mono clip.</summary>
    public float[] Audio { get; init; } = [];
}

/// <summary>Grades a Test Coms clip: the matcher decides if the phrase was understood, the level says if the mic sounds right.</summary>
public static class MicCheck
{
    public const double ClippingDb = -1;
    public const double QuietDb = -40;
    public const double MinSeconds = 0.6;

    /// <summary>The phrase words, as keywords for the speech engine (as a profile's phrases are in normal use).</summary>
    public static IReadOnlyList<string> Keywords(string expected)
    {
        var matcher = new PhraseMatcher();
        matcher.Load([expected]);
        return matcher.LiteralRuns();
    }

    public static MicCheckResult Grade(string expected, string heard, float[] samples, int threshold)
    {
        var matcher = new PhraseMatcher();
        matcher.Load([expected]);
        var decision = matcher.Decide(heard);
        var best = decision.Matches.Count > 0 ? decision.Matches[0] : null;
        var score = best is null ? 0 : Math.Round(best.Score, 1);
        var understood = best is not null && decision.Reject is null && score >= threshold;

        double peak = 0, sum = 0;
        foreach (var s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
            sum += (double)s * s;
        }
        var peakDb = Db(peak);
        var rmsDb = Db(samples.Length == 0 ? 0 : Math.Sqrt(sum / samples.Length));
        var seconds = (double)samples.Length / WhistleModel.SampleRate;

        var hints = new List<string>();
        if (heard.Trim().Length == 0) hints.Add("Nothing was heard. Check the microphone is the right one and not muted.");
        else if (!understood) hints.Add(decision.Reject is { } reject ? $"Not understood: {reject}." : "Not understood. Speak clearly and a bit slower.");
        if (seconds < MinSeconds) hints.Add("Very short. Hold push-to-talk until you finish speaking.");
        if (peakDb >= ClippingDb) hints.Add("Too loud, the audio clips. Turn the microphone level down in Windows.");
        else if (rmsDb < QuietDb) hints.Add("Very quiet. Move the microphone closer or turn its level up in Windows.");

        var levelOk = hints.Count == 0; // when understood, every hint is about the level
        return new MicCheckResult
        {
            Expected = expected,
            Heard = heard,
            Score = score,
            Threshold = threshold,
            Grade = !understood ? MicGrade.Poor : levelOk ? MicGrade.Good : MicGrade.Fair,
            PeakDb = Math.Round(peakDb, 1),
            RmsDb = Math.Round(rmsDb, 1),
            Seconds = Math.Round(seconds, 2),
            Hints = hints,
            Audio = samples,
        };
    }

    private static double Db(double value) => value <= 0 ? -120 : 20 * Math.Log10(value);
}
