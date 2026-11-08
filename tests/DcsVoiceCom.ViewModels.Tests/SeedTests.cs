using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.ViewModels.Tests;

public class SeedTests
{
    private static MainViewModel Open(Workspace ws, FakeDialogs dialogs)
    {
        var vm = new MainViewModel(ws.Store, ws.Definitions, dialogs, InlineUiThread.Instance);
        vm.SelectedProfile = vm.Profiles.First(p => p.Name == "TestJet");
        return vm;
    }

    [Fact]
    public void Seeding_adds_ticked_panels_disabled_and_skips_what_you_have()
    {
        using var ws = new Workspace();
        ws.SaveTestJet(new Command { Name = "Gear up", Phrases = ["gear up"], Steps = [new BiosStep { Control = "GEAR_LEVER", Arg = "0" }] });
        SeedViewModel? shown = null;
        var dialogs = new FakeDialogs
        {
            OnSeed = seed =>
            {
                shown = seed;
                Assert.Equal(["Gear", "Weapons"], seed.Categories.Select(c => c.Name));
                Assert.Equal("1 of 2", seed.Categories[0].CountText);
                Assert.False(seed.CanAdd);
                seed.Categories[0].IsChecked = true;
                Assert.Equal("Add 1 command", seed.AddText);
                return true;
            },
        };
        var vm = Open(ws, dialogs);
        Assert.True(vm.SeedCommand.CanExecute(null));

        vm.SeedCommand.Execute(null);

        Assert.NotNull(shown);
        Assert.Contains(shown!.Preview, i => i.IsCovered && i.StatusText.Contains("Gear up"));
        Assert.Equal(["Gear up", "Landing gear lever down"], vm.Commands.Select(c => c.Name));
        var added = vm.Commands[1];
        Assert.False(added.Enabled);
        Assert.True(added.Seeded);
        Assert.True(vm.IsDirty);
        Assert.Equal(1, vm.SeededCount);
        Assert.Equal("2 commands · 1 seeded", vm.CommandCountText);
    }

    [Fact]
    public void Cancel_adds_nothing()
    {
        using var ws = new Workspace();
        ws.SaveTestJet();
        var vm = Open(ws, new FakeDialogs { OnSeed = seed => { seed.SelectAllCommand.Execute(null); return false; } });

        vm.SeedCommand.Execute(null);

        Assert.Empty(vm.Commands);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Enabling_or_editing_adopts_a_seeded_command_and_remove_keeps_it()
    {
        using var ws = new Workspace();
        ws.SaveTestJet();
        var dialogs = new FakeDialogs { OnSeed = seed => { seed.SelectAllCommand.Execute(null); return true; } };
        var vm = Open(ws, dialogs);
        vm.SeedCommand.Execute(null);
        Assert.Equal(3, vm.SeededCount);

        vm.Commands[0].Enabled = true;
        Assert.False(vm.Commands[0].Seeded);
        vm.SelectedCommand = vm.Commands[1];
        vm.Editor!.VoiceLine = "Gear down";
        Assert.False(vm.Commands[1].Seeded);
        Assert.Equal(1, vm.SeededCount);

        dialogs.ConfirmAnswers.Enqueue(true);
        vm.RemoveSeededCommand.Execute(null);
        Assert.Equal(["Gear down", "Landing gear lever down"], vm.Commands.Select(c => c.Name));
        Assert.False(vm.RemoveSeededCommand.CanExecute(null));
    }

    [Fact]
    public void Filter_and_hide_seeded_narrow_the_list()
    {
        using var ws = new Workspace();
        ws.SaveTestJet(new Command { Name = "Gear up", Phrases = ["gear up"] });
        var vm = Open(ws, new FakeDialogs { OnSeed = seed => { seed.SelectAllCommand.Execute(null); return true; } });
        vm.SeedCommand.Execute(null);

        vm.CommandFilter = "gear";
        Assert.Equal(["Gear up", "Landing gear lever down", "Landing gear lever up"], vm.Commands.Where(c => c.IsVisible).Select(c => c.Name));
        vm.HideSeeded = true;
        Assert.Equal(["Gear up"], vm.Commands.Where(c => c.IsVisible).Select(c => c.Name));
        Assert.Equal("1 of 4 commands · 3 seeded", vm.CommandCountText);
        vm.CommandFilter = "";
        vm.HideSeeded = false;
        Assert.All(vm.Commands, c => Assert.True(c.IsVisible));
    }

    [Fact]
    public void Seed_search_looks_across_panels()
    {
        using var ws = new Workspace();
        ws.SaveTestJet();
        var vm = Open(ws, new FakeDialogs
        {
            OnSeed = seed =>
            {
                seed.Search = "master";
                Assert.Equal(["Master arm position {position:0-2}"], seed.Preview.Select(p => p.VoiceLine));
                Assert.True(seed.Preview[0].Critical);
                seed.Search = "";
                Assert.Equal("Gear", seed.PreviewTitle);
                return false;
            },
        });
        vm.SeedCommand.Execute(null);
    }

    [Fact]
    public void The_common_profile_cannot_be_seeded()
    {
        using var ws = new Workspace();
        ws.SaveTestJet();
        var vm = Open(ws, new FakeDialogs());
        vm.SelectedProfile = vm.Profiles.First(p => p.IsCommon);
        Assert.False(vm.SeedCommand.CanExecute(null));
    }
}
