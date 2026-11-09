using ReolinkDBWin.Core.Settings;

namespace ReolinkDBWin.Core.Tests;

public class QualityTests
{
    [Fact]
    public void Small_display_uses_light_stream_and_small_decode()
    {
        Assert.Equal(new StreamQuality(true, 640), StreamQuality.Plan(480, 1, hasLight: true, adaptive: true));
        Assert.Equal(new StreamQuality(false, 640), StreamQuality.Plan(480, 1, hasLight: false, adaptive: true));
        Assert.Equal(new StreamQuality(true, 960), StreamQuality.Plan(760, 1, hasLight: true, adaptive: true));
    }

    [Fact]
    public void Large_or_zoomed_display_uses_main_stream()
    {
        Assert.Equal(new StreamQuality(false, 1920), StreamQuality.Plan(1700, 1, true, true));
        Assert.Equal(new StreamQuality(false, 1920), StreamQuality.Plan(480, 3, true, true)); // zoom
        Assert.Equal(StreamQuality.Full, StreamQuality.Plan(300, 1, true, adaptive: false));
    }

    [Fact]
    public void Replacement_only_when_it_matters()
    {
        var light640 = new StreamQuality(true, 640);
        var main640 = new StreamQuality(false, 640);
        var main1920 = new StreamQuality(false, 1920);
        Assert.True(main1920.ShouldReplace(light640));             // agrandissement / zoom : flux principal
        Assert.True(main1920.ShouldReplace(main640));              // même flux, plus grand
        Assert.False(main640.ShouldReplace(main1920));             // même flux, plus petit : inutile
        Assert.False(new StreamQuality(false, 960).ShouldReplace(new StreamQuality(false, 960)));
        Assert.True(light640.ShouldReplace(main1920));             // retour au sous-flux…
        Assert.True(light640.IsDowngradeFrom(main1920));           // …mais différé par l'appelant
        Assert.False(main1920.IsDowngradeFrom(light640));
    }

    [Fact]
    public void Light_urls()
    {
        var s = new AppSettings().Normalize();
        s.Reolink.Host = "192.168.1.50";
        s.Reolink.Password = "x";
        var doorbell = s.MultiView.Cameras[0];
        Assert.EndsWith("/h264Preview_01_sub", StreamUrlBuilder.BuildLight(s, doorbell));
        Assert.EndsWith("/h264Preview_01_main", StreamUrlBuilder.Build(s, doorbell)); // inchangé

        s.Stream.Source = StreamSource.ReolinkSub;
        Assert.Null(StreamUrlBuilder.BuildLight(s, doorbell));

        s.Stream.Source = StreamSource.FrigateRestream;
        s.Frigate.BaseUrl = "http://10.0.0.5:5000";
        s.Frigate.Camera = "porte";
        Assert.Null(StreamUrlBuilder.BuildLight(s, doorbell));
        s.Stream.FrigateLightStreamName = "porte_sub";
        Assert.Equal("rtsp://10.0.0.5:8554/porte_sub", StreamUrlBuilder.BuildLight(s, doorbell));

        var cour = new CameraSettings { Source = CameraSourceKind.FrigateRestream, StreamName = "cour", LightAddress = "cour_sub" };
        Assert.Equal("rtsp://10.0.0.5:8554/cour_sub", StreamUrlBuilder.BuildLight(s, cour));
    }
}
