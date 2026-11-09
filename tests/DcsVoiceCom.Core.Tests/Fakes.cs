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

/// <summary>
/// A pretend F-14 pilot UHF: four spring wheels (2 = click up, 0 = click down). It shows the frequency on
/// PLT_UHF_DISP and on the old per-wheel read-backs.
/// </summary>
internal sealed class FakeUhf
{
    public int Th = 25, One = 1, D3, Q;
    public bool InvertTenth, Stuck;

    public void Attach(FakeCockpit cockpit)
    {
        Publish(cockpit);
        cockpit.OnSend = (c, id, arg) =>
        {
            if (arg is not ("2" or "0") || Stuck) return;
            var dir = arg == "2" ? 1 : -1;
            switch (id)
            {
                case "PLT_UHF1_110_DIAL": Th = Wrap(Th + dir, 22, 39); break;
                case "PLT_UHF1_1_DIAL": One = Wrap(One + dir, 0, 9); break;
                case "PLT_UHF1_01_DIAL": D3 = Wrap(D3 + (InvertTenth ? -dir : dir), 0, 9); break;
                case "PLT_UHF1_025_DIAL": Q = Wrap(Q + dir, 0, 3); break;
            }
            Publish(c);
        };
    }

    private void Publish(FakeCockpit c)
    {
        c.Integers["PLT_UHF_HIGH_FREQ"] = Th * 10 + One;
        c.Integers["PLT_UHF_DIAL3_FREQ"] = D3;
        c.Integers["PLT_UHF_DIAL4_FREQ"] = Q * 25;
        c.Strings["PLT_UHF_DISP"] = $"{Th * 10 + One}.{D3}{Q * 25:00}";
    }

    private static int Wrap(int v, int min, int max) => v > max ? min : v < min ? max : v;
}
