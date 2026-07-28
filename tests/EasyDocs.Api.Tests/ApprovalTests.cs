using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class ApprovalTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ApprovalTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private record RegisterDto(Guid Id);
    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId, int Major, int Minor, int Revision);
    private record ApprovalDto(Guid Id, Guid VersionId, Guid ApproverId, DateTimeOffset? DueAt);

    private async Task<(HttpClient client, Guid userId)> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"apr-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "A", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var body = await reg.Content.ReadFromJsonAsync<RegisterDto>();
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return (client, body!.Id);
    }

    private static MultipartFormDataContent Docx(byte marker)
    {
        var part = new ByteArrayContent(new byte[] { marker, 9, 9 });
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        return new MultipartFormDataContent { { part, "file", "f.docx" } };
    }

    private async Task<Guid> CreateDocAsync(HttpClient c)
        => (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Doc" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;

    private async Task<UploadDto> UploadAsync(HttpClient c, Guid docId, byte marker)
    {
        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(marker));
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        return (await up.Content.ReadFromJsonAsync<UploadDto>())!;
    }

    private async Task<Guid> PublishAsync(HttpClient c, Guid vid)
    {
        var res = await c.PostAsJsonAsync($"/api/v1/versions/{vid}/publish", new { kind = "minor" });
        res.EnsureSuccessStatusCode();
        return vid;
    }

    [Fact]
    public async Task Cannot_request_approval_on_unpublished_version_400()
    {
        var (c, me) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);
        var v = await UploadAsync(c, docId, 1); // unpublished

        var res = await c.PostAsJsonAsync($"/api/v1/versions/{v.VersionId}/approvals", new { approverIds = new[] { me } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Request_creates_one_row_per_approver()
    {
        var (c, _) = await AuthedClientAsync();
        var (_, a) = await AuthedClientAsync();
        var (_, b) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);
        var v = await UploadAsync(c, docId, 1);
        await PublishAsync(c, v.VersionId);

        var due = DateTimeOffset.UtcNow.AddDays(3);
        var res = await c.PostAsJsonAsync($"/api/v1/versions/{v.VersionId}/approvals",
            new { approverIds = new[] { a, b }, dueAt = due });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var rows = (await res.Content.ReadFromJsonAsync<List<ApprovalDto>>())!;
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ApproverId == a);
        Assert.Contains(rows, r => r.ApproverId == b);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var persisted = await db.ApprovalRequests.Where(x => x.VersionId == v.VersionId).ToListAsync();
        Assert.Equal(2, persisted.Count);
        Assert.All(persisted, x => Assert.NotNull(x.DueAt));
    }

    [Fact]
    public async Task Respond_records_immutable_decision()
    {
        var (c, _) = await AuthedClientAsync();
        var (approver, approverId) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);
        var v = await UploadAsync(c, docId, 1);
        await PublishAsync(c, v.VersionId);

        var created = (await (await c.PostAsJsonAsync($"/api/v1/versions/{v.VersionId}/approvals",
            new { approverIds = new[] { approverId } })).Content.ReadFromJsonAsync<List<ApprovalDto>>())!;
        var id = created[0].Id;

        var res = await approver.PostAsJsonAsync($"/api/v1/approvals/{id}:respond",
            new { decision = "approved", comment = "ok" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var row = await db.ApprovalRequests.FirstAsync(x => x.Id == id);
            Assert.Equal("approved", row.Decision);
            Assert.Equal("ok", row.DecisionComment);
            Assert.NotNull(row.DecidedAt);
        }

        // Immutable: a second response is rejected and does not overwrite.
        var again = await approver.PostAsJsonAsync($"/api/v1/approvals/{id}:respond",
            new { decision = "rejected", comment = "changed my mind" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var row = await db.ApprovalRequests.FirstAsync(x => x.Id == id);
            Assert.Equal("approved", row.Decision);
            Assert.Equal("ok", row.DecisionComment);
        }
    }

    [Fact]
    public async Task Only_named_approver_may_respond_403()
    {
        var (c, _) = await AuthedClientAsync();
        var (_, approverId) = await AuthedClientAsync();
        var (stranger, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);
        var v = await UploadAsync(c, docId, 1);
        await PublishAsync(c, v.VersionId);

        var created = (await (await c.PostAsJsonAsync($"/api/v1/versions/{v.VersionId}/approvals",
            new { approverIds = new[] { approverId } })).Content.ReadFromJsonAsync<List<ApprovalDto>>())!;
        var id = created[0].Id;

        var res = await stranger.PostAsJsonAsync($"/api/v1/approvals/{id}:respond", new { decision = "approved" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Cancel_closes_request()
    {
        var (c, _) = await AuthedClientAsync();
        var (approver, approverId) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);
        var v = await UploadAsync(c, docId, 1);
        await PublishAsync(c, v.VersionId);

        var created = (await (await c.PostAsJsonAsync($"/api/v1/versions/{v.VersionId}/approvals",
            new { approverIds = new[] { approverId } })).Content.ReadFromJsonAsync<List<ApprovalDto>>())!;
        var id = created[0].Id;

        var res = await c.PostAsJsonAsync($"/api/v1/approvals/{id}:cancel", new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var row = await db.ApprovalRequests.FirstAsync(x => x.Id == id);
            Assert.NotNull(row.CancelledAt);
        }

        // A cancelled request can't be responded to.
        var respond = await approver.PostAsJsonAsync($"/api/v1/approvals/{id}:respond", new { decision = "approved" });
        Assert.Equal(HttpStatusCode.Conflict, respond.StatusCode);
    }
}
