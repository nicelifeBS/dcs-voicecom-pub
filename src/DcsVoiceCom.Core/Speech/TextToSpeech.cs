namespace DcsVoiceCom.Speech;

/// <summary>One installed text-to-speech voice. <see cref="Language"/> is a BCP-47 tag such as "en-US".</summary>
public sealed record VoiceInfo(string Id, string Name, string Language, string Gender = "");

/// <summary>How to speak one reply. <see cref="Rate"/> is 0.5-2.0, <see cref="Volume"/> is 0-1.</summary>
public sealed record TtsOptions(string? VoiceId, double Rate = 1.0, double Volume = 1.0, string? Device = null);

/// <summary>Speaks text with an installed voice (on Windows: OneCore voices).</summary>
public interface ITextToSpeech
{
    /// <summary>The installed voices; empty when there are none.</summary>
    IReadOnlyList<VoiceInfo> Voices { get; }

    /// <summary>Queues the text and completes when it has been spoken (or cut off by <see cref="Stop"/>).</summary>
    Task SpeakAsync(string text, TtsOptions options, CancellationToken ct = default);

    /// <summary>Cuts off the current reply and clears the queue.</summary>
    void Stop();
}

/// <summary>No voices, speaks nothing. For tests and systems without voices.</summary>
public sealed class NullTextToSpeech : ITextToSpeech
{
    public IReadOnlyList<VoiceInfo> Voices => [];
    public Task SpeakAsync(string text, TtsOptions options, CancellationToken ct = default) => Task.CompletedTask;
    public void Stop() { }
}

public static class VoiceChooser
{
    /// <summary>
    /// The saved voice if it is still installed, else the first voice for the language ("en" matches "en-US",
    /// "en-GB"…), else null.
    /// </summary>
    public static VoiceInfo? Pick(IReadOnlyList<VoiceInfo> voices, string? language, string? voiceId)
    {
        if (!string.IsNullOrEmpty(voiceId) && voices.FirstOrDefault(v => v.Id == voiceId) is { } saved) return saved;
        return voices.FirstOrDefault(v => MatchesLanguage(v.Language, language));
    }

    /// <summary>True when the voice's tag is the language or starts with it plus "-". A null language matches all.</summary>
    public static bool MatchesLanguage(string voiceLanguage, string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return true;
        return voiceLanguage.Equals(language, StringComparison.OrdinalIgnoreCase)
            || voiceLanguage.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase);
    }
}
