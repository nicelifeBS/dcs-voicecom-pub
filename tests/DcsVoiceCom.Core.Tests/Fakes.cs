using DcsVoiceCom.DcsBios;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Input;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Tests;

/// <summary>A pretend cockpit: integer outputs in a dictionary, DCS-BIOS lines applied by an optional rule.</summary>
internal sealed class FakeCockpit : IBiosSender, IKeySender, IStateReader, ICuePlayer
{
    private readonly object _lock = new();

    public Dictionary<string, int> Integers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Strings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Sent { get; } = [];
    public List<string> Keys { get; } = [];
    public List<Cue> Cues { get; } = [];
    public bool IsLive { get; set; } = true;

    /// <summary>Applies a sent line to the state, e.g. set_state or INC/DEC.</summary>
    public Action<FakeCockpit, string, string>? OnSend { get; set; } = (c, id, arg) =>
    {
        if (int.TryParse(arg, out var value)) c.Integers[id] = value;
    };

    public Task SendAsync(string controlId, string argument, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Sent.Add($"{controlId} {argument}");
            OnSend?.Invoke(this, controlId, argument);
        }
        return Task.CompletedTask;
    }

    public Task SendAsync(KeyChord chord, TimeSpan hold, CancellationToken cancellationToken = default)
    {
        lock (_lock) Keys.Add(chord.ToString());
        return Task.CompletedTask;
    }

    public bool TryReadInteger(string identifier, out int value)
    {
        lock (_lock) return Integers.TryGetValue(identifier, out value);
    }

    public bool TryReadString(string identifier, out string value)
    {
        lock (_lock) return Strings.TryGetValue(identifier, out value!);
    }

    public void Set(string identifier, int value)
    {
        lock (_lock) Integers[identifier] = value;
    }

    public void Play(Cue cue)
    {
        lock (_lock) Cues.Add(cue);
    }
}
