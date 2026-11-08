using System.Net;
using System.Net.Sockets;
using System.Text;
using DcsVoiceCom.DcsHook;
using DcsVoiceCom.Profiles;

namespace DcsVoiceCom.Tests;

public class ServerProfileTests
{
    private static readonly DcsSession Reapers = new()
    {
        Multiplayer = true,
        Server = "ShadowReapers Dynamic Campaign Modern PvE",
        Address = "65.109.30.115:10321",
        Mission = "SRDCModernCaucasus",
        Map = "Caucasus",
    };

    private const string ReapersJson =
        """{"v":1,"event":"mission","mp":true,"server":" ShadowReapers Dynamic Campaign Modern PvE ","address":"65.109.30.115:10321","mission":"SRDCModernCaucasus","map":"Caucasus"}""";

    [Fact]
    public void Parses_a_mission_message_and_trims_the_server_name()
    {
        Assert.True(DcsSession.TryParse(ReapersJson, out var session));
        Assert.Equal(Reapers, session);
        Assert.True(session!.OnServer);
    }

    [Theory]
    [InlineData("""{"v":1,"event":"stop"}""", true)]
    [InlineData("""{"v":2,"event":"mission","mp":true,"server":"X"}""", false)]
    [InlineData("""{"event":"other"}""", false)]
    [InlineData("not json", false)]
    [InlineData("[1,2]", false)]
    public void Stop_ends_the_session_and_anything_else_is_ignored(string json, bool understood)
    {
        Assert.Equal(understood, DcsSession.TryParse(json, out var session));
        Assert.Null(session);
    }

    [Fact]
    public void Single_player_is_not_on_a_server()
    {
        Assert.True(DcsSession.TryParse("""{"v":1,"event":"mission","mp":false,"mission":"Training","map":"Caucasus"}""", out var session));
        Assert.False(session!.OnServer);
        Assert.False(ServerPattern.Matches("*", session));
    }

    [Fact]
    public void Tracker_reports_changes_only_and_ends_the_session_when_the_hook_goes_quiet()
    {
        var tracker = new DcsSessionTracker();
        var t0 = DateTimeOffset.UnixEpoch;
        Assert.True(tracker.Handle(ReapersJson, t0));
        Assert.False(tracker.Handle(ReapersJson, t0.AddSeconds(10))); // the 10 s repeat changes nothing
        Assert.False(tracker.Tick(t0.AddSeconds(35))); // 25 s since the last message
        Assert.True(tracker.Tick(t0.AddSeconds(41)));
        Assert.Null(tracker.Session);

        Assert.True(tracker.Handle(ReapersJson, t0.AddSeconds(50)));
        Assert.True(tracker.Handle("""{"v":1,"event":"stop"}""", t0.AddSeconds(51)));
        Assert.Null(tracker.Session);
        Assert.False(tracker.Handle("garbage", t0.AddSeconds(52)));
    }

    [Theory]
    [InlineData("ShadowReapers Dynamic Campaign Modern PvE", true)] // exact
    [InlineData("  shadowreapers dynamic campaign modern pve ", true)] // case and outer spaces
    [InlineData("ShadowReapers", false)] // without * the whole name must match
    [InlineData("ShadowReapers*", true)] // start
    [InlineData("*Campaign*", true)] // middle
    [InlineData("*PvE", true)] // end
    [InlineData("*PvP", false)]
    [InlineData("Shadow*Modern*", true)]
    [InlineData("65.109.30.115:10321", true)] // address
    [InlineData("65.109.30.115:*", true)]
    [InlineData("65.109.30.115:10320", false)]
    [InlineData("*", false)] // would match every server
    [InlineData("", false)]
    [InlineData("Shadow.Reapers*", false)] // regex characters are plain text
    public void Matches_server_names_and_addresses(string pattern, bool expected) =>
        Assert.Equal(expected, ServerPattern.Matches(pattern, Reapers));

    [Fact]
    public void Merges_aircraft_over_server_profiles_over_common()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        store.Save(new Profile { Aircraft = "TestJet", Commands = [Cmd("Gear up", "jet")] });
        store.Save(new Profile { Aircraft = "server-Reapers", Servers = ["ShadowReapers*"], Keywords = ["CTLD"],
            Commands = [Cmd("Gear up", "server"), Cmd("Request tanker", "server"), Cmd("Check in", "server")] });
        store.Save(new Profile { Aircraft = "server-Other", Servers = ["Other*"], Commands = [Cmd("Other", "other")] });
        store.Save(new Profile { Aircraft = "common", Commands = [Cmd("Check in", "common"), Cmd("Radio check", "common")] });

        Assert.Equal(["TestJet"], store.List());
        Assert.Equal(["server-Other", "server-Reapers"], store.ListServerProfiles());

        var servers = store.MatchingServerProfiles(Reapers);
        Assert.Equal(["server-Reapers"], servers);
        var merged = store.LoadMerged("TestJet", servers)!;
        Assert.Equal(["Gear up/jet", "Request tanker/server", "Check in/server", "Radio check/common"],
            merged.Commands.Select(c => $"{c.Name}/{c.Description}"));
        Assert.Contains("CTLD", merged.Keywords);

        Assert.Empty(store.MatchingServerProfiles(null));
        Assert.Equal(["Gear up/jet", "Check in/common", "Radio check/common"],
            store.LoadMerged("TestJet")!.Commands.Select(c => $"{c.Name}/{c.Description}"));
    }

    [Fact]
    public void A_broken_server_profile_is_skipped_and_reported()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        store.Save(new Profile { Aircraft = "server-Good", Servers = ["*Reapers*"] });
        File.WriteAllText(Path.Combine(dir.Path, "server-Bad.json"), "{ not json");
        var problems = new List<string>();
        Assert.Equal(["server-Good"], store.MatchingServerProfiles(Reapers, problems));
        Assert.Single(problems);
    }

    [Fact]
    public void Servers_is_saved_only_for_server_profiles()
    {
        Assert.DoesNotContain("servers", ProfileSerializer.Serialize(new Profile { Aircraft = "TestJet" }));
        var json = ProfileSerializer.Serialize(new Profile { Aircraft = "server-X", Servers = ["X*"] });
        Assert.Equal(["X*"], ProfileSerializer.Deserialize(json).Servers);
    }

    [Fact]
    public void Installs_the_hook_with_the_port_into_every_DCS_folder()
    {
        using var dir = new TempDir();
        var installer = new DcsHookInstaller(7791, dir.Path);
        Assert.Equal(HookStatus.NoDcs, installer.Status());

        Directory.CreateDirectory(Path.Combine(dir.Path, "DCS"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "DCS.openbeta"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "Other Game"));
        Assert.Equal(HookStatus.NotInstalled, installer.Status());

        var written = installer.Install();
        Assert.Equal(2, written.Count);
        Assert.Equal(HookStatus.Installed, installer.Status());
        var script = File.ReadAllText(written[0]);
        Assert.Contains("local PORT = 7791", script);
        Assert.DoesNotContain("{{PORT}}", script);
        Assert.DoesNotContain("getTheatreID()", script.Replace("Never DCS.getTheatreID()", ""));

        Assert.Equal(HookStatus.OutOfDate, new DcsHookInstaller(7790, dir.Path).Status()); // another port
        File.Delete(written[1]);
        Assert.Equal(HookStatus.OutOfDate, installer.Status()); // only one folder has it

        installer.Install();
        Assert.Equal(2, installer.Remove().Count);
        Assert.Equal(HookStatus.NotInstalled, installer.Status());
    }

    [Fact]
    public async Task Listener_receives_hook_messages_over_udp()
    {
        var port = FreeUdpPort();
        await using var listener = new DcsHookListener(port);
        var changed = new TaskCompletionSource<DcsSession?>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionChanged += s => changed.TrySetResult(s);
        listener.Start();

        using var udp = new UdpClient();
        var bytes = Encoding.UTF8.GetBytes(ReapersJson);
        await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));

        var session = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Reapers, session);
        Assert.Equal(Reapers, listener.Session);
    }

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static Command Cmd(string name, string source) => new() { Name = name, Description = source, Phrases = [name.ToLowerInvariant()] };

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dcsvoicecom-server-tests", Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
