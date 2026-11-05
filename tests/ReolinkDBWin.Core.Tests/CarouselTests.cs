using ReolinkDBWin.Core.Models;
using ReolinkDBWin.Core.Settings;

namespace ReolinkDBWin.Core.Tests;

public class CarouselTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static CarouselScheduler Make(params (string id, int dwell)[] items) =>
        new(items.Select(i => new CarouselItem(i.id, TimeSpan.FromSeconds(i.dwell), TransitionKind.Fade, TimeSpan.FromMilliseconds(500))))
        { MaxWait = TimeSpan.FromSeconds(10) };

    [Fact]
    public void Preloads_next_and_switches_only_when_ready()
    {
        var s = Make(("a", 10), ("b", 5), ("c", 8));
        s.Start(T0);
        Assert.Equal(["a", "b"], s.WantedIds);

        Assert.Null(s.Tick(T0.AddSeconds(9), _ => true)); // pas encore l'heure
        Assert.Null(s.Tick(T0.AddSeconds(10), _ => false)); // b pas prête : on attend
        Assert.True(s.IsWaitingForNext);
        var sw = s.Tick(T0.AddSeconds(11), _ => true);

        Assert.NotNull(sw);
        Assert.Equal(("a", "b"), (sw.FromId, sw.ToId));
        Assert.Equal(["b", "c"], s.WantedIds);
        Assert.Equal(T0.AddSeconds(16), s.SwitchDueAt); // durée propre à b
    }

    [Fact]
    public void Skips_camera_that_never_becomes_ready()
    {
        var s = Make(("a", 5), ("b", 5), ("c", 5));
        s.Start(T0);
        Func<string, bool> ready = id => id != "b";
        Assert.Null(s.Tick(T0.AddSeconds(5), ready));
        Assert.Null(s.Tick(T0.AddSeconds(15), ready)); // b sautée
        Assert.Equal(["a", "c"], s.WantedIds);
        var sw = s.Tick(T0.AddSeconds(15.25), ready);
        Assert.Equal("c", sw!.ToId);
        Assert.Equal(["c", "a"], s.WantedIds); // b sera retentée au prochain tour
    }

    [Fact]
    public void Priority_switches_when_ready_and_holds()
    {
        var s = Make(("a", 10), ("b", 10), ("c", 10));
        s.Start(T0);
        Assert.True(s.Prioritize("c", T0.AddSeconds(2), TimeSpan.FromSeconds(30)));
        Assert.Contains("c", s.WantedIds);
        Assert.Null(s.Tick(T0.AddSeconds(3), id => id != "c")); // on reste sur a en attendant c
        Assert.Null(s.Tick(T0.AddSeconds(10.5), id => id != "c")); // pas de défilement vers b pendant l'attente
        var sw = s.Tick(T0.AddSeconds(11), _ => true);
        Assert.True(sw!.IsPriority);
        Assert.Equal("c", sw.ToId);
        Assert.Equal(T0.AddSeconds(41), s.SwitchDueAt);
    }

    [Fact]
    public void Priority_is_abandoned_after_max_wait()
    {
        var s = Make(("a", 10), ("b", 10), ("c", 10));
        s.Start(T0);
        s.Prioritize("c", T0, TimeSpan.FromSeconds(30));
        Assert.Null(s.Tick(T0.AddSeconds(10), id => id != "c"));
        Assert.Null(s.PendingPriorityId);
        Assert.Equal("b", s.Tick(T0.AddSeconds(10.25), _ => true)!.ToId); // le défilement reprend
    }

    [Fact]
    public void Priority_on_current_camera_extends_display()
    {
        var s = Make(("a", 10), ("b", 10));
        s.Start(T0);
        s.Prioritize("a", T0.AddSeconds(5), TimeSpan.FromSeconds(30));
        Assert.Null(s.Tick(T0.AddSeconds(5), _ => true));
        Assert.Equal(T0.AddSeconds(35), s.SwitchDueAt);
    }

    [Fact]
    public void Paused_and_single_mode_do_not_advance()
    {
        var s = Make(("a", 5), ("b", 5));
        s.Start(T0);
        s.Paused = true;
        Assert.Null(s.Tick(T0.AddSeconds(60), _ => true));

        var single = new CarouselScheduler([new CarouselItem("a", TimeSpan.FromSeconds(5), TransitionKind.Cut, TimeSpan.Zero),
                                            new CarouselItem("b", TimeSpan.FromSeconds(5), TransitionKind.Cut, TimeSpan.Zero)])
        { AutoAdvance = false };
        single.Start(T0);
        Assert.Equal(["a"], single.WantedIds);
        Assert.Null(single.Tick(T0.AddSeconds(60), _ => true));
        Assert.Equal("b", single.Step(+1, T0)!.ToId);
        Assert.Equal("a", single.Step(-1, T0)!.ToId);
    }

    [Fact]
    public void Matches_detection_to_camera()
    {
        var s = new AppSettings().Normalize();
        s.Frigate.Camera = "porte";
        s.MultiView.Cameras.Add(new CameraSettings { Id = "cour", Name = "Cour", Source = CameraSourceKind.FrigateRestream, StreamName = "cour" });
        s.MultiView.Mode = LiveViewMode.Carousel;

        Assert.Equal(CameraSettings.DoorbellId, CameraMatcher.Find(s, new DetectionEvent { Source = EventSource.Reolink, Type = DetectionType.Doorbell })!.Id);
        Assert.Equal(CameraSettings.DoorbellId, CameraMatcher.Find(s, new DetectionEvent { Source = EventSource.Frigate, Type = DetectionType.Person, Camera = "porte" })!.Id);
        Assert.Equal("cour", CameraMatcher.Find(s, new DetectionEvent { Source = EventSource.Frigate, Type = DetectionType.Person, Camera = "COUR" })!.Id);
        Assert.Null(CameraMatcher.Find(s, new DetectionEvent { Source = EventSource.Frigate, Type = DetectionType.Person, Camera = "garage" }));

        Assert.Equal(["cour", "porte"], CameraMatcher.FrigateCamerasToWatch(s));
        Assert.Equal(["porte"], CameraMatcher.FrigateCamerasToNotify(s));
        Assert.Equal("rtsp://192.168.1.10:8554/cour", StreamUrlBuilder.Build(Configure(s), s.MultiView.Cameras[1]));
    }

    [Fact]
    public void Normalize_adds_doorbell_once()
    {
        var s = new AppSettings().Normalize().Normalize();
        Assert.Single(s.MultiView.Cameras);
        Assert.Equal(CameraSourceKind.Doorbell, s.MultiView.Cameras[0].Source);
    }

    private static AppSettings Configure(AppSettings s)
    {
        s.Frigate.BaseUrl = "http://192.168.1.10:5000";
        return s;
    }
}
