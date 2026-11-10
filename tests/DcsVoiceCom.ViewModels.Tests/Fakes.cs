global using Xunit;
using DcsVoiceCom.Config;
using DcsVoiceCom.DcsBios;
using DcsVoiceCom.DcsHook;
using DcsVoiceCom.Engine;
using DcsVoiceCom.Profiles;
using DcsVoiceCom.Runtime;

namespace DcsVoiceCom.ViewModels.Tests;

internal sealed class FakeDialogs : IDialogs
{
    public Queue<bool> ConfirmAnswers { get; } = new();
    public Queue<bool?> ConfirmOrCancelAnswers { get; } = new();
    public Queue<string?> PromptAnswers { get; } = new();
    public string? LastInitial { get; private set; }
    public List<string> Asked { get; } = [];
    public IReadOnlyList<string>? LastSuggestions { get; private set; }

    public bool Confirm(string title, string message)
    {
        Asked.Add(message);
        return ConfirmAnswers.Count > 0 && ConfirmAnswers.Dequeue();
    }

    public bool? ConfirmOrCancel(string title, string message)
    {
        Asked.Add(message);
        return ConfirmOrCancelAnswers.Count > 0 ? ConfirmOrCancelAnswers.Dequeue() : null;
    }

    public string? Prompt(string title, string label, string initial = "", IReadOnlyList<string>? suggestions = null)
    {
        Asked.Add(label);
        LastInitial = initial;
        LastSuggestions = suggestions;
        return PromptAnswers.Count > 0 ? PromptAnswers.Dequeue() : null;
    }

    /// <summary>Runs on the seed window instead of showing it; return true to add.</summary>
    public Func<SeedViewModel, bool>? OnSeed { get; set; }

    public bool ShowSeed(SeedViewModel seed) => OnSeed?.Invoke(seed) ?? false;

    /// <summary>Runs on the "Enter value" window instead of showing it; return true for OK.</summary>
    public Func<EnterValueEditorViewModel, bool>? OnEnterValue { get; set; }

    public bool ShowEnterValue(EnterValueEditorViewModel editor) => OnEnterValue?.Invoke(editor) ?? false;
}

internal sealed class FakeRuntime : IVoiceComRuntime
{
    public string DcsAircraft { get; set; } = "";
    public string? ActiveAircraft => ProfileOverride ?? (DcsAircraft.Length > 0 ? DcsAircraft : null);
    /// <summary>Set to act like a variant using its family's profile; null means the active aircraft.</summary>
    public string? SharedProfile { get; set; }
    public string? ActiveProfile => ActiveAircraft is null ? null : SharedProfile ?? ActiveAircraft;
    public string? ProfileOverride { get; set; }
    public DcsSession? Session { get; set; }
    public string? ServerOverride { get; set; }
    public IReadOnlyList<string> ActiveServerProfiles { get; set; } = [];
    public CompiledProfile Profile { get; set; } = CompiledProfile.Empty;
    public StateBuffer State { get; } = new();
    public bool IsLive { get; set; }
    public long FrameCount { get; set; }
    public bool IsListening { get; set; }
    public bool SpeechReady { get; set; } = true;

    public int Reloads { get; private set; }
    public List<(string Text, bool DryRun)> Tests { get; } = [];
    public CommandOutcome? NextOutcome { get; set; }

    public event Action<CommandOutcome>? CommandExecuted;
    public event Action<CompiledProfile>? ProfileLoaded;
    public event Action<bool>? ListeningChanged;
    public event Action<string>? AircraftChanged;
    public event Action<DcsSession?>? SessionChanged;

    public Task SetServerOverrideAsync(string? serverProfile, CancellationToken ct = default)
    {
        ServerOverride = serverProfile;
        return ReloadProfileAsync(ct);
    }

    public void RaiseSession(DcsSession? session)
    {
        Session = session;
        SessionChanged?.Invoke(session);
    }

    public Task SetProfileOverrideAsync(string? aircraft, CancellationToken ct = default)
    {
        ProfileOverride = aircraft;
        return ReloadProfileAsync(ct);
    }

    public Task ReloadProfileAsync(CancellationToken ct = default)
    {
        Reloads++;
        ProfileLoaded?.Invoke(Profile);
        return Task.CompletedTask;
    }

    public List<string> SentBios { get; } = [];

    public Task SendBiosAsync(string control, string argument, CancellationToken ct = default)
    {
        SentBios.Add($"{control} {argument}");
        return Task.CompletedTask;
    }

    public Task<CommandOutcome> TestAsync(string text, bool dryRun, CancellationToken ct = default)
    {
        Tests.Add((text, dryRun));
        var outcome = NextOutcome ?? new CommandOutcome(OutcomeStatus.NoMatch, new RecognitionResult { Raw = text }, null, new Dictionary<string, string>(), [], "no phrase matched");
        CommandExecuted?.Invoke(outcome);
        return Task.FromResult(outcome);
    }

    public Task<IReadOnlyList<string>> ListInputDevicesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(["Headset"]);

    public void RaiseListening(bool on) => ListeningChanged?.Invoke(on);
    public void RaiseAircraft(string name) => AircraftChanged?.Invoke(name);
}

internal sealed class FakeBinder(PttSettings? result) : IPttBinder
{
    public Task<PttSettings?> DetectAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(result);
}

/// <summary>A temp folder with a profiles store and DCS-BIOS definitions for "TestJet".</summary>
internal sealed class Workspace : IDisposable
{
    public Workspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "dcsvoicecom-vm-tests", Guid.NewGuid().ToString("N"));
        Store = new ProfileStore(Path.Combine(Root, "profiles"));
        Definitions = new Definitions.DefinitionLoader(DcsVoiceCom.Tests.TestCatalog.WriteDefinitions(Path.Combine(Root, "json")));
    }

    public string Root { get; }
    public ProfileStore Store { get; }
    public Definitions.DefinitionLoader Definitions { get; }

    public void SaveTestJet(params Command[] commands) => Store.Save(new Profile { Aircraft = "TestJet", Commands = commands.ToList() });

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
