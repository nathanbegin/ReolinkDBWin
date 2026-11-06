using System.Net;
using System.Security.Cryptography;
using ReolinkDBWin.Core.Updates;

namespace ReolinkDBWin.Core.Tests;

public class UpdateTests
{
    private const string Release = """
        {"tag_name":"v1.6.0","name":"Vigie Widget 1.6.0","body":"- Correctif","html_url":"https://github.com/o/r/releases/tag/v1.6.0",
         "draft":false,"prerelease":false,"published_at":"2026-11-10T12:00:00Z",
         "assets":[{"name":"VigieWidget-1.6.0-win-x64.zip","browser_download_url":"https://x/zip","size":10},
                   {"name":"VigieWidget-Setup-1.6.0.exe","browser_download_url":"https://x/setup.exe","size":4,"digest":"sha256:DIGEST"}]}
        """;

    [Fact]
    public void Parses_release_and_picks_installer()
    {
        var info = UpdateChecker.Parse(Release)!;
        Assert.Equal(new Version(1, 6, 0), info.Version);
        Assert.Equal("https://x/setup.exe", info.InstallerUrl!.ToString());
        Assert.Equal("digest", info.InstallerSha256);
        Assert.Null(UpdateChecker.Parse(Release.Replace("\"prerelease\":false", "\"prerelease\":true")));
    }

    [Theory]
    [InlineData("v1.6.0", "1.5.0", true)]
    [InlineData("1.5.1", "1.5.0", true)]
    [InlineData("v1.5", "1.5.0", false)]
    [InlineData("1.5.0-beta", "1.5.0", false)]
    [InlineData("1.4.9", "1.5.0", false)]
    public void Compares_versions(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(UpdateChecker.ParseVersion(candidate)!, Version.Parse(current)));

    [Fact]
    public async Task Downloads_and_verifies_digest()
    {
        var payload = "MZ\u0090\u0000"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        using var checker = new UpdateChecker("https://api.github.com/repos/o/r/releases/latest", "test", handler);
        var info = UpdateChecker.Parse(Release.Replace("DIGEST", sha))!;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe");
        try
        {
            await checker.DownloadAsync(info, path);
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));

            var bad = info with { InstallerSha256 = new string('0', 64) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => checker.DownloadAsync(bad, path + "2"));
            Assert.False(File.Exists(path + "2"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Missing_release_returns_null()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var checker = new UpdateChecker("https://api.github.com/repos/o/r/releases/latest", "test", handler);
        Assert.Null(await checker.GetLatestAsync());
    }
}
