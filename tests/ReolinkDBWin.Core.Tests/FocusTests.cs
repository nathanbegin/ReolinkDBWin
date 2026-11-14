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
    public void Active_event_prolongs_but_never_beyond_max_duration()
    {
        var f = new DetectionFocus
        {
            Hold = TimeSpan.FromSeconds(10), Linger = TimeSpan.FromSeconds(5),
            MaxDuration = TimeSpan.FromSeconds(60), Cooldown = TimeSpan.FromSeconds(120),
        };
        f.SetActive(["cour"], T0);
        Assert.Empty(f.Cameras); // un événement en cours seul n'ouvre pas d'alerte

        Assert.True(f.Detected("cour", T0));
        f.SetActive(["cour"], T0);
        f.Update(T0.AddSeconds(40));
        Assert.Equal(["cour"], f.Cameras); // prolongée : l'événement dure

        // Objet immobile / rue passante : après la durée maximale, le défilement reprend…
        Assert.True(f.Update(T0.AddSeconds(61)));
        Assert.Empty(f.Cameras);
        // …et la caméra ne revient pas aussitôt, même avec de nouvelles détections.
        Assert.False(f.Detected("cour", T0.AddSeconds(70)));
        Assert.Empty(f.Cameras);
        f.Update(T0.AddSeconds(182));
        Assert.True(f.Detected("cour", T0.AddSeconds(182)));
    }

    [Fact]
    public void Event_end_releases_camera_after_linger()
    {
        var f = new DetectionFocus { Hold = TimeSpan.FromSeconds(10), Linger = TimeSpan.FromSeconds(5) };
        f.Detected("cour", T0);
        f.SetActive(["cour"], T0.AddSeconds(20));
        f.Update(T0.AddSeconds(20));
        f.SetActive([], T0.AddSeconds(22));
        f.Update(T0.AddSeconds(24));
        Assert.Equal(["cour"], f.Cameras);
        f.Update(T0.AddSeconds(26));
        Assert.Empty(f.Cameras);
        Assert.True(f.Detected("cour", T0.AddSeconds(27))); // fin normale : pas de sourdine
    }

    [Fact]
    public void User_can_dismiss_and_cameras_are_snoozed()
    {
        var f = new DetectionFocus { Cooldown = TimeSpan.FromSeconds(60) };
        f.Detected("porte", T0);
        f.Detected("cour", T0);
        Assert.True(f.Dismiss(T0.AddSeconds(5)));
        Assert.Empty(f.Cameras);
        Assert.False(f.Detected("porte", T0.AddSeconds(30)));
        Assert.True(f.Detected("garage", T0.AddSeconds(30)));   // une autre caméra reste suivie
        Assert.True(f.Detected("porte", T0.AddSeconds(66)));
        Assert.False(f.Dismiss(T0.AddSeconds(500)) && f.Cameras.Count > 0);
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
