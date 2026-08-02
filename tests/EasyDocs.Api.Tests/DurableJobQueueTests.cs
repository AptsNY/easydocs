using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Diffing;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EasyDocs.Api.Tests;

// Issue #16: the BackgroundJobs table is the queue; the in-process channel is only a latency nudge.
// These tests assert the durable half — a job row that no channel ever announced still gets
// processed (that is exactly the restart story: rows left by a dead process are found by polling),
// and a poison row is dropped instead of wedging the worker forever.
public class DurableJobQueueTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = f.CreateClient();
        var email = $"jobs-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "J", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
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
    private record UploadDto(Guid VersionId);

    private async Task<bool> PollAsync(Func<EasyDocsDbContext, Task<bool>> probe, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            using var scope = f.Services.CreateScope();
            if (await probe(scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>())) return true;
            await Task.Delay(250);
        }
        return false;
    }

    [Fact]
    public async Task A_job_row_nobody_nudged_about_is_still_processed()
    {
        // Two real versions, so the diff job has blobs to chew on.
        var c = await AuthedClientAsync();
        var docId = (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Durable" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var v1 = (await (await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(DocxFixtures.Base())))
            .Content.ReadFromJsonAsync<UploadDto>())!.VersionId;
        var v2 = (await (await c.PostAsync($"/api/v1/documents/{docId}/versions:import", Docx(DocxFixtures.Edited())))
            .Content.ReadFromJsonAsync<UploadDto>())!.VersionId;

        string fromSha, toSha;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            fromSha = await db.Versions.Where(v => v.Id == v1).Select(v => v.BlobSha256).SingleAsync();
            toSha = await db.Versions.Where(v => v.Id == v2).Select(v => v.BlobSha256).SingleAsync();
        }

        // Let the organic upload-triggered job finish, then erase its result and plant a bare row —
        // the same shape a crashed process leaves behind: committed to the table, nudge long gone.
        Assert.True(await PollAsync(
            db => db.VersionDiffs.AnyAsync(d => d.FromSha256 == fromSha && d.ToSha256 == toSha),
            TimeSpan.FromSeconds(15)), "organic diff never landed — upload path is broken");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            await db.VersionDiffs.Where(d => d.FromSha256 == fromSha && d.ToSha256 == toSha)
                .ExecuteDeleteAsync();
            db.Add(BackgroundJobs.For(BackgroundJobs.Diff, new DiffJob(fromSha, toSha, docId)));
            await db.SaveChangesAsync();
        }

        // No channel write happened for that row; only the poll loop can find it.
        Assert.True(await PollAsync(
            db => db.VersionDiffs.AnyAsync(d => d.FromSha256 == fromSha && d.ToSha256 == toSha),
            TimeSpan.FromSeconds(15)), "worker never picked up the un-nudged row — polling is broken");
    }

    [Fact]
    public async Task A_poison_job_is_dropped_at_the_attempt_cap_and_the_worker_survives()
    {
        long poisonId;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var poison = new BackgroundJob
            {
                Type = BackgroundJobs.Diff,
                Payload = "{\"this is\": \"not a DiffJob\"", // truncated JSON — always throws
                Attempts = 5,                               // next claim exceeds the cap
                RunAfter = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Add(poison);
            await db.SaveChangesAsync();
            poisonId = poison.Id;
        }

        Assert.True(await PollAsync(
            db => db.BackgroundJobs.AllAsync(j => j.Id != poisonId),
            TimeSpan.FromSeconds(15)), "poison row was never dropped");

        // The worker that dropped it must still be alive and serving.
        (await f.CreateClient().GetAsync("/health")).EnsureSuccessStatusCode();
    }
}
