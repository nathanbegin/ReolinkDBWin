using System.Text.Json.Nodes;
using ReolinkDBWin.Core.Reolink;

namespace ReolinkDBWin.Core.Tests;

public class ReolinkClientTests
{
    private static readonly ReolinkConnectionInfo Info = new() { Host = "192.168.1.50", Username = "admin", Password = "p@ss" };

    private const string LoginOk = """[{"cmd":"Login","code":0,"value":{"Token":{"leaseTime":3600,"name":"TOKEN1"}}}]""";

    [Fact]
    public async Task Login_sends_credentials_and_stores_token()
    {
        var handler = new FakeHandler((req, body) => FakeHandler.Json(LoginOk));
        using var client = new ReolinkClient(Info, handler);

        await client.LoginAsync();

        Assert.True(client.HasValidToken);
        var (request, sent) = handler.Requests.Single();
        Assert.Equal("http://192.168.1.50/cgi-bin/api.cgi?cmd=Login", request.RequestUri!.ToString());
        var user = JsonNode.Parse(sent)![0]!["param"]!["User"]!;
        Assert.Equal("admin", (string?)user["userName"]);
        Assert.Equal("p@ss", (string?)user["password"]);
    }

    [Fact]
    public async Task Login_failure_throws_api_exception()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(
            """[{"cmd":"Login","code":1,"error":{"detail":"login failed","rspCode":-7}}]"""));
        using var client = new ReolinkClient(Info, handler);

        var ex = await Assert.ThrowsAsync<ReolinkApiException>(() => client.LoginAsync());
        Assert.Equal(-7, ex.RspCode);
        Assert.True(ex.IsAuthenticationError);
    }

    [Fact]
    public async Task Expired_session_triggers_single_relogin()
    {
        var logins = 0;
        var calls = 0;
        var handler = new FakeHandler((req, _) =>
        {
            var q = req.RequestUri!.Query;
            if (q.Contains("cmd=Login"))
            {
                logins++;
                return FakeHandler.Json(LoginOk.Replace("TOKEN1", "TOKEN" + logins));
            }
            calls++;
            if (q.Contains("token=TOKEN1"))
                return FakeHandler.Json("""[{"cmd":"GetDevInfo","code":1,"error":{"detail":"please login first","rspCode":-6}}]""");
            return FakeHandler.Json("""[{"cmd":"GetDevInfo","code":0,"value":{"DevInfo":{"model":"Reolink Video Doorbell WiFi","name":"Porte","firmVer":"v3.0.0.1","channelNum":1}}}]""");
        });
        using var client = new ReolinkClient(Info, handler);

        var info = await client.GetDeviceInfoAsync();

        Assert.Equal(2, logins);
        Assert.Equal(2, calls);
        Assert.Equal("Porte", info.Name);
        Assert.True(info.IsDoorbell);
    }

    [Fact]
    public void Parses_GetMdState()
    {
        var states = ReolinkClient.ParseDetectionStates(JsonNode.Parse("""{"state":1}""")!);
        Assert.True(states["motion"]);
    }

    [Fact]
    public void Parses_GetAiState_and_ignores_unsupported()
    {
        var states = ReolinkClient.ParseDetectionStates(JsonNode.Parse("""
            {"channel":0,"dog_cat":{"alarm_state":0,"support":1},"face":{"alarm_state":0,"support":0},
             "people":{"alarm_state":1,"support":1},"vehicle":{"alarm_state":0,"support":1}}
            """)!);
        Assert.True(states["person"]);
        Assert.False(states["animal"]);
        Assert.False(states["vehicle"]);
        Assert.False(states.ContainsKey("face"));
    }

    [Fact]
    public void Parses_GetEvents_with_visitor()
    {
        var states = ReolinkClient.ParseDetectionStates(JsonNode.Parse("""
            {"channel":0,"ai":{"people":{"alarm_state":0,"support":1},"package":{"alarm_state":1,"support":1}},
             "md":{"alarm_state":1,"support":1},"visitor":{"alarm_state":1,"support":1}}
            """)!);
        Assert.True(states["visitor"]);
        Assert.True(states["package"]);
        Assert.True(states["motion"]);
        Assert.False(states["person"]);
    }

    [Fact]
    public async Task GetDetectionStates_batches_md_and_ai()
    {
        var handler = new FakeHandler((req, body) =>
        {
            if (req.RequestUri!.Query.Contains("cmd=Login")) return FakeHandler.Json(LoginOk);
            return FakeHandler.Json("""
                [{"cmd":"GetMdState","code":0,"value":{"state":1}},
                 {"cmd":"GetAiState","code":0,"value":{"people":{"alarm_state":1,"support":1}}}]
                """);
        });
        using var client = new ReolinkClient(Info, handler);

        var states = await client.GetDetectionStatesAsync(0, tryGetEvents: false);

        Assert.True(states["motion"]);
        Assert.True(states["person"]);
        var batch = JsonNode.Parse(handler.Requests.Last().Body)!.AsArray();
        Assert.Equal(2, batch.Count);
    }

    [Fact]
    public async Task GetEvents_unsupported_throws()
    {
        var handler = new FakeHandler((req, _) =>
            req.RequestUri!.Query.Contains("cmd=Login")
                ? FakeHandler.Json(LoginOk)
                : FakeHandler.Json("""[{"cmd":"GetEvents","code":1,"error":{"detail":"not support","rspCode":-9}}]"""));
        using var client = new ReolinkClient(Info, handler);

        var ex = await Assert.ThrowsAsync<ReolinkApiException>(() => client.GetDetectionStatesAsync(0, tryGetEvents: true));
        Assert.True(ex.IsNotSupported);
    }

    [Fact]
    public void Https_and_custom_port_build_api_uri()
    {
        var info = Info with { UseHttps = true, Port = 8443 };
        Assert.Equal("https://192.168.1.50:8443/cgi-bin/api.cgi", info.ApiUri.ToString());
        Assert.Equal(443, (Info with { UseHttps = true }).EffectivePort);
    }
}
