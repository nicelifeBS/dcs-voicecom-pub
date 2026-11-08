using DcsVoiceCom.Config;
using DcsVoiceCom.DcsHook;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.ViewModels.Tests;

public class ServerProfileViewModelTests
{
    private static readonly DcsSession Reapers = new()
    {
        Multiplayer = true, Server = "ShadowReapers Dynamic Campaign Modern PvE", Address = "65.109.30.115:10321",
        Mission = "SRDCModernCaucasus", Map = "Caucasus",
    };

    private static MainViewModel Create(Workspace ws, FakeDialogs dialogs, FakeRuntime? runtime = null)
    {
        var vm = new MainViewModel(ws.Store, ws.Definitions, dialogs, InlineUiThread.Instance);
        if (runtime != null) vm.AttachRuntime(runtime);
        return vm;
    }

    [Fact]
    public void Lists_server_profiles_after_aircraft_and_offers_them_to_pin()
    {
        using var ws = new Workspace();
        ws.SaveTestJet();
        ws.Store.Save(new Profile { Aircraft = "server-Reapers", Servers = ["ShadowReapers*"] });
        var vm = Create(ws, new FakeDialogs());

        Assert.Equal(["TestJet", "server-Reapers", "common"], vm.Profiles.Select(p => p.Name));
        Assert.Equal("Reapers (server)", vm.Profiles[1].Display);
        Assert.Equal(["Auto", "TestJet"], vm.OverrideOptions);
        Assert.Equal(["Auto", "Reapers"], vm.ServerOverrideOptions);

        vm.SelectedProfile = vm.Profiles[1];
        Assert.True(vm.IsServerProfile);
        Assert.False(vm.CanSeed);
        Assert.Equal("ShadowReapers*", vm.ServersText);
        vm.SelectedProfile = vm.Profiles[0];
        Assert.False(vm.IsServerProfile);
    }

    [Fact]
    public void New_server_profile_starts_from_the_current_server_and_saves_edited_servers()
    {
        using var ws = new Workspace();
        var dialogs = new FakeDialogs();
        dialogs.PromptAnswers.Enqueue("ShadowReapers*");
        var runtime = new FakeRuntime { DcsAircraft = "TestJet", Session = Reapers };
        var vm = Create(ws, dialogs, runtime);

        vm.NewServerProfileCommand.Execute(null);

        Assert.Equal(Reapers.Server, dialogs.LastInitial);
        Assert.Equal("server-ShadowReapers", vm.SelectedProfile!.Name);
        Assert.Equal(["ShadowReapers*"], ws.Store.TryLoad("server-ShadowReapers")!.Servers);
        Assert.True(runtime.Reloads > 0);

        vm.ServersText = "ShadowReapers*\r\n  65.109.30.115:*  \r\n\r\n";
        Assert.True(vm.IsDirty);
        vm.SaveCommand.Execute(null);
        Assert.Equal(["ShadowReapers*", "65.109.30.115:*"], ws.Store.TryLoad("server-ShadowReapers")!.Servers);
    }

    [Fact]
    public void Shows_the_server_and_marks_the_active_server_profile()
    {
        using var ws = new Workspace();
        ws.SaveTestJet();
        ws.Store.Save(new Profile { Aircraft = "server-Reapers", Servers = ["ShadowReapers*"] });
        var runtime = new FakeRuntime { DcsAircraft = "TestJet" };
        var vm = Create(ws, new FakeDialogs(), runtime);
        Assert.False(vm.HasServer);

        runtime.ActiveServerProfiles = ["server-Reapers"];
        runtime.RaiseSession(Reapers);
        runtime.ReloadProfileAsync();

        Assert.Equal($"Server · {Reapers.Server}", vm.ServerStatus);
        Assert.Equal("Profile · TestJet", vm.ActiveProfileText); // the green dot in the list shows the server profile
        Assert.True(vm.Profiles.Single(p => p.Name == "server-Reapers").IsActive);
        Assert.False(vm.Profiles.Single(p => p.Name == "common").IsActive);

        runtime.ActiveServerProfiles = [];
        runtime.RaiseSession(null);
        runtime.ReloadProfileAsync();
        Assert.False(vm.HasServer);
        Assert.False(vm.Profiles.Single(p => p.Name == "server-Reapers").IsActive);
    }

    [Fact]
    public void Pinning_a_server_profile_tells_the_runtime_and_the_app()
    {
        using var ws = new Workspace();
        ws.Store.Save(new Profile { Aircraft = "server-Reapers", Servers = ["ShadowReapers*"] });
        var runtime = new FakeRuntime { DcsAircraft = "TestJet" };
        var vm = Create(ws, new FakeDialogs(), runtime);
        string? remembered = "unset";
        vm.ServerOverrideChanged += p => remembered = p;

        vm.SelectedServerOverride = "Reapers";
        Assert.Equal("server-Reapers", runtime.ServerOverride);
        Assert.Equal("server-Reapers", remembered);

        vm.SelectedServerOverride = MainViewModel.AutoProfile;
        Assert.Null(runtime.ServerOverride);
        Assert.Null(remembered);
    }

    [Fact]
    public void Settings_install_and_remove_the_hook()
    {
        using var ws = new Workspace();
        var savedGames = Path.Combine(ws.Root, "Saved Games");
        Directory.CreateDirectory(Path.Combine(savedGames, "DCS"));
        var settings = new SettingsViewModel(new AppSettings(), hook: new DcsHookInstaller(7790, savedGames));

        Assert.StartsWith("Not installed", settings.HookStatus);
        Assert.True(settings.InstallHookCommand.CanExecute(null));
        Assert.False(settings.RemoveHookCommand.CanExecute(null));

        settings.InstallHookCommand.Execute(null);
        Assert.StartsWith("Installed", settings.HookStatus);
        Assert.True(File.Exists(Path.Combine(savedGames, "DCS", "Scripts", "Hooks", DcsHookInstaller.FileName)));
        Assert.False(settings.InstallHookCommand.CanExecute(null));

        settings.RemoveHookCommand.Execute(null);
        Assert.StartsWith("Not installed", settings.HookStatus);
    }
}
