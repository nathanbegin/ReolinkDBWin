using System.Net;
using System.Net.Sockets;
using System.Text;
using ReolinkDBWin.Core.Net;
using ReolinkDBWin.Core.Settings;

namespace ReolinkDBWin.Core.Tests;

public class RtspTests
{
    [Fact]
    public void Restream_uses_rtsp_host_override_and_credentials()
    {
        var s = new AppSettings().Normalize();
        s.Frigate.BaseUrl = "https://frigate.example.org:8971";
        s.Frigate.Camera = "porte";
        s.Stream.Source = StreamSource.FrigateRestream;
        Assert.Equal("rtsp://frigate.example.org:8554/porte", StreamUrlBuilder.Build(s));

        s.Stream.FrigateRtspHost = "192.168.1.10";
        Assert.Equal("rtsp://192.168.1.10:8554/porte", StreamUrlBuilder.Build(s));
        s.Stream.FrigateRtspHost = "http://192.168.1.10:5000";
        Assert.Equal("rtsp://192.168.1.10:8554/porte", StreamUrlBuilder.Build(s));
        s.Stream.FrigateRtspHost = "192.168.1.10:8554";
        Assert.Equal("rtsp://192.168.1.10:8554/porte", StreamUrlBuilder.Build(s));

        s.Stream.FrigateRtspUsername = "vigie";
        s.Stream.FrigateRtspPassword = "p@ss";
        Assert.Equal("rtsp://vigie:p%40ss@192.168.1.10:8554/porte", StreamUrlBuilder.Build(s));
        var cam = new CameraSettings { Source = CameraSourceKind.FrigateRestream, StreamName = "cour" };
        Assert.Equal("rtsp://vigie:p%40ss@192.168.1.10:8554/cour", StreamUrlBuilder.Build(s, cam));
    }

    [Theory]
    [InlineData("RTSP/1.0 200 OK\r\nCSeq: 2\r\n", RtspProbeResult.Ok)]
    [InlineData("RTSP/1.0 404 Not Found\r\n", RtspProbeResult.StreamNotFound)]
    [InlineData("RTSP/1.0 401 Unauthorized\r\n", RtspProbeResult.Unauthorized)]
    [InlineData("HTTP/1.1 400 Bad Request\r\n", RtspProbeResult.NotRtsp)]
    public void Interprets_rtsp_responses(string response, RtspProbeResult expected) =>
        Assert.Equal(expected, RtspProbe.Interpret(response).Result);

    [Fact]
    public async Task Probes_a_local_server()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            var buf = new byte[1024];
            var n = await s.ReadAsync(buf);
            var req = Encoding.ASCII.GetString(buf, 0, n);
            var reply = req.Contains("/porte ") ? "RTSP/1.0 200 OK\r\nCSeq: 2\r\n\r\n" : "RTSP/1.0 404 Not Found\r\nCSeq: 2\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(reply));
        });

        var report = await RtspProbe.ProbeAsync($"rtsp://127.0.0.1:{port}/porte", TimeSpan.FromSeconds(3));
        await server;
        listener.Stop();
        Assert.Equal(RtspProbeResult.Ok, report.Result);
    }

    [Fact]
    public async Task Closed_port_is_unreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var report = await RtspProbe.ProbeAsync($"rtsp://127.0.0.1:{port}/x", TimeSpan.FromSeconds(2));
        Assert.Equal(RtspProbeResult.Unreachable, report.Result);
    }
}
