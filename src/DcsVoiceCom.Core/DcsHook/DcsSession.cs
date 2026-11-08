using System.Text.Json;

namespace DcsVoiceCom.DcsHook;

/// <summary>What the DCS hook reports about the running mission. Server and address are empty in single player.</summary>
public sealed record DcsSession
{
    public bool Multiplayer { get; init; }
    public string Server { get; init; } = "";
    public string Address { get; init; } = "";
    public string Mission { get; init; } = "";
    public string Map { get; init; } = "";

    /// <summary>True on a multiplayer server we know the name or address of.</summary>
    public bool OnServer => Multiplayer && (Server.Length > 0 || Address.Length > 0);

    /// <summary>The server name, or its address when the name is unknown.</summary>
    public string DisplayName => Server.Length > 0 ? Server : Address;

    public override string ToString() =>
        OnServer ? $"{DisplayName} ({Mission}, {Map})" : $"single player ({Mission}, {Map})";

    /// <summary>
    /// Reads one hook message. <c>{"v":1,"event":"mission",...}</c> gives a session, <c>{"event":"stop"}</c> gives
    /// <see cref="Stopped"/>. Anything else (bad JSON, unknown event, newer version) gives null.
    /// </summary>
    public static bool TryParse(string json, out DcsSession? session)
    {
        session = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() > 1) return false;
            var evt = Text(root, "event");
            if (evt == "stop") return true; // session stays null: the mission ended
            if (evt != "mission") return false;
            session = new DcsSession
            {
                Multiplayer = root.TryGetProperty("mp", out var mp) && mp.ValueKind == JsonValueKind.True,
                Server = Text(root, "server").Trim(),
                Address = Text(root, "address").Trim(),
                Mission = Text(root, "mission").Trim(),
                Map = Text(root, "map").Trim(),
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

/// <summary>
/// Turns hook messages into the current session. The hook repeats its message every 10 s while a mission runs;
/// when nothing arrives for <see cref="Timeout"/> (DCS quit or crashed) the session ends.
/// </summary>
public sealed class DcsSessionTracker
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private DateTimeOffset _lastMessageAt;

    public DcsSession? Session { get; private set; }

    /// <summary>Handles one message; true when the session changed.</summary>
    public bool Handle(string json, DateTimeOffset now)
    {
        if (!DcsSession.TryParse(json, out var session)) return false;
        _lastMessageAt = now;
        return Set(session);
    }

    /// <summary>Ends the session when the hook went quiet; true when that changed it.</summary>
    public bool Tick(DateTimeOffset now) => Session != null && now - _lastMessageAt > Timeout && Set(null);

    private bool Set(DcsSession? session)
    {
        if (Equals(Session, session)) return false;
        Session = session;
        return true;
    }
}
