using System.Text.Json.Serialization;

namespace DcsVoiceCom.Engine;

/// <summary>What speech recognition returns for push-to-talk or typed text: best phrase (null when nothing came close), score 0-100, slot words, raw transcript.</summary>
public sealed record RecognitionResult
{
    [JsonPropertyName("phrase")] public string? Phrase { get; init; }
    [JsonPropertyName("score")] public double Score { get; init; }
    [JsonPropertyName("slots")] public string Slots { get; init; } = "";
    [JsonPropertyName("raw")] public string Raw { get; init; } = "";
    [JsonPropertyName("ms")] public double Ms { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }

    /// <summary>Why the best phrase must not run whatever its score: a word of it was not heard, or another command came too close.</summary>
    [JsonPropertyName("reject")] public string? Reject { get; init; }

    /// <summary>Runner-up matches, best first; tried in order when the best one's spoken values don't parse.</summary>
    [JsonPropertyName("candidates")] public IReadOnlyList<RecognitionCandidate> Candidates { get; init; } = [];
}

public sealed record RecognitionCandidate
{
    [JsonPropertyName("phrase")] public string Phrase { get; init; } = "";
    [JsonPropertyName("score")] public double Score { get; init; }
    [JsonPropertyName("slots")] public string Slots { get; init; } = "";
}

public enum OutcomeStatus
{
    /// <summary>All steps ran.</summary>
    Completed,
    /// <summary>Nothing matched well enough, or a spoken value was invalid. Nothing ran.</summary>
    NoMatch,
    /// <summary>Critical command below the critical threshold, or a wait/step limit failed mid-sequence.</summary>
    Refused,
    /// <summary>A new push-to-talk press cancelled the sequence.</summary>
    Cancelled,
    /// <summary>A step threw (e.g. a bad key name).</summary>
    Failed,
}

public sealed record CommandOutcome(
    OutcomeStatus Status,
    RecognitionResult Recognition,
    string? Command,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> Trace,
    string? Message);

public interface ICuePlayer
{
    void Play(Profiles.Cue cue);
}

public sealed class NullCuePlayer : ICuePlayer
{
    public void Play(Profiles.Cue cue) { }
}
