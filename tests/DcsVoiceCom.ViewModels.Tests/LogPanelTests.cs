using DcsVoiceCom.Config;

namespace DcsVoiceCom.ViewModels.Tests;

public class LogPanelTests
{
    [Fact]
    public void Log_panel_is_hidden_by_default_and_reports_each_change()
    {
        using var ws = new Workspace();
        var vm = new MainViewModel(ws.Store, ws.Definitions, new FakeDialogs(), InlineUiThread.Instance);
        var changes = new List<bool>();
        vm.ShowLogChanged += changes.Add;

        Assert.False(vm.ShowLog);
        Assert.False(new AppSettings().ShowLog);

        vm.ToggleLogCommand.Execute(null);
        vm.ShowLog = true; // no change: no event
        vm.ToggleLogCommand.Execute(null);

        Assert.Equal([true, false], changes);
    }

    [Fact]
    public void Log_panel_state_is_saved_in_the_settings_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings { ShowLog = true }.Save(path);
            Assert.True(AppSettings.Load(path).ShowLog);
        }
        finally { File.Delete(path); }
    }
}
