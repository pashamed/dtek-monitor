using System.Net;
using System.Text.RegularExpressions;

namespace DtekBot.Tests;

public sealed class MiniAppStaticAssetTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public MiniAppStaticAssetTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task MiniAppDocuments_UseContentVersionedAssets_AndAreNotCached()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/lite");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var scriptVersion = Regex.Match(html, """app\.js\?v=([a-f0-9]{16})""").Groups[1].Value;
        var styleVersion = Regex.Match(html, """style\.css\?v=([a-f0-9]{16})""").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(scriptVersion));
        Assert.Equal(scriptVersion, styleVersion);
        Assert.DoesNotContain("__ASSET_VERSION__", html);

        var fallback = await client.GetStringAsync("/lite/channel/settings");
        Assert.Equal(html, fallback);

        var asset = await client.GetAsync($"/lite/app.js?v={scriptVersion}");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Contains("no-cache", asset.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task MiniAppDocument_CarriesTheReleasedAppVersion()
    {
        using var client = _factory.CreateClient();

        var html = await client.GetStringAsync("/lite");

        var appVersion = Regex.Match(html, """<meta name="app-version" content="([^"]*)">""").Groups[1].Value;
        Assert.Matches("""^\d+\.\d+\.\d+$""", appVersion);
        Assert.DoesNotContain("__APP_VERSION__", html);

        using var manifest = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), ".release-please-manifest.json")));
        Assert.Equal(manifest.RootElement.GetProperty(".").GetString(), appVersion);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".release-please-manifest.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
