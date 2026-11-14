using System.Net;
using System.Text;
using ReolinkDBWin.Core.Cameras;
using ReolinkDBWin.Core.Discovery;
using ReolinkDBWin.Core.Settings;

namespace ReolinkDBWin.Core.Tests;

public class CameraTests
{
    [Theory]
    [InlineData("reolink", false, 1, "rtsp://admin:p%40ss@10.0.0.9:554/h264Preview_01_main")]
    [InlineData("reolink", true, 2, "rtsp://admin:p%40ss@10.0.0.9:554/h264Preview_02_sub")]
    [InlineData("hikvision", false, 1, "rtsp://admin:p%40ss@10.0.0.9:554/Streaming/Channels/101")]
    [InlineData("hikvision", true, 3, "rtsp://admin:p%40ss@10.0.0.9:554/Streaming/Channels/302")]
    [InlineData("dahua", true, 1, "rtsp://admin:p%40ss@10.0.0.9:554/cam/realmonitor?channel=1&subtype=1")]
    [InlineData("tapo", false, 1, "rtsp://admin:p%40ss@10.0.0.9:554/stream1")]
    [InlineData("foscam", false, 1, "rtsp://admin:p%40ss@10.0.0.9:88/videoMain")]
    public void Brand_templates(string brand, bool sub, int channel, string expected) =>
        Assert.Equal(expected, CameraBrands.Find(brand)!.BuildUrl(sub, "10.0.0.9", "admin", "p@ss", channel));

    [Fact]
    public void Brand_without_credentials_or_sub_stream()
    {
        Assert.Equal("rtsp://10.0.0.9:8554/stream1", CameraBrands.Find("tapo")!.BuildUrl(false, "10.0.0.9", "", "", 1, 8554));
        Assert.Equal("rtsp://u:p@h:554/live0", CameraBrands.Find("eufy")!.BuildUrl(sub: true, "h", "u", "p")); // pas de sous-flux : principal
        Assert.All(CameraBrands.All.Where(b => b.Id != CameraBrands.OnvifId), b => Assert.StartsWith("rtsp://", b.Main));
    }

    [Fact]
    public void Onvif_security_header_digest_is_standard()
    {
        // Exemple de la spécification WS-UsernameToken : SHA1(nonce + created + password)
        var nonce = Convert.FromBase64String("LKqI6G/AikKCQrN0zqZFlg==");
        var header = OnvifClient.BuildSecurityHeader("admin", "userpassword", new DateTime(2010, 9, 16, 7, 50, 45, DateTimeKind.Utc), nonce);
        Assert.Contains("<wsse:Username>admin</wsse:Username>", header);
        Assert.Contains("2010-09-16T07:50:45.000Z", header);
        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(
            [.. nonce, .. Encoding.UTF8.GetBytes("2010-09-16T07:50:45.000Z"), .. Encoding.UTF8.GetBytes("userpassword")]));
        Assert.Contains(expected, header);
    }

    [Fact]
    public async Task Onvif_profiles_and_stream_uris()
    {
        var handler = new FakeHandler((req, body) =>
        {
            if (body.Contains("GetCapabilities"))
                return Xml("""<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body><tds:GetCapabilitiesResponse xmlns:tds="d" xmlns:tt="t"><tds:Capabilities><tt:Media><tt:XAddr>http://192.168.99.1:8000/onvif/media_service</tt:XAddr></tt:Media></tds:Capabilities></tds:GetCapabilitiesResponse></s:Body></s:Envelope>""");
            if (body.Contains("GetProfiles"))
                return Xml("""
                    <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:trt="m" xmlns:tt="t"><s:Body><trt:GetProfilesResponse>
                      <trt:Profiles token="sub"><tt:Name>Sub</tt:Name><tt:VideoEncoderConfiguration><tt:Encoding>H264</tt:Encoding><tt:Resolution><tt:Width>640</tt:Width><tt:Height>360</tt:Height></tt:Resolution></tt:VideoEncoderConfiguration></trt:Profiles>
                      <trt:Profiles token="main"><tt:Name>Main</tt:Name><tt:VideoEncoderConfiguration><tt:Encoding>H265</tt:Encoding><tt:Resolution><tt:Width>2560</tt:Width><tt:Height>1440</tt:Height></tt:Resolution></tt:VideoEncoderConfiguration></trt:Profiles>
                    </trt:GetProfilesResponse></s:Body></s:Envelope>
                    """);
            Assert.Equal("10.0.0.9", req.RequestUri!.Host); // l'hôte joignable est conservé, pas celui annoncé
            var token = body.Contains(">main<") ? "main" : "sub";
            return Xml($"""<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:trt="m" xmlns:tt="t"><s:Body><trt:GetStreamUriResponse><trt:MediaUri><tt:Uri>rtsp://10.0.0.9:554/{token}</tt:Uri></trt:MediaUri></trt:GetStreamUriResponse></s:Body></s:Envelope>""");
        });
        using var client = new OnvifClient("10.0.0.9", 8000, "admin", "p@ss", handler);

        var profiles = await client.GetProfilesAsync();

        Assert.Equal(["main", "sub"], profiles.Select(p => p.Token)); // du plus grand au plus petit
        Assert.Equal("rtsp://10.0.0.9:554/main", profiles[0].StreamUri);
        Assert.Contains("UsernameToken", handler.Requests[0].Body);
        Assert.Equal("rtsp://admin:p%40ss@10.0.0.9:554/main", OnvifClient.WithCredentials(profiles[0].StreamUri, "admin", "p@ss"));
        Assert.Equal("rtsp://a:b@h/x", OnvifClient.WithCredentials("rtsp://old:creds@h/x", "a", "b"));
    }

    [Fact]
    public async Task Onvif_fault_gives_readable_error()
    {
        var handler = new FakeHandler((_, _) => Xml("""<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body><s:Fault><s:Reason><s:Text>Sender not Authorized</s:Text></s:Reason></s:Fault></s:Body></s:Envelope>""", HttpStatusCode.BadRequest));
        using var client = new OnvifClient("h", 80, "u", "bad", handler);
        var ex = await Assert.ThrowsAsync<OnvifException>(() => client.GetProfilesAsync());
        Assert.Contains("identifiants", ex.Message);
    }

    [Fact]
    public void Probe_match_keeps_onvif_service_url()
    {
        const string xml = """<e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"><e:Body><d:ProbeMatches><d:ProbeMatch><d:Scopes>onvif://www.onvif.org/name/Tapo_C200</d:Scopes><d:XAddrs>http://192.168.1.40:2020/onvif/device_service</d:XAddrs></d:ProbeMatch></d:ProbeMatches></e:Body></e:Envelope>""";
        var d = ReolinkDiscovery.ParseProbeMatch(xml, IPAddress.Parse("192.168.1.40"))!;
        Assert.Equal("http://192.168.1.40:2020/onvif/device_service", d.OnvifServiceUrl);
        Assert.False(d.IsReolink);
    }

    [Fact]
    public void Camera_urls_with_passwords_are_encrypted_on_disk_and_legacy_plain_still_loads()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new SettingsStore(path, new Base64Protector());
            var s = new AppSettings().Normalize();
            s.MultiView.Cameras.Add(new CameraSettings { Id = "c1", Source = CameraSourceKind.Url, Url = "rtsp://admin:secret@10.0.0.9/main", LightAddress = "rtsp://admin:secret@10.0.0.9/sub" });
            s.MultiView.Cameras.Add(new CameraSettings { Id = "c2", Source = CameraSourceKind.Url, Url = "rtsp://10.0.0.8/public" });
            store.Save(s);

            var raw = File.ReadAllText(path);
            Assert.DoesNotContain("secret", raw);
            Assert.Contains("rtsp://10.0.0.8/public", raw); // sans mot de passe : lisible
            Assert.Equal("rtsp://admin:secret@10.0.0.9/main", s.MultiView.Cameras[1].Url); // l'objet en mémoire est intact

            var loaded = store.Load();
            Assert.Equal("rtsp://admin:secret@10.0.0.9/main", loaded.MultiView.Cameras[1].Url);
            Assert.Equal("rtsp://admin:secret@10.0.0.9/sub", loaded.MultiView.Cameras[1].LightAddress);

            // Ancien fichier (adresse en clair) : toujours lu tel quel.
            File.WriteAllText(path, raw.Replace(loaded.MultiView.Cameras[2].Url, "rtsp://old:plain@10.0.0.7/x"));
            Assert.Equal("rtsp://old:plain@10.0.0.7/x", store.Load().MultiView.Cameras[2].Url);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Legacy_hardware_checkbox_migrates_to_decoder_choice_and_keep_all_open_wants_every_stream()
    {
        var s = new AppSettings();
        s.Stream.HardwareDecoding = true;
        s.Normalize();
        Assert.Equal(HardwareDecoder.D3D11, s.Stream.Decoder);
        Assert.False(s.Stream.HardwareDecoding);

        var items = new[] { "a", "b", "c", "d" }.Select(id => new CarouselItem(id, TimeSpan.FromSeconds(10), TransitionKind.Cut, TimeSpan.Zero));
        var all = new CarouselScheduler(items) { KeepAllOpen = true };
        all.Start(DateTimeOffset.Now);
        Assert.Equal(["a", "b", "c", "d"], all.WantedIds);
    }

    private static HttpResponseMessage Xml(string xml, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(xml, Encoding.UTF8, "application/soap+xml") };

    private sealed class Base64Protector : ISecretProtector
    {
        public string Protect(string plain) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        public string Unprotect(string protectedValue) => Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
    }
}
