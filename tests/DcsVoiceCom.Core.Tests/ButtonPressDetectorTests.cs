using DcsVoiceCom.Input;

namespace DcsVoiceCom.Tests;

public class ButtonPressDetectorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1);

    private static bool[] Down(int count, params int[] pressed)
    {
        var buttons = new bool[count];
        foreach (var b in pressed) buttons[b] = true;
        return buttons;
    }

    [Fact]
    public void Ignores_a_switch_held_from_the_start()
    {
        var detector = new ButtonPressDetector(TimeSpan.FromMilliseconds(300));
        Assert.Null(detector.Update(0, Down(32, 20), T0));
        for (var ms = 10; ms < 2000; ms += 10) Assert.Null(detector.Update(0, Down(32, 20), T0.AddMilliseconds(ms)));

        Assert.Equal(3, detector.Update(0, Down(32, 3, 20), T0.AddSeconds(2)));
    }

    [Fact]
    public void Ignores_presses_while_settling()
    {
        var detector = new ButtonPressDetector(TimeSpan.FromMilliseconds(300));
        detector.Update(0, Down(32), T0);
        // A switch that reports late (first polls read all-up) is taken as held, not pressed.
        Assert.Null(detector.Update(0, Down(32, 20), T0.AddMilliseconds(50)));
        Assert.Null(detector.Update(0, Down(32, 20), T0.AddMilliseconds(400)));
        Assert.Equal(5, detector.Update(0, Down(32, 5, 20), T0.AddMilliseconds(500)));
    }

    [Fact]
    public void A_held_button_counts_after_release_and_press()
    {
        var detector = new ButtonPressDetector(TimeSpan.Zero);
        detector.Update(0, Down(8, 1), T0);
        Assert.Null(detector.Update(0, Down(8), T0.AddMilliseconds(10)));
        Assert.Equal(1, detector.Update(0, Down(8, 1), T0.AddMilliseconds(20)));
    }

    [Fact]
    public void Tracks_controllers_apart()
    {
        var detector = new ButtonPressDetector(TimeSpan.Zero);
        detector.Update(0, Down(8, 2), T0);
        detector.Update(1, Down(8), T0);
        Assert.Null(detector.Update(0, Down(8, 2), T0.AddMilliseconds(10)));
        Assert.Equal(2, detector.Update(1, Down(8, 2), T0.AddMilliseconds(10)));
    }
}
