using System.Net;
using System.Text.Json;

namespace EasyDocs.Api.Tests;

public class OpenApiTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Openapi_json_is_served_and_lists_core_endpoints()
    {
        var res = await f.CreateClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("application/json", res.Content.Headers.ContentType!.ToString());

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.StartsWith("3.", root.GetProperty("openapi").GetString());

        var paths = root.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/documents", out _));
        Assert.True(paths.TryGetProperty("/api/v1/versions/{vid}/publish", out _));
        Assert.True(paths.TryGetProperty("/api/v1/tokens", out _));

        // The copies/push line of §10.1 — the last routes to land (M4), so the published document is now
        // the complete v1 endpoint set.
        foreach (var path in new[]
        {
            "/api/v1/versions/{vid}/copies",
            "/api/v1/documents/{id}/copies",
            "/api/v1/documents/{id}/pushes",
            "/api/v1/documents/{id}/push-requests",
            "/api/v1/push-requests/{id}:accept",
            "/api/v1/push-requests/{id}:reject",
        })
            Assert.True(paths.TryGetProperty(path, out _), $"missing {path} in the published document");

        var schemes = root.GetProperty("components").GetProperty("securitySchemes");
        var hasBearer = schemes.EnumerateObject()
            .Any(s => string.Equals(
                s.Value.TryGetProperty("scheme", out var sch) ? sch.GetString() : null,
                "bearer", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasBearer, "expected an http bearer security scheme");
    }

    [Fact]
    public async Task Docs_page_is_self_contained_html()
    {
        var res = await f.CreateClient().GetAsync("/docs");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("text/html", res.Content.Headers.ContentType!.ToString());

        var body = await res.Content.ReadAsStringAsync();
        // Spec §3: no phone-home / no external CDN — assets must be same-origin/inline.
        Assert.DoesNotContain("https://cdn.", body);
        Assert.DoesNotContain("http://cdn.", body);
        Assert.DoesNotContain("unpkg.com", body);
        Assert.DoesNotContain("jsdelivr", body);
    }
}
