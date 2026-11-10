using DcsVoiceCom.Engine;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Tests;

public class CommandEngineTests
{
    private static (CommandEngine Engine, FakeCockpit Cockpit) Create(params Command[] commands)
    {
        var cockpit = new FakeCockpit();
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit,
            new EngineOptions { PollInterval = TimeSpan.FromMilliseconds(5) })
        {
            Profile = CompiledProfile.Compile(new Profile { Aircraft = "TestJet", Commands = [.. commands] }),
        };
        return (engine, cockpit);
    }

    private static RecognitionResult Heard(string phrase, double score = 100, string slots = "") =>
        new() { Phrase = phrase, Score = score, Slots = slots, Raw = phrase };

    private static Command Gear => new()
    {
        Name = "Gear up",
        Phrases = ["gear up"],
        Steps = [new BiosStep { Control = "GEAR_LEVER", Arg = "0", SkipIfAlready = true }],
    };

    [Fact]
    public async Task Runs_a_matched_command_and_plays_accepted()
    {
        var (engine, cockpit) = Create(Gear);
        cockpit.Integers["GEAR_LEVER"] = 1;

        var outcome = await engine.HandleAsync(Heard("gear up", 90));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(["GEAR_LEVER 0"], cockpit.Sent);
        Assert.Equal([Cue.Accepted], cockpit.Cues);
    }

    [Fact]
    public async Task A_press_pushes_in_holds_and_releases()
    {
        var press = new Command
        {
            Name = "Press U1",
            Phrases = ["press u one"],
            Steps =
            [
                new BiosStep { Control = "MFCD_L_U1", Arg = "1", PressMs = 20, SkipIfAlready = true },
                new BiosStep { Control = "SPRING_SW", Arg = "2", PressMs = 20, Release = "1" },
            ],
        };
        var (engine, cockpit) = Create(press);
        cockpit.Integers["MFCD_L_U1"] = 1;

        var outcome = await engine.HandleAsync(Heard("press u one"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(["MFCD_L_U1 1", "MFCD_L_U1 0", "SPRING_SW 2", "SPRING_SW 1"], cockpit.Sent);
    }

    [Fact]
    public async Task A_press_is_released_when_cancelled_while_pushing_in()
    {
        var press = new Command
        {
            Name = "Press U1",
            Phrases = ["press u one"],
            Steps = [new BiosStep { Control = "MFCD_L_U1", Arg = "1", PressMs = 20 }],
        };
        var (engine, cockpit) = Create(press);
        // The push-in line goes out, then the cancel lands before the send returns.
        cockpit.OnSend = (_, _, arg) => { if (arg == "1") throw new OperationCanceledException(); };

        var outcome = await engine.HandleAsync(Heard("press u one"));

        Assert.Equal(OutcomeStatus.Cancelled, outcome.Status);
        Assert.Equal(["MFCD_L_U1 1", "MFCD_L_U1 0"], cockpit.Sent);
    }

    [Fact]
    public async Task Dry_run_of_a_press_sends_nothing()
    {
        var press = new Command
        {
            Name = "Press U1",
            Phrases = ["press u one"],
            Steps = [new BiosStep { Control = "MFCD_L_U1", Arg = "1", PressMs = 5000 }],
        };
        var (engine, cockpit) = Create(press);

        var outcome = await engine.HandleAsync(Heard("press u one"), dryRun: true);

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Empty(cockpit.Sent);
        Assert.Contains(outcome.Trace, t => t.Contains("press MFCD_L_U1 1 (5000 ms), release 0"));
    }

    [Fact]
    public async Task Below_threshold_or_unknown_phrase_is_no_match()
    {
        var (engine, cockpit) = Create(Gear);

        Assert.Equal(OutcomeStatus.NoMatch, (await engine.HandleAsync(Heard("gear up", 84))).Status);
        Assert.Equal(OutcomeStatus.NoMatch, (await engine.HandleAsync(new RecognitionResult { Raw = "uh" })).Status);
        Assert.Equal(OutcomeStatus.NoMatch, (await engine.HandleAsync(Heard("flaps down"))).Status);
        Assert.Empty(cockpit.Sent);
        Assert.Equal([Cue.NoMatch, Cue.NoMatch, Cue.NoMatch], cockpit.Cues);
    }

    [Fact]
    public async Task Critical_command_below_critical_threshold_is_refused()
    {
        var jettison = new Command
        {
            Name = "Jettison",
            Critical = true,
            Phrases = ["emergency jettison"],
            Steps = [new BiosStep { Control = "EMERG_JETT", Arg = "1" }],
        };
        var (engine, cockpit) = Create(jettison);

        var refused = await engine.HandleAsync(Heard("emergency jettison", 94));
        var accepted = await engine.HandleAsync(Heard("emergency jettison", 95));

        Assert.Equal(OutcomeStatus.Refused, refused.Status);
        Assert.Equal(OutcomeStatus.Completed, accepted.Status);
        Assert.Equal(["EMERG_JETT 1"], cockpit.Sent);
        Assert.Equal([Cue.Refused, Cue.Accepted], cockpit.Cues);
    }

    [Fact]
    public async Task Startup_sequence_second_run_skips_steps_already_set()
    {
        string[] switches = ["BATTERY", "GEN_L", "GEN_R", "FUEL_PUMP", "APU", "INS", "RADAR", "IFF", "LIGHTS", "CANOPY"];
        var startup = new Command
        {
            Name = "Startup",
            Phrases = ["start up"],
            Steps = switches.Select(s => (Step)new BiosStep { Control = s, Arg = "1", SkipIfAlready = true }).ToList(),
        };
        var (engine, cockpit) = Create(startup);
        foreach (var s in switches) cockpit.Integers[s] = 0;

        var first = await engine.HandleAsync(Heard("start up"));
        var sentFirst = cockpit.Sent.Count;
        var second = await engine.HandleAsync(Heard("start up"));

        Assert.Equal(OutcomeStatus.Completed, first.Status);
        Assert.Equal(10, sentFirst);
        Assert.Equal(OutcomeStatus.Completed, second.Status);
        Assert.Equal(10, cockpit.Sent.Count);
        Assert.All(second.Trace, line => Assert.StartsWith("skip", line));
    }

    [Fact]
    public async Task Skip_if_already_sends_when_state_is_not_live()
    {
        var (engine, cockpit) = Create(Gear);
        cockpit.Integers["GEAR_LEVER"] = 0;
        cockpit.IsLive = false;

        await engine.HandleAsync(Heard("gear up"));

        Assert.Equal(["GEAR_LEVER 0"], cockpit.Sent);
    }

    [Fact]
    public async Task Wait_for_timeout_stops_the_sequence_with_refused()
    {
        var command = new Command
        {
            Name = "APU start",
            Phrases = ["apu start"],
            Steps =
            [
                new BiosStep { Control = "APU", Arg = "1" },
                new WaitForStep { Until = new Condition { Output = "APU_READY_LT", Value = "1" }, TimeoutMs = 60 },
                new BiosStep { Control = "ENGINE_CRANK", Arg = "1" },
            ],
        };
        var (engine, cockpit) = Create(command);
        cockpit.Integers["APU_READY_LT"] = 0;

        var outcome = await engine.HandleAsync(Heard("apu start"));

        Assert.Equal(OutcomeStatus.Refused, outcome.Status);
        Assert.Equal(["APU 1"], cockpit.Sent);
        Assert.Equal([Cue.Accepted, Cue.Refused], cockpit.Cues);
        Assert.Contains("timed out", outcome.Message);
    }

    [Fact]
    public async Task Wait_for_continues_once_the_state_arrives()
    {
        var command = new Command
        {
            Name = "APU start",
            Phrases = ["apu start"],
            Steps =
            [
                new BiosStep { Control = "APU", Arg = "1" },
                new WaitForStep { Until = new Condition { Output = "APU_READY_LT", Value = "1" }, TimeoutMs = 2000 },
                new BiosStep { Control = "ENGINE_CRANK", Arg = "1" },
            ],
        };
        var (engine, cockpit) = Create(command);
        cockpit.Integers["APU_READY_LT"] = 0;
        cockpit.OnSend = (c, id, arg) =>
        {
            if (id == "APU") _ = Task.Delay(50).ContinueWith(_ => c.Set("APU_READY_LT", 1));
        };

        var outcome = await engine.HandleAsync(Heard("apu start"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(["APU 1", "ENGINE_CRANK 1"], cockpit.Sent);
    }

    [Fact]
    public async Task If_else_branches_on_cockpit_state()
    {
        var command = new Command
        {
            Name = "Arm",
            Phrases = ["arm up"],
            Steps =
            [
                new IfStep
                {
                    Condition = new Condition { Output = "MASTER_ARM", Op = "=", Value = "0" },
                    Then = [new BiosStep { Control = "MASTER_ARM", Arg = "1" }],
                    Else = [new CueStep { Cue = Cue.Refused }],
                },
            ],
        };
        var (engine, cockpit) = Create(command);
        cockpit.Integers["MASTER_ARM"] = 0;

        await engine.HandleAsync(Heard("arm up"));
        await engine.HandleAsync(Heard("arm up"));

        Assert.Equal(["MASTER_ARM 1"], cockpit.Sent);
        Assert.Equal([Cue.Accepted, Cue.Accepted, Cue.Refused], cockpit.Cues);
    }

    [Fact]
    public async Task Bios_until_steps_a_knob_to_the_spoken_target()
    {
        var command = new Command
        {
            Name = "Heading",
            Phrases = ["heading {deg:0-359}"],
            Steps = [new BiosUntilStep { Control = "HDG_KNOB", Target = "{deg}", Scale = 10, DelayMs = 1, MaxSteps = 100 }],
        };
        var (engine, cockpit) = Create(command);
        cockpit.Integers["HDG_KNOB"] = 2000;
        cockpit.OnSend = (c, id, arg) => c.Integers[id] += arg == "INC" ? 100 : -100;

        var outcome = await engine.HandleAsync(Heard("heading {deg:0-359}", slots: "two seven zero"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(2700, cockpit.Integers["HDG_KNOB"]);
        Assert.Equal(7, cockpit.Sent.Count);
        Assert.Equal("270", outcome.Values["deg"]);
    }

    [Fact]
    public async Task Bios_until_gives_up_after_the_step_limit()
    {
        var command = new Command
        {
            Name = "Heading",
            Phrases = ["heading {deg:0-359}"],
            Steps = [new BiosUntilStep { Control = "HDG_KNOB", Target = "{deg}", DelayMs = 1, MaxSteps = 3 }],
        };
        var (engine, cockpit) = Create(command);
        cockpit.Integers["HDG_KNOB"] = 0;
        cockpit.OnSend = (_, _, _) => { }; // knob stuck

        var outcome = await engine.HandleAsync(Heard("heading {deg:0-359}", slots: "ninety"));

        Assert.Equal(OutcomeStatus.Refused, outcome.Status);
        Assert.Equal(3, cockpit.Sent.Count);
    }

    [Fact]
    public async Task Foreach_char_types_a_spoken_frequency()
    {
        var command = new Command
        {
            Name = "Radio",
            Phrases = ["radio one {freq}"],
            Steps =
            [
                new ForEachCharStep
                {
                    Value = "{freq}",
                    Map = new() { ["."] = "DOT" },
                    Steps = [new BiosStep { Control = "UFC_{char}", Arg = "1" }],
                },
                new KeyStep { Keys = "RCtrl+Enter" },
            ],
        };
        var (engine, cockpit) = Create(command);

        var outcome = await engine.HandleAsync(Heard("radio one {freq}", slots: "two five one decimal five"));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(["UFC_2 1", "UFC_5 1", "UFC_1 1", "UFC_DOT 1", "UFC_5 1"], cockpit.Sent);
        Assert.Equal(["RCtrl+Enter"], cockpit.Keys);
    }

    [Fact]
    public async Task Out_of_range_parameter_runs_nothing()
    {
        var command = new Command
        {
            Name = "TACAN",
            Phrases = ["tacan {channel:1-126} {band:X|Y}"],
            Steps = [new BiosStep { Control = "TACAN_CH", Arg = "{channel}" }],
        };
        var (engine, cockpit) = Create(command);

        var outcome = await engine.HandleAsync(Heard("tacan {channel:1-126} {band:X|Y}", slots: "one two seven x-ray"));

        Assert.Equal(OutcomeStatus.NoMatch, outcome.Status);
        Assert.Empty(cockpit.Sent);
        Assert.Equal([Cue.NoMatch], cockpit.Cues);
    }

    [Fact]
    public async Task New_command_cancels_a_running_sequence()
    {
        var slow = new Command
        {
            Name = "Slow",
            Phrases = ["slow"],
            Steps = [new BiosStep { Control = "A", Arg = "1" }, new WaitStep { Ms = 5000 }, new BiosStep { Control = "B", Arg = "1" }],
        };
        var (engine, cockpit) = Create(slow, Gear);

        var running = engine.HandleAsync(Heard("slow"));
        await Task.Delay(50);
        engine.Cancel();
        var cancelled = await running.WaitAsync(TimeSpan.FromSeconds(2));
        var next = await engine.HandleAsync(Heard("gear up"));

        Assert.Equal(OutcomeStatus.Cancelled, cancelled.Status);
        Assert.Equal(OutcomeStatus.Completed, next.Status);
        Assert.Equal(["A 1", "GEAR_LEVER 0"], cockpit.Sent);
    }

    [Fact]
    public async Task Dry_run_traces_steps_without_sending()
    {
        var command = new Command
        {
            Name = "Mixed",
            Phrases = ["mixed"],
            Steps =
            [
                new BiosStep { Control = "A", Arg = "1" },
                new KeyStep { Keys = "LShift+F1" },
                new WaitStep { Ms = 5000 },
                new WaitForStep { Until = new Condition { Output = "X", Value = "1" } },
            ],
        };
        var (engine, cockpit) = Create(command);

        var outcome = await engine.HandleAsync(Heard("mixed"), dryRun: true).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Empty(cockpit.Sent);
        Assert.Empty(cockpit.Keys);
        Assert.Empty(cockpit.Cues);
        Assert.Equal(["bios A 1", "key LShift+F1", "wait 5000 ms", "wait for X = 1 (timeout 10000 ms)"], outcome.Trace);
    }

    [Fact]
    public async Task Bad_key_name_fails_with_refused_cue()
    {
        var command = new Command { Name = "Bad", Phrases = ["bad"], Steps = [new KeyStep { Keys = "Ctrl+{x}" }] };
        var (engine, cockpit) = Create(command);

        var outcome = await engine.HandleAsync(Heard("bad"));

        Assert.Equal(OutcomeStatus.Failed, outcome.Status);
        Assert.Contains("{x}", outcome.Message);
    }
}

public class PlaceholderTests
{
    private static readonly Dictionary<string, string> Values = new() { ["channel"] = "73", ["freq"] = "251.0" };

    [Theory]
    [InlineData("{channel}", "73")]
    [InlineData("{channel/10}", "7")]
    [InlineData("{channel%10}", "3")]
    [InlineData("{channel*2}", "146")]
    [InlineData("{channel+1}-{channel-1}", "74-72")]
    [InlineData("UFC_{freq}", "UFC_251.0")]
    public void Applies(string text, string expected) => Assert.Equal(expected, Placeholders.Apply(text, Values));

    [Fact]
    public void Arithmetic_needs_whole_numbers() =>
        Assert.Throws<StepException>(() => Placeholders.Apply("{freq/10}", Values));
}

public class SampleProfileTests
{
    private static async Task<(CommandOutcome Outcome, FakeCockpit Cockpit)> Run(string phrase, string slots)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "profiles", "F-14.json");
        var cockpit = new FakeCockpit();
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit)
        {
            Profile = CompiledProfile.Compile(ProfileSerializer.Deserialize(File.ReadAllText(path))),
        };
        var outcome = await engine.HandleAsync(new RecognitionResult { Phrase = phrase, Score = 100, Slots = slots });
        return (outcome, cockpit);
    }

    [Fact]
    public async Task Tacan_channel_splits_across_wheels_and_maps_band()
    {
        var (outcome, cockpit) = await Run("tacan {channel:1-126} {band:X=0|Y=1}", "one hundred nine yankee");

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(["PLT_TACAN_DIAL_TENS 10", "PLT_TACAN_DIAL_ONES 9", "PLT_TACAN_CHANNEL 1"], cockpit.Sent);
    }

    [Fact]
    public async Task Tacan_mode_accepts_multi_word_option()
    {
        var (outcome, cockpit) = await Run("tacan mode {mode:off=0|receive=1|transmit=2|air to air=3|beacon=4}", "air to air");

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(["PLT_TACAN_MODE 3"], cockpit.Sent);
    }
}

public class CandidateTests
{
    [Fact]
    public async Task Falls_back_to_a_runner_up_whose_values_parse()
    {
        var cockpit = new FakeCockpit();
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit)
        {
            Profile = CompiledProfile.Compile(new Profile
            {
                Commands =
                [
                    new Command { Name = "Channel", Phrases = ["tacan {channel:1-126}"], Steps = [new BiosStep { Control = "CH", Arg = "{channel}" }] },
                    new Command { Name = "Mode", Phrases = ["tacan mode {mode:off=0|on=1}"], Steps = [new BiosStep { Control = "MODE", Arg = "{mode}" }] },
                ],
            }),
        };

        var outcome = await engine.HandleAsync(new RecognitionResult
        {
            Phrase = "tacan {channel:1-126}",
            Score = 100,
            Slots = "mode off",
            Candidates = [new RecognitionCandidate { Phrase = "tacan mode {mode:off=0|on=1}", Score = 100, Slots = "off" }],
        });

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal("Mode", outcome.Command);
        Assert.Equal(["MODE 0"], cockpit.Sent);
    }

    [Fact]
    public async Task Ignores_runner_ups_below_threshold()
    {
        var cockpit = new FakeCockpit();
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit)
        {
            Profile = CompiledProfile.Compile(new Profile
            {
                Commands = [new Command { Name = "Gear", Phrases = ["gear up"], Steps = [new BiosStep { Control = "G", Arg = "0" }] }],
            }),
        };

        var outcome = await engine.HandleAsync(new RecognitionResult
        {
            Phrase = "flaps up",
            Score = 90,
            Candidates = [new RecognitionCandidate { Phrase = "gear up", Score = 60 }],
        });

        Assert.Equal(OutcomeStatus.NoMatch, outcome.Status);
        Assert.Empty(cockpit.Sent);
    }

    [Fact]
    public async Task Matcher_reject_wins_over_a_high_score()
    {
        var cockpit = new FakeCockpit();
        var engine = new CommandEngine(cockpit, cockpit, cockpit, cockpit)
        {
            Profile = CompiledProfile.Compile(new Profile
            {
                Commands =
                [
                    new Command { Name = "Gear up", Phrases = ["gear up"], Steps = [new BiosStep { Control = "G", Arg = "0" }] },
                    new Command { Name = "Gear down", Phrases = ["gear down"], Steps = [new BiosStep { Control = "G", Arg = "1" }] },
                ],
            }),
        };

        var outcome = await engine.HandleAsync(new RecognitionResult
        {
            Phrase = "gear up",
            Score = 92,
            Reject = "too close to call: 'gear up' / 'gear down'",
            Candidates = [new RecognitionCandidate { Phrase = "gear down", Score = 90 }],
        });

        Assert.Equal(OutcomeStatus.NoMatch, outcome.Status);
        Assert.Equal("Gear up", outcome.Command);
        Assert.Equal("too close to call: 'gear up' / 'gear down'", outcome.Message);
        Assert.Empty(cockpit.Sent);
        Assert.Equal([Cue.NoMatch], cockpit.Cues);
    }
}
