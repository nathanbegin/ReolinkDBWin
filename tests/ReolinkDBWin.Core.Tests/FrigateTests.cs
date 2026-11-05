using System.Net;
using ReolinkDBWin.Core.Frigate;
using ReolinkDBWin.Core.Models;

namespace ReolinkDBWin.Core.Tests;

public class FrigateTests
{
    [Fact]
    public void Parses_events_from_various_versions()
    {
        var list = FrigateEvent.ParseList("""
            [
              {"id":"1700000000.123-abc","camera":"porte","label":"person","sub_label":["Nathan",0.91],
               "start_time":1700000000.123,"end_time":1700000012.5,"has_clip":true,"has_snapshot":true,
               "zones":["entree"],"data":{"top_score":0.87,"score":0.8}},
              {"id":"2","camera":"porte","label":"car","sub_label":null,"start_time":1700000100,"end_time":null,
               "has_clip":false,"has_snapshot":true,"top_score":0.7,"zones":[]},
              {"id":"3","camera":"porte","label":"package","sub_label":"Amazon","start_time":1700000200}
            ]
            """);

        Assert.Equal(3, list.Count);
        Assert.Equal("Nathan", list[0].SubLabel);
        Assert.Equal(0.87, list[0].Score);
        Assert.Equal(12.377, list[0].Duration!.Value.TotalSeconds, 2);
        Assert.Equal(["entree"], list[0].Zones);
        Assert.True(list[1].InProgress);
        Assert.Equal(0.7, list[1].Score);
        Assert.Equal(DetectionType.Vehicle, list[1].Type);
        Assert.Equal("Amazon", list[2].SubLabel);
        Assert.Equal(DetectionType.Package, list[2].Type);
    }

    [Fact]
    public void Builds_event_query()
    {
        var q = FrigateClient.BuildEventQuery(new FrigateEventQuery
        {
            Cameras = ["porte"],
            Labels = ["person", "car"],
            After = DateTimeOffset.FromUnixTimeSeconds(1700000000),
            Before = DateTimeOffset.FromUnixTimeMilliseconds(1700000100500),
            Limit = 20,
            HasClip = true,
        });
        Assert.Equal("cameras=porte&labels=person%2Ccar&after=1700000000&before=1700000100.5&has_clip=1&limit=20&include_thumbnails=0", q);
    }

    [Fact]
    public async Task Logs_in_when_credentials_given_and_retries_on_401()
    {
        var logins = 0;
        var versionCalls = 0;
        var handler = new FakeHandler((req, body) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/login")
            {
                logins++;
                Assert.Contains("\"user\":\"admin\"", body);
                return FakeHandler.Json("{}");
            }
            if (path == "/api/version")
            {
                versionCalls++;
                return versionCalls == 1
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("0.16.1-abc") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new FrigateClient(new FrigateConnectionInfo { BaseUrl = "https://frigate.local:8971", Username = "admin", Password = "x" }, handler);

        var version = await client.GetVersionAsync();

        Assert.Equal("0.16.1-abc", version);
        Assert.Equal(2, logins);
    }

    [Fact]
    public async Task Unauthorized_without_credentials_gives_helpful_error()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new FrigateClient(new FrigateConnectionInfo { BaseUrl = "frigate.local:8971" }, handler);
        var ex = await Assert.ThrowsAsync<FrigateException>(() => client.GetVersionAsync());
        Assert.Contains("authentification", ex.Message);
    }

    [Fact]
    public async Task Lists_cameras_from_config()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json("""{"cameras":{"sonnette":{},"cour":{}}}"""));
        using var client = new FrigateClient(new FrigateConnectionInfo { BaseUrl = "http://f:5000" }, handler);
        Assert.Equal(["cour", "sonnette"], await client.GetCamerasAsync());
        Assert.Equal("http://f:5000/api/events/abc/clip.mp4", client.GetClipUri("abc").ToString());
    }
}
