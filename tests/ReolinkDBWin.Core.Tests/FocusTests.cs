using ReolinkDBWin.Core.Events;
using ReolinkDBWin.Core.Frigate;
using ReolinkDBWin.Core.Settings;

namespace ReolinkDBWin.Core.Tests;

public class FocusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Detection_is_held_then_expires()
    {
        var f = new DetectionFocus { Hold = TimeSpan.FromSeconds(30) };
        f.Detected("porte", T0);
        f.Detected("cour", T0.AddSeconds(10));
        Assert.Equal(["porte", "cour"], f.Cameras);

        Assert.True(f.Update(T0.AddSeconds(31)));
        Assert.Equal(["cour"], f.Cameras);
        f.Update(T0.AddSeconds(41));
        Assert.Empty(f.Cameras);
    }

    [Fact]
    public void Active_frigate_event_keeps_camera_until_it_ends()
    {
        var f = new DetectionFocus { Hold = TimeSpan.FromSeconds(10), Linger = TimeSpan.FromSeconds(5) };
        f.SetActive(["cour"], T0);
        f.Update(T0.AddMinutes(2));
        Assert.Equal(["cour"], f.Cameras); // toujours en cours

        f.SetActive([], T0.AddMinutes(2));
        f.Update(T0.AddMinutes(2).AddSeconds(4));
        Assert.Equal(["cour"], f.Cameras); // court maintien
        f.Update(T0.AddMinutes(2).AddSeconds(6));
        Assert.Empty(f.Cameras);
    }

    [Fact]
    public void Frigate_active_cameras_are_in_progress_events()
    {
        var now = DateTimeOffset.Now;
        var active = FrigateEventMonitor.ActiveCameras([
            new FrigateEvent { Id = "1", Camera = "porte", Label = "person", StartTime = now.AddSeconds(-5) },
            new FrigateEvent { Id = "2", Camera = "cour", Label = "car", StartTime = now.AddSeconds(-60), EndTime = now.AddSeconds(-30) },
            new FrigateEvent { Id = "3", Camera = "garage", Label = "person", StartTime = now.AddHours(-2) },
        ]);
        Assert.Equal(["porte"], active);
    }
}
