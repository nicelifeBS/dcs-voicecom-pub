using DcsVoiceCom.DcsBios;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Runtime;

/// <summary>What the UI needs from the running app; <see cref="VoiceComRuntime"/> implements it.</summary>
public interface IVoiceComRuntime
{
    /// <summary>The aircraft DCS-BIOS reports, "" before the first frame.</summary>
    string DcsAircraft { get; }

    /// <summary>The aircraft whose profile is active: the override, else the reported aircraft.</summary>
    string? ActiveAircraft { get; }

    /// <summary>The pinned profile, or null for Auto.</summary>
    string? ProfileOverride { get; }

    CompiledProfile Profile { get; }

    /// <summary>The live export buffer, for showing control values.</summary>
    StateBuffer State { get; }

    /// <summary>True while export frames are arriving.</summary>
    bool IsLive { get; }

    long FrameCount { get; }
    bool IsListening { get; }
    /// <summary>True once Whistle and the microphone are open, so push-to-talk works.</summary>
    bool SpeechReady { get; }

    event Action<CommandOutcome>? CommandExecuted;
    event Action<CompiledProfile>? ProfileLoaded;
    event Action<bool>? ListeningChanged;
    event Action<string>? AircraftChanged;

    Task SetProfileOverrideAsync(string? aircraft, CancellationToken ct = default);
    Task ReloadProfileAsync(CancellationToken ct = default);
    /// <summary>Sends one DCS-BIOS line, e.g. a test click from the "Enter value" window.</summary>
    Task SendBiosAsync(string control, string argument, CancellationToken ct = default);

    Task<CommandOutcome> TestAsync(string text, bool dryRun, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListInputDevicesAsync(CancellationToken ct = default);
}
