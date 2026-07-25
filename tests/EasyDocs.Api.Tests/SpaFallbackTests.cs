using System.Net;

namespace EasyDocs.Api.Tests;

public class SpaFallbackTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public SpaFallbackTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task Unknown_client_route_serves_index_html()
    {
        var res = await _f.CreateClient().GetAsync("/some/spa/route");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("text/html", res.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task Unknown_api_route_is_404_not_index()
    {
        var res = await _f.CreateClient().GetAsync("/api/v1/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Health_still_ok()
        => (await _f.CreateClient().GetAsync("/health")).EnsureSuccessStatusCode();
}
