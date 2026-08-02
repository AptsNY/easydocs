using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EasyDocs.Api.Tests;

// Issue #15: the sweep deletes blobs referenced by nothing (older than the grace window) and keeps
// everything any Versions or VersionDiffs column points at. Fast knobs via BlobGc:*Seconds; this
// class's hosts share one Postgres with each other only, so an aggressive GC here cannot eat
// another class's in-flight commits.
public class BlobGcTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    [Fact]
    public async Task Sweep_removes_the_orphan_and_keeps_everything_referenced()
    {
        // Seed through the default host (its ApiFactory config has NO fast GC — inert background).
        var client = f.CreateClient();
        var email = $"gc-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "G", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var jwt = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="))
            ["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var docId = (await (await client.PostAsJsonAsync("/api/v1/documents", new { name = "GC" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var part = new ByteArrayContent(DocxFixtures.Base());
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        var up = await client.PostAsync($"/api/v1/documents/{docId}/versions",
            new MultipartFormDataContent { { part, "file", "d.docx" } });
        up.EnsureSuccessStatusCode();

        var store = f.Services.GetRequiredService<IBlobStore>();
        string referencedSha, orphanSha, diffOnlySha1, diffOnlySha2;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            referencedSha = await db.Versions.Where(v => v.DocumentId == docId)
                .Select(v => v.BlobSha256).FirstAsync();

            // An orphan: bytes in the store + a Blobs row, referenced by nothing, older than grace.
            var orphan = await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(
                $"orphan-{Guid.NewGuid():N}")));
            orphanSha = orphan.Sha256;
            db.Add(new Blob { Sha256 = orphanSha, SizeBytes = orphan.SizeBytes, Mime = "application/octet-stream",
                StorageKey = orphanSha, CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) });

            // A pair referenced ONLY by a VersionDiffs row — the cache columns must also pin blobs.
            var d1 = await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes($"d1-{Guid.NewGuid():N}")));
            var d2 = await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes($"d2-{Guid.NewGuid():N}")));
            (diffOnlySha1, diffOnlySha2) = (d1.Sha256, d2.Sha256);
            db.Add(new Blob { Sha256 = diffOnlySha1, SizeBytes = d1.SizeBytes, Mime = "text/html",
                StorageKey = diffOnlySha1, CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) });
            db.Add(new Blob { Sha256 = diffOnlySha2, SizeBytes = d2.SizeBytes, Mime = "text/html",
                StorageKey = diffOnlySha2, CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) });
            db.Add(new VersionDiff { FromSha256 = diffOnlySha1, ToSha256 = diffOnlySha2,
                CreatedAt = DateTimeOffset.UtcNow });
            // Backdate the referenced upload too, so "kept" is proven by the reference, not by grace.
            await db.SaveChangesAsync();
            await db.Blobs.Where(b => b.Sha256 == referencedSha)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.CreatedAt, DateTimeOffset.UtcNow.AddDays(-2)));
        }

        // A second host on the same DB/store with a fast sweep: first pass fires after ~1s.
        using var gcHost = f.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BlobGc:IntervalSeconds"] = "1",
                ["BlobGc:GraceSeconds"] = "3600", // orphans are backdated 2 days; fresh test traffic is safe
            })));
        using var _ = gcHost.CreateClient(); // boot it

        var sw = Stopwatch.StartNew();
        var orphanGone = false;
        while (sw.Elapsed < TimeSpan.FromSeconds(15) && !orphanGone)
        {
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            orphanGone = !await db.Blobs.AnyAsync(b => b.Sha256 == orphanSha);
            if (!orphanGone) await Task.Delay(250);
        }

        Assert.True(orphanGone, "orphan blob row survived the sweep");
        Assert.False(await store.ExistsAsync(orphanSha), "orphan bytes survived the sweep");

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            Assert.True(await db.Blobs.AnyAsync(b => b.Sha256 == referencedSha), "version blob row was collected");
            Assert.True(await db.Blobs.AnyAsync(b => b.Sha256 == diffOnlySha1), "diff-referenced blob was collected");
            Assert.True(await db.Blobs.AnyAsync(b => b.Sha256 == diffOnlySha2), "diff-referenced blob was collected");
        }
        Assert.True(await store.ExistsAsync(referencedSha), "version blob bytes were collected");
        Assert.True(await store.ExistsAsync(diffOnlySha1), "diff blob bytes were collected");
    }

    private record DocDto(Guid Id);
}
