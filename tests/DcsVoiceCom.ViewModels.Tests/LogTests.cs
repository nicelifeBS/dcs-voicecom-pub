using DcsVoiceCom.ViewModels;

namespace DcsVoiceCom.ViewModels.Tests;

public class LogTests
{
    [Fact]
    public void Formats_selected_lines_in_log_order_or_all()
    {
        var log = new LogViewModel();
        log.Add("INFO", "one");
        log.Add("ERROR", "two");
        log.Add("HEAR", "three");
        var e = log.Entries;

        Assert.Equal($"{e[0].TimeText}  INFO     one{Environment.NewLine}{e[2].TimeText}  HEAR     three",
            log.Format([e[2], e[0]]));
        Assert.Equal(3, log.Format().Split(Environment.NewLine).Length);
        Assert.Equal(log.Format(), log.Format([]));
    }
}
