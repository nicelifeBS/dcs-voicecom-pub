using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Tests;

public class ProfileTests
{
    private const string Json = """
    {
      "aircraft": "TestJet",
      "threshold": 80,
      "keywords": ["TACAN"],
      "commands": [
        {
          "name": "Gear up",
          "phrases": ["[gear;landing gear] up"],
          "steps": [ { "control": "GEAR_LEVER", "type": "bios", "arg": "0", "skip_if_already": true } ]
        },
        {
          "name": "Startup",
          "critical": true,
          "enabled": false,
          "phrases": ["start up"],
          "steps": [
            { "type": "key", "keys": "RCtrl+Home", "hold_ms": 100 },
            { "type": "wait_for", "until": { "output": "APU_READY_LT", "op": "=", "value": "1" }, "timeout_ms": 60000 },
            { "type": "if", "condition": { "output": "MASTER_ARM", "value": "0" },
              "then": [ { "type": "cue", "cue": "refused" } ],
              "else": [ { "type": "wait", "ms": 500 } ] },
            { "type": "bios_until", "control": "HDG_KNOB", "target": "{deg}", "scale": 182.04, "inc": "+3200", "dec": "-3200" },
            { "type": "foreach_char", "value": "{freq}", "map": { ".": "DOT" }, "steps": [ { "type": "bios", "control": "UFC_{char}", "arg": "1" } ] }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void Reads_all_step_types_with_type_anywhere_in_the_object()
    {
        var profile = ProfileSerializer.Deserialize(Json);

        var gear = Assert.IsType<BiosStep>(profile.Commands[0].Steps[0]);
        Assert.True(gear.SkipIfAlready);
        Assert.True(profile.Commands[0].Enabled);

        var startup = profile.Commands[1];
        Assert.True(startup.Critical);
        Assert.False(startup.Enabled);
        Assert.Equal(100, Assert.IsType<KeyStep>(startup.Steps[0]).HoldMs);
        Assert.Equal(60000, Assert.IsType<WaitForStep>(startup.Steps[1]).TimeoutMs);
        var ifStep = Assert.IsType<IfStep>(startup.Steps[2]);
        Assert.Equal("=", ifStep.Condition.Op);
        Assert.Equal(Cue.Refused, Assert.IsType<CueStep>(ifStep.Then[0]).Cue);
        Assert.Equal("+3200", Assert.IsType<BiosUntilStep>(startup.Steps[3]).Inc);
        Assert.Equal("DOT", Assert.IsType<ForEachCharStep>(startup.Steps[4]).Map["."]);
    }

    [Fact]
    public void Round_trips_and_keeps_disabled_flag()
    {
        var profile = ProfileSerializer.Deserialize(Json);
        var json = ProfileSerializer.Serialize(profile);
        var again = ProfileSerializer.Deserialize(json);

        Assert.Contains("\"type\": \"bios_until\"", json);
        Assert.Contains("\"enabled\": false", json);
        Assert.False(again.Commands[1].Enabled);
        Assert.Equal(ProfileSerializer.Serialize(again), json);
    }

    [Fact]
    public void A_press_round_trips_through_json()
    {
        var json = System.Text.Json.JsonSerializer.Serialize<Step>(new BiosStep { Control = "U1", Arg = "1", PressMs = 50, Release = "0" }, ProfileSerializer.Options);
        Assert.Contains("\"press_ms\": 50", json);
        Assert.Contains("\"release\": \"0\"", json);
        var step = Assert.IsType<BiosStep>(System.Text.Json.JsonSerializer.Deserialize<Step>(json, ProfileSerializer.Options));
        Assert.Equal(50, step.PressMs);
        Assert.Equal("0", step.Release);

        var plain = System.Text.Json.JsonSerializer.Serialize<Step>(new BiosStep { Control = "U1", Arg = "1" }, ProfileSerializer.Options);
        Assert.DoesNotContain("press_ms", plain);
        Assert.DoesNotContain("release", plain);
    }

    [Fact]
    public void Rejects_unknown_step_type()
    {
        var ex = Assert.ThrowsAny<System.Text.Json.JsonException>(() => ProfileSerializer.Deserialize(
            """{ "commands": [ { "name": "x", "steps": [ { "type": "teleport" } ] } ] }"""));
        Assert.Contains("teleport", ex.Message);
    }

    [Fact]
    public void Compiles_enabled_phrases_and_reports_duplicates()
    {
        var profile = ProfileSerializer.Deserialize(Json);
        profile.Commands.Add(new Command { Name = "Gear up again", Phrases = ["gear up"] });

        var compiled = CompiledProfile.Compile(profile);

        Assert.Equal(["gear up", "landing gear up"], compiled.Phrases.Order());
        Assert.Equal("Gear up", compiled.Find("gear up")!.Command.Name);
        Assert.Null(compiled.Find("start up"));
        Assert.Contains(compiled.Problems, p => p.Contains("Gear up again"));
    }

    [Fact]
    public void Slot_only_phrase_is_a_problem()
    {
        var compiled = CompiledProfile.Compile(new Profile { Commands = [new Command { Name = "Any", Phrases = ["{x:freq}"] }] });

        Assert.Empty(compiled.Phrases);
        Assert.Contains(compiled.Problems, p => p.Contains("besides slots"));
    }

    [Fact]
    public void Store_merges_common_profile_under_aircraft()
    {
        var store = new ProfileStore(Path.Combine(Path.GetTempPath(), "dcsvoicecom-tests", Guid.NewGuid().ToString("N")));
        store.Save(new Profile
        {
            Aircraft = "TestJet",
            Keywords = ["TACAN"],
            Commands = [new Command { Name = "Gear up", Phrases = ["gear up"] }, new Command { Name = "View", Phrases = ["my view"] }],
        });
        store.Save(new Profile
        {
            Aircraft = ProfileStore.CommonProfileName,
            Keywords = ["Tacan", "bingo"],
            Commands = [new Command { Name = "View", Phrases = ["common view"] }, new Command { Name = "F10 map", Phrases = ["map"] }],
        });

        var merged = store.LoadMerged("TestJet")!;

        Assert.Equal(["Gear up", "View", "F10 map"], merged.Commands.Select(c => c.Name));
        Assert.Equal(["my view"], merged.Commands[1].Phrases);
        Assert.Equal(["TACAN", "bingo"], merged.Keywords);
        Assert.Equal(["TestJet"], store.List());
        Assert.NotNull(store.LoadMerged("OtherJet")); // common alone still gives a profile
    }

    [Fact]
    public void A_renamed_file_saves_back_to_its_own_file()
    {
        // F-14BU.json copied to F-14.json still says "aircraft": "F-14BU" inside.
        var store = new ProfileStore(Path.Combine(Path.GetTempPath(), "dcsvoicecom-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(store.PathFor("F-14"), ProfileSerializer.Serialize(new Profile { Aircraft = "F-14BU" }));

        var profile = store.TryLoad("F-14")!;
        Assert.Equal("F-14", profile.Aircraft);
        profile.Commands.Add(new Command { Name = "Gear up" });
        store.Save(profile);

        Assert.False(store.Exists("F-14BU"));
        Assert.Single(store.TryLoad("F-14")!.Commands);
    }

    [Fact]
    public void Sample_profiles_compile_without_problems()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "profiles");
        var files = Directory.GetFiles(dir, "*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var compiled = CompiledProfile.Compile(ProfileSerializer.Deserialize(File.ReadAllText(file)));
            Assert.True(compiled.Problems.Count == 0, $"{Path.GetFileName(file)}: {string.Join("; ", compiled.Problems)}");
            Assert.NotEmpty(compiled.Phrases);
        }
    }

    [Fact]
    public void A_press_with_equal_in_and_out_or_a_negative_hold_is_a_problem()
    {
        var compiled = CompiledProfile.Compile(new Profile
        {
            Aircraft = "TestJet",
            Commands =
            [
                new Command { Name = "Same", Phrases = ["press same"], Steps = [new BiosStep { Control = "U1", Arg = "0", PressMs = 50 }] },
                new Command { Name = "Negative", Phrases = ["press negative"], Steps = [new BiosStep { Control = "U2", Arg = "1", PressMs = -1 }] },
                new Command { Name = "Fine", Phrases = ["press fine"], Steps = [new BiosStep { Control = "U3", Arg = "1", PressMs = 50 }] },
            ],
        });

        Assert.Contains(compiled.Problems, p => p.Contains("Same") && p.Contains("different in and out"));
        Assert.Contains(compiled.Problems, p => p.Contains("Negative") && p.Contains("0 ms or more"));
        Assert.DoesNotContain(compiled.Problems, p => p.Contains("Fine"));
    }
}
