using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Diffing;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Tests;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class DiffTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public DiffTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"diff-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "D", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    private static MultipartFormDataContent Docx(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        return new MultipartFormDataContent { { part, "file", "d.docx" } };
    }

    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId, int Major, int Minor, int Revision);
    private record SummaryDto(int Insertions, int Deletions, int Moves, int FormatChanges);

    // Base @ 0.0.1, Edited @ 0.0.2 (import = fast-forward on main -> parent is the base version).
    private async Task<(Guid docId, Guid v1, Guid v2)> BaseAndEditedAsync(HttpClient c)
    {
        var docId = (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Diff" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(DocxFixtures.Base()));
        up.EnsureSuccessStatusCode();
        var v1 = (await up.Content.ReadFromJsonAsync<UploadDto>())!.VersionId;
        var imp = await c.PostAsync($"/api/v1/documents/{docId}/versions:import", Docx(DocxFixtures.Edited()));
        imp.EnsureSuccessStatusCode();
        var v2 = (await imp.Content.ReadFromJsonAsync<UploadDto>())!.VersionId;
        return (docId, v1, v2);
    }

    [Fact]
    public async Task Numeric_summary_computed_eagerly_after_commit()
    {
        var c = await AuthedClientAsync();
        var (docId, v1, v2) = await BaseAndEditedAsync(c);

        // Eager BackgroundService populates the numeric summary; poll the row until it lands.
        var populated = false;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            populated = await db.VersionDiffs.AnyAsync(d => d.Insertions != null || d.Deletions != null);
            if (populated) break;
            await Task.Delay(250);
        }
        Assert.True(populated, "worker did not populate the numeric summary within 15s");

        var summary = await c.GetFromJsonAsync<SummaryDto>(
            $"/api/v1/documents/{docId}/compare?from={v1}&to={v2}&format=summary");
        Assert.True(summary!.Insertions > 0 || summary.Deletions > 0);
    }

    [Fact]
    public async Task Redline_html_on_demand_and_cached()
    {
        var c = await AuthedClientAsync();
        var (docId, v1, v2) = await BaseAndEditedAsync(c);

        var url = $"/api/v1/documents/{docId}/compare?from={v1}&to={v2}&format=html";
        var first = await c.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("text/html", first.Content.Headers.ContentType?.MediaType);

        // First compare caches the HTML blob on the VersionDiff row.
        string? htmlSha;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var row = await db.VersionDiffs.FirstAsync(d => d.HtmlBlobSha256 != null);
            htmlSha = row.HtmlBlobSha256;
            Assert.NotNull(htmlSha);
        }

        var second = await c.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // Row still points at the same cached blob (not recomputed into a new one).
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var row = await db.VersionDiffs.FirstAsync(d => d.HtmlBlobSha256 != null);
            Assert.Equal(htmlSha, row.HtmlBlobSha256);
        }
    }

    [Fact]
    public async Task WmlComparer_failure_degrades_not_throws()
    {
        using var scope = _f.Services.CreateScope();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var svc = scope.ServiceProvider.GetRequiredService<WmlComparerDiffService>();

        var stored = await blobs.PutAsync(new MemoryStream(DocxFixtures.Malformed()));

        var result = await svc.SummaryAsync(stored.Sha256, stored.Sha256, default);
        Assert.False(result.Available);
    }
}
