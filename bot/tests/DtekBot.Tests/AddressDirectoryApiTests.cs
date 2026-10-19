using System.Net;
using System.Net.Http.Json;
using DtekBot.Api.Groups;

namespace DtekBot.Tests;

public sealed class AddressDirectoryApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public AddressDirectoryApiTests()
    {
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
    }

    [Fact]
    public async Task ValidDirectoryIsImportedForConfiguredDtekRegion()
    {
        var response = await _client.PostAsJsonAsync("/ingest/address-directory", new
        {
            regionId = "kyiv-oblast",
            streets = new Dictionary<string, string[]>
            {
                ["м. Ірпінь"] = ["вул. Західна", "вул. Центральна"],
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new[] { "вул. Західна", "вул. Центральна" },
            _factory.DtekAddresses.ImportedDirectory!["м. Ірпінь"]);
    }

    [Fact]
    public async Task EmptyDirectoryIsRejectedWithoutReplacingCachedData()
    {
        var response = await _client.PostAsJsonAsync("/ingest/address-directory", new
        {
            regionId = "kyiv-oblast",
            streets = new Dictionary<string, string[]>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_factory.DtekAddresses.ImportedDirectory);
    }

    [Fact]
    public async Task BlankStreetEntriesAreIgnoredWhenImportingDirectory()
    {
        var response = await _client.PostAsJsonAsync("/ingest/address-directory", new
        {
            regionId = "kyiv-oblast",
            streets = new Dictionary<string, string[]>
            {
                ["м. Ірпінь"] = ["вул. Західна", "", "   "],
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new[] { "вул. Західна" },
            _factory.DtekAddresses.ImportedDirectory!["м. Ірпінь"]);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
