using System.Net;
using ReolinkDBWin.Core.Discovery;
using ReolinkDBWin.Core.HomeAssistant;
using ReolinkDBWin.Core.Models;
using ReolinkDBWin.Core.Settings;

namespace ReolinkDBWin.Core.Tests;

public class MiscTests
{
    [Fact]
    public void Parses_onvif_probe_match()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://www.w3.org/2003/05/soap-envelope" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
              <SOAP-ENV:Body><d:ProbeMatches><d:ProbeMatch>
                <d:Scopes>onvif://www.onvif.org/type/video_encoder onvif://www.onvif.org/name/Reolink_Doorbell onvif://www.onvif.org/hardware/Video%20Doorbell%20WiFi</d:Scopes>
                <d:XAddrs>http://192.168.1.77:8000/onvif/device_service</d:XAddrs>
              </d:ProbeMatch></d:ProbeMatches></SOAP-ENV:Body>
            </SOAP-ENV:Envelope>
            """;
        var d = ReolinkDiscovery.ParseProbeMatch(xml, IPAddress.Parse("192.168.1.77"));
        Assert.NotNull(d);
        Assert.Equal("192.168.1.77", d.Host);
        Assert.Equal("Reolink Doorbell", d.Name);
        Assert.Equal("Video Doorbell WiFi", d.Model);
        Assert.True(d.IsReolink);
    }

    [Fact]
    public void Recognizes_reolink_api_signature()
    {
        Assert.True(ReolinkDiscovery.IsReolinkApiResponse("""[{"cmd":"GetDevInfo","code":1,"error":{"detail":"please login first","rspCode":-6}}]"""));
        Assert.False(ReolinkDiscovery.IsReolinkApiResponse("<html>router</html>"));
    }

    [Fact]
    public void Enumerates_at_most_a_slash_24()
    {
        var hosts = ReolinkDiscovery.EnumerateHosts(IPAddress.Parse("10.0.5.20"), IPAddress.Parse("255.255.0.0")).ToList();
        Assert.Equal(253, hosts.Count); // 254 hôtes moins le nôtre
        Assert.Equal("10.0.5.1", hosts[0].ToString());
        Assert.Equal("10.0.5.254", hosts[^1].ToString());
        Assert.DoesNotContain(hosts, h => h.ToString() == "10.0.5.20");
    }

    [Fact]
    public void Builds_stream_urls()
    {
        var s = new AppSettings();
        s.Reolink.Host = "192.168.1.50";
        s.Reolink.Password = "a b@c";
        Assert.Equal("rtsp://admin:a%20b%40c@192.168.1.50:554/h264Preview_01_main", StreamUrlBuilder.Build(s));

        s.Stream.Source = StreamSource.ReolinkSub;
        Assert.EndsWith("/h264Preview_01_sub", StreamUrlBuilder.Build(s));

        s.Stream.Source = StreamSource.FrigateRestream;
        s.Frigate.BaseUrl = "http://192.168.1.10:5000";
        s.Frigate.Camera = "sonnette";
        Assert.Equal("rtsp://192.168.1.10:8554/sonnette", StreamUrlBuilder.Build(s));

        Assert.Equal("rtsp://admin:•••@h:554/x", StreamUrlBuilder.Redact("rtsp://admin:secret@h:554/x"));
    }

    [Fact]
    public void Settings_round_trip_with_protected_secrets()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new SettingsStore(path, new ReverseProtector());
            var s = new AppSettings();
            s.Reolink.Host = "10.0.0.2";
            s.Reolink.Password = "secret";
            s.HomeAssistant.AccessToken = "tok";
            s.Stream.Source = StreamSource.FrigateRestream;
            store.Save(s);

            var raw = File.ReadAllText(path);
            Assert.DoesNotContain("\"secret\"", raw);
            Assert.Contains("terces", raw);
            Assert.Contains("FrigateRestream", raw);

            var loaded = store.Load();
            Assert.Equal("secret", loaded.Reolink.Password);
            Assert.Equal("tok", loaded.HomeAssistant.AccessToken);
            Assert.Equal(StreamSource.FrigateRestream, loaded.Stream.Source);

            var clone = SettingsStore.Clone(loaded);
            clone.Reolink.Host = "changed";
            Assert.Equal("10.0.0.2", loaded.Reolink.Host);
            Assert.Equal("secret", clone.Reolink.Password);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Notification_filter_applies_type_source_and_cooldown()
    {
        var f = new NotificationFilter();
        var s = new NotificationSettings { CooldownSeconds = 30 };
        var t0 = DateTimeOffset.Now;
        DetectionEvent Ev(DetectionType t, EventSource src = EventSource.Reolink) => new() { Source = src, Type = t };

        Assert.True(f.ShouldNotify(Ev(DetectionType.Person), s, t0));
        Assert.False(f.ShouldNotify(Ev(DetectionType.Person, EventSource.Frigate), s, t0.AddSeconds(5))); // source désactivée
        s.FromFrigate = true;
        Assert.False(f.ShouldNotify(Ev(DetectionType.Person, EventSource.Frigate), s, t0.AddSeconds(5))); // délai
        Assert.True(f.ShouldNotify(Ev(DetectionType.Person), s, t0.AddSeconds(31)));
        Assert.False(f.ShouldNotify(Ev(DetectionType.Motion), s, t0)); // type désactivé
        Assert.True(f.ShouldNotify(Ev(DetectionType.Doorbell), s, t0));
        Assert.True(f.ShouldNotify(Ev(DetectionType.Doorbell), s, t0.AddSeconds(11))); // sonnette : délai court
        s.Enabled = false;
        Assert.False(f.ShouldNotify(Ev(DetectionType.Package), s, t0));
    }

    [Fact]
    public void HomeAssistant_urls_and_entity_list()
    {
        Assert.Equal("ws://ha.local:8123/api/websocket", HomeAssistantClient.WebSocketUri("ha.local:8123").ToString());
        Assert.Equal("wss://ha.example.com/api/websocket", HomeAssistantClient.WebSocketUri("https://ha.example.com").ToString());

        var list = HomeAssistantClient.ParseEntities("""
            [{"entity_id":"light.salon","state":"on","attributes":{}},
             {"entity_id":"binary_sensor.porte_visitor","state":"off","attributes":{"friendly_name":"Porte Visiteur"}}]
            """);
        var e = Assert.Single(list);
        Assert.Equal("Porte Visiteur", e.FriendlyName);
    }

    private sealed class ReverseProtector : ISecretProtector
    {
        public string Protect(string plain) => new(plain.Reverse().ToArray());
        public string Unprotect(string protectedValue) => new(protectedValue.Reverse().ToArray());
    }
}
