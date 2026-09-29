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
    public async Task Openapi_json_lists_the_m4_5_phase_a_routes()
    {
        // Phase A (M4.5) added the approvals-read and org-management endpoints the SPA needs
        // (spec §10.1). Routes are discovered from minimal-API metadata, so this is the guard
        // that catches a route added to src/ without the tags/grouping needed to surface it here.
        var res = await f.CreateClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var paths = doc.RootElement.GetProperty("paths");

        foreach (var path in new[]
        {
            "/api/v1/approvals",
            "/api/v1/versions/{vid}/approvals",
            "/api/v1/org",
            "/api/v1/org/members",
            "/api/v1/org/members/{uid}",
        })
            Assert.True(paths.TryGetProperty(path, out _), $"missing {path} in the published document");
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

    // The docs site publishes a committed snapshot of /openapi/v1.json (rendered by Swagger UI at
    // /api/ on the site), because the mkdocs job is python-only and cannot boot the app. This test
    // is what stops that snapshot rotting: any change to the served document fails CI until the
    // snapshot is regenerated. To regenerate:
    //   UPDATE_OPENAPI_SNAPSHOT=1 dotnet test --filter Openapi_snapshot_in_docs_site_matches
    [Fact]
    public async Task Openapi_snapshot_in_docs_site_matches_the_served_document()
    {
        var served = Normalize(await f.CreateClient().GetStringAsync("/openapi/v1.json"));

        var path = Path.Combine(RepoRoot(), "docs-site", "docs", "api", "openapi", "v1.json");
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI_SNAPSHOT") == "1")
            await File.WriteAllTextAsync(path, served + "\n");

        Assert.True(File.Exists(path), $"missing snapshot {path} — regenerate per the comment above");
        var snapshot = (await File.ReadAllTextAsync(path)).TrimEnd('\n');
        Assert.True(snapshot == served,
            "docs-site/docs/api/openapi/v1.json no longer matches the served /openapi/v1.json —"
            + " regenerate with: UPDATE_OPENAPI_SNAPSHOT=1 dotnet test --filter Openapi_snapshot_in_docs_site_matches");
    }

    // Indent for reviewable diffs; drop `servers` — it echoes the test host's address, which is
    // meaningless in a published document (each install is its own server).
    private static string Normalize(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("servers");
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "easydocs.slnx")))
                return d.FullName;
        throw new InvalidOperationException("easydocs.slnx not found above the test binary.");
    }
}
