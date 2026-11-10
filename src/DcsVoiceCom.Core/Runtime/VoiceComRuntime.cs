using System.Diagnostics;
using DcsVoiceCom.Config;
using DcsVoiceCom.DcsBios;
using DcsVoiceCom.DcsHook;
using DcsVoiceCom.Definitions;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Input;
using DcsVoiceCom.Profiles;
using DcsVoiceCom.Speech;

namespace DcsVoiceCom.Runtime;

/// <summary>
/// Wires the app together: DCS-BIOS link, definitions, profiles, speech (microphone, Whistle, phrase matching),
/// push-to-talk and the command engine. Shared by the console host and the WPF app.
/// </summary>
public sealed class VoiceComRuntime : IVoiceComRuntime, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly DcsBiosConnection _dcsBios;
    private readonly DcsHookListener _hook;
    private readonly DefinitionLoader _definitions;
    private readonly ProfileStore _profiles;
    private readonly IMicrophoneProvider? _microphones;
    private SpeechService? _speech;
    private readonly CommandEngine _engine;
    private readonly IPushToTalk? _ptt;
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly Stopwatch _pttWatch = new();
    private ControlCatalog _catalog = ControlCatalog.Empty;
    private string? _override;
    private string? _serverOverride;
    private (string Aircraft, string Profile)? _activeProfile;
    private IReadOnlyList<string> _activeServerProfiles = [];
    private bool _listening;
    private readonly CatalogBiosSender _sender;

    /// <param name="microphones">Microphone capture; null leaves push-to-talk off (typed tests still work).</param>
    public VoiceComRuntime(AppSettings settings, IKeySender keys, ICuePlayer cues, IPushToTalk? ptt, Action<string>? log = null,
        IMicrophoneProvider? microphones = null)
    {
        _settings = settings;
        Log = log ?? (_ => { });
        _override = settings.ProfileOverride;
        _serverOverride = settings.ServerProfileOverride;
        _dcsBios = new DcsBiosConnection(settings.DcsBios);
        _hook = new DcsHookListener(settings.DcsHook.Port);
        _definitions = new DefinitionLoader(settings.DcsBiosJsonPath);
        _profiles = new ProfileStore(settings.ProfilesPath);
        _microphones = microphones;

        var state = new CatalogStateReader(_dcsBios.State, () => _catalog,
            () => DateTimeOffset.UtcNow - _dcsBios.LastFrameAt < TimeSpan.FromSeconds(2));
        _sender = new CatalogBiosSender(_dcsBios, () => _catalog);
        _engine = new CommandEngine(_sender, keys, state, cues,
            new EngineOptions { KeyHold = TimeSpan.FromMilliseconds(settings.KeyHoldMs) });
        _engine.Executed += OnExecuted;
        StateReader = state;

        _ptt = ptt;
        if (_ptt != null)
        {
            _ptt.Pressed += OnPttPressed;
            _ptt.Released += OnPttReleased;
        }
    }

    public Action<string> Log { get; }

    public DcsBiosConnection DcsBios => _dcsBios;
    public string DcsAircraft => _dcsBios.Aircraft;
    public long FrameCount => _dcsBios.FrameCount;
    StateBuffer IVoiceComRuntime.State => _dcsBios.State;

    /// <summary>Reads profile outputs by name through the active catalog.</summary>
    public IStateReader StateReader { get; }
    public ControlCatalog Catalog => _catalog;
    public CompiledProfile Profile => _engine.Profile;
    public ProfileStore Profiles => _profiles;
    public DefinitionLoader Definitions => _definitions;

    /// <summary>The pinned profile, or null for Auto (follow _ACFT_NAME).</summary>
    public string? ProfileOverride => _override;

    /// <summary>The aircraft whose profile should be active.</summary>
    public string? ActiveAircraft => _override ?? (IsRealAircraft(_dcsBios.Aircraft) ? _dcsBios.Aircraft : null);

    /// <summary>
    /// The aircraft profile in use: the active aircraft's own file, else the one its variants share
    /// (F-16D_52 uses F-16C_50). Null before an aircraft is known.
    /// </summary>
    public string? ActiveProfile =>
        ActiveAircraft is { } aircraft && _activeProfile is { } p && p.Aircraft.Equals(aircraft, StringComparison.OrdinalIgnoreCase)
            ? p.Profile
            : ActiveAircraft;

    public DcsSession? Session => _hook.Session;
    public string? ServerOverride => _serverOverride;
    public IReadOnlyList<string> ActiveServerProfiles => _activeServerProfiles;

    public bool IsListening => _listening;
    /// <summary>True once Whistle and the microphone are open, so push-to-talk works.</summary>
    public bool SpeechReady => _speech?.IsReady == true;
    public bool IsLive => DateTimeOffset.UtcNow - _dcsBios.LastFrameAt < TimeSpan.FromSeconds(2);

    public event Action<CommandOutcome>? CommandExecuted;
    public event Action<CompiledProfile>? ProfileLoaded;
    public event Action<bool>? ListeningChanged;
    public event Action<string>? AircraftChanged;
    public event Action<DcsSession?>? SessionChanged;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!_definitions.Exists)
            Log($"DCS-BIOS definitions not found at {_definitions.JsonDirectory}; install DCS-BIOS (Skunkworks) or set dcsBiosJsonPath.");

        _dcsBios.AircraftChanged += OnAircraftChanged;
        _dcsBios.ReceiveError += ex => Log($"DCS-BIOS export: handling a packet failed, still listening: {ex.Message}");
        _dcsBios.Start();
        Log($"Listening for DCS-BIOS export on {_settings.DcsBios.ExportMulticastGroup}:{_settings.DcsBios.ExportPort}");

        _hook.SessionChanged += OnSessionChanged;
        try
        {
            _hook.Start();
            Log($"Listening for the DCS hook on 127.0.0.1:{_hook.Port} (server profiles)");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Log($"DCS hook: can't listen on 127.0.0.1:{_hook.Port} ({ex.Message}); server profiles only work when pinned.");
        }

        // Without a model or microphone the rest still works: live state, profile editing and typed tests.
        _speech = await SpeechService.OpenAsync(_settings.Speech, _settings.Language, _microphones, m => Log("[speech] " + m), ct: ct)
            .ConfigureAwait(false);

        await ReloadProfileAsync(ct).ConfigureAwait(false);
        _ptt?.Start();
    }

    /// <summary>Pins a profile (from the app or tray menu), or returns to Auto with null.</summary>
    public async Task SetProfileOverrideAsync(string? aircraft, CancellationToken ct = default)
    {
        _override = string.IsNullOrWhiteSpace(aircraft) ? null : aircraft;
        Log(_override is null ? "Profile: Auto" : $"Profile pinned to {_override}");
        await ReloadProfileAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Pins a server profile (for testing without DCS), or returns to Auto with null.</summary>
    public async Task SetServerOverrideAsync(string? serverProfile, CancellationToken ct = default)
    {
        _serverOverride = string.IsNullOrWhiteSpace(serverProfile) ? null : serverProfile;
        Log(_serverOverride is null ? "Server profile: Auto" : $"Server profile pinned to {_serverOverride}");
        await ReloadProfileAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reloads definitions and profile for the active aircraft and loads its phrases for matching.</summary>
    public async Task ReloadProfileAsync(CancellationToken ct = default)
    {
        await _switchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var aircraft = ActiveAircraft;
            if (aircraft is null)
            {
                Log("No aircraft detected yet; waiting for DCS-BIOS.");
                return;
            }

            _catalog = _definitions.Exists ? _definitions.LoadAircraft(aircraft) : ControlCatalog.Empty;
            var problems = new List<string>();
            var servers = ServerLayers(problems);
            foreach (var problem in problems) Log(problem);
            var names = servers.Select(p => p.Aircraft).ToList();
            if (!names.SequenceEqual(_activeServerProfiles, StringComparer.OrdinalIgnoreCase))
                Log(names.Count == 0 ? "Server profile: none" : $"Server profile: {string.Join(", ", names)}");
            _activeServerProfiles = names;
            var profileName = ResolveProfile(aircraft);
            if (!profileName.Equals(aircraft, StringComparison.OrdinalIgnoreCase) &&
                !profileName.Equals(ActiveProfile, StringComparison.OrdinalIgnoreCase))
                Log($"No profile for {aircraft}; using {profileName}");
            _activeProfile = (aircraft, profileName);
            var profile = _profiles.LoadMerged(profileName, servers) ?? new Profile { Aircraft = profileName };
            var compiled = CompiledProfile.Compile(profile);
            foreach (var problem in compiled.Problems) Log($"Profile problem: {problem}");
            if (_catalog.Count > 0)
            {
                foreach (var id in CompiledProfile.FindMissingControls(profile, _catalog))
                    Log($"Profile uses {id}, which DCS-BIOS no longer defines for {aircraft}");
            }

            _engine.Profile = compiled;
            if (_speech is { } speech)
            {
                var count = speech.LoadPhrases(_settings.Language, compiled.Entries, compiled.Keywords);
                Log($"Profile {profileName}: {compiled.Profile.Commands.Count(c => c.Enabled)} commands, {count} phrases, {_catalog.Count} controls");
            }
            ProfileLoaded?.Invoke(compiled);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    /// <summary>The aircraft's own profile, else its variants' shared one when that file exists, else the aircraft.</summary>
    private string ResolveProfile(string aircraft)
    {
        if (_override is not null || _profiles.Exists(aircraft) || !_definitions.Exists) return aircraft;
        try
        {
            var main = _definitions.BaseAircraft(aircraft);
            return _profiles.Exists(main) ? main : aircraft;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return aircraft; // a broken aliases file: the catalog load reports it
        }
    }

    /// <summary>Microphones that can be opened.</summary>
    public Task<IReadOnlyList<string>> ListInputDevicesAsync(CancellationToken ct = default) =>
        _microphones is null ? Task.FromResult<IReadOnlyList<string>>([]) : Task.Run(_microphones.ListDevices, ct);

    /// <summary>Sends one DCS-BIOS line (the "Enter value" window's test clicks).</summary>
    public Task SendBiosAsync(string control, string argument, CancellationToken ct = default) => _sender.SendAsync(control, argument, ct);

    /// <summary>Runs typed text through the phrase matcher and the engine (test panel, replay).</summary>

    public async Task<CommandOutcome> TestAsync(string text, bool dryRun, CancellationToken ct = default)
    {
        var recognition = _speech?.Match(text) ?? new RecognitionResult { Raw = text, Error = "speech is still starting" };
        return await _engine.HandleAsync(recognition, dryRun).ConfigureAwait(false);
    }

    private void OnAircraftChanged(string aircraft)
    {
        Log($"Aircraft: {(IsRealAircraft(aircraft) ? aircraft : "none")}");
        AircraftChanged?.Invoke(aircraft);
        if (_override is null && IsRealAircraft(aircraft)) _ = RunLogged(ReloadProfileAsync);
    }

    /// <summary>The pinned server profile, or the ones matching the current session. Unreadable files are skipped.</summary>
    private IReadOnlyList<Profile> ServerLayers(List<string>? problems = null) =>
        _serverOverride is { } pinned
            ? _profiles.TryLoadServerProfile(pinned, problems) is { } profile ? [profile] : []
            : _profiles.MatchingServerProfiles(_hook.Session, problems);

    private void OnSessionChanged(DcsSession? session)
    {
        Log(session is null ? "DCS hook: mission ended" : $"DCS hook: {session}");
        SessionChanged?.Invoke(session);
        if (_serverOverride is not null || ActiveAircraft is null) return;
        // A new mission or map on the same server keeps the same server profiles: no reload needed.
        var names = ServerLayers().Select(p => p.Aircraft);
        if (!names.SequenceEqual(_activeServerProfiles, StringComparer.OrdinalIgnoreCase)) _ = RunLogged(ReloadProfileAsync);
    }

    private void OnPttPressed()
    {
        _engine.Cancel();
        _pttWatch.Restart();
        _listening = true;
        ListeningChanged?.Invoke(true);
        _speech?.Start();
    }

    private void OnPttReleased()
    {
        if (!_listening) return;
        _listening = false;
        ListeningChanged?.Invoke(false);
        _pttWatch.Restart();
        _ = RunLogged(async ct =>
        {
            var speech = _speech;
            var recognition = speech is null
                ? new RecognitionResult { Error = "speech is still starting" }
                : await Task.Run(speech.Stop, ct).ConfigureAwait(false);
            await _engine.HandleAsync(recognition).ConfigureAwait(false);
        });
    }

    private void OnExecuted(CommandOutcome outcome)
    {
        var r = outcome.Recognition;
        var values = outcome.Values.Count > 0 ? " " + string.Join(", ", outcome.Values.Select(kv => $"{kv.Key}={kv.Value}")) : "";
        Log($"\"{r.Raw}\" → {r.Phrase ?? "-"} ({r.Score:0}, {r.Ms:0} ms) → {outcome.Status}{(outcome.Command is null ? "" : $" [{outcome.Command}]")}{values}" +
            (outcome.Message is null ? "" : $": {outcome.Message}") +
            (_pttWatch.IsRunning ? $" (release→done {_pttWatch.ElapsedMilliseconds} ms)" : ""));
        _pttWatch.Reset();
        CommandExecuted?.Invoke(outcome);
    }

    private async Task RunLogged(Func<CancellationToken, Task> action)
    {
        try
        {
            await action(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
        }
    }

    private static bool IsRealAircraft(string name) => name.Length > 0 && !name.Equals("NONE", StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        _ptt?.Dispose();
        _engine.Cancel();
        _speech?.Dispose();
        await _hook.DisposeAsync().ConfigureAwait(false);
        await _dcsBios.DisposeAsync().ConfigureAwait(false);
    }
}
