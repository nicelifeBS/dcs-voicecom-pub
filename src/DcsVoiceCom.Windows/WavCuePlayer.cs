using DcsVoiceCom.Config;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Profiles;
using static DcsVoiceCom.Windows.NativeMethods;

namespace DcsVoiceCom.Windows;

/// <summary>Plays the replaceable WAV cues asynchronously through winmm.</summary>
public sealed class WavCuePlayer : ICuePlayer
{
    private readonly Dictionary<Cue, string> _files = new();

    public WavCuePlayer(CueSettings settings, string? baseDirectory = null)
    {
        if (!settings.Enabled) return;
        baseDirectory ??= AppContext.BaseDirectory;
        Add(Cue.Accepted, settings.Accepted, "accepted.wav");
        Add(Cue.NoMatch, settings.NoMatch, "no_match.wav");
        Add(Cue.Refused, settings.Refused, "refused.wav");

        void Add(Cue cue, string? configured, string bundled)
        {
            var path = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(baseDirectory, "cues", bundled)
                : Path.GetFullPath(configured, baseDirectory);
            if (File.Exists(path)) _files[cue] = path;
        }
    }

    public void Play(Cue cue)
    {
        if (_files.TryGetValue(cue, out var path)) PlaySound(path, 0, SndFilename | SndAsync | SndNoDefault);
    }
}
