using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class ApprovalTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ApprovalTests(ApiFactory f) => _f = f;

    private record ApprovalDto(Guid Id, Guid VersionId, Guid ApproverId, DateTimeOffset? DueAt);

    // An approver must be a document member (spec §11) — so every fixture here seeds one into the
    // owner's org and adds them to the document, rather than registering a stranger with their own org.
    private async Task<Account> MemberAsync(Account owner, Guid docId, string role = "Editor")
    {
        var acct = await _f.SeedOrgUserAsync(owner.OrgId);
        (await owner.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email = acct.Email, role })).EnsureSuccessStatusCode();
        return acct;
    }

    private async Task<Guid> PublishAsync(Account owner, Guid vid)
    {
        (await owner.Client.PostAsJsonAsync($"/api/v1/versions/{vid}/publish", new { kind = "minor" }))
            .EnsureSuccessStatusCode();
        return vid;
    }

    private async Task<List<ApprovalDto>> RequestAsync(Account owner, Guid vid, params Guid[] approverIds)
    {
        var res = await owner.Client.PostAsJsonAsync($"/api/v1/versions/{vid}/approvals",
            new { approverIds });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<List<ApprovalDto>>())!;
    }

    [Fact]
    public async Task Cannot_request_approval_on_unpublished_version_400()
    {
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync("Doc");
        var (vid, _) = await owner.Client.UploadAsync(docId, DocxFixtures.Base()); // unpublished

        var res = await owner.Client.PostAsJsonAsync($"/api/v1/versions/{vid}/approvals",
            new { approverIds = new[] { owner.UserId } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Request_creates_one_row_per_approver()
    {
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync("Doc");
        var a = await MemberAsync(owner, docId);
        var b = await MemberAsync(owner, docId);
        var (vid, _) = await owner.Client.UploadAsync(docId, DocxFixtures.Base());
        await PublishAsync(owner, vid);

        var due = DateTimeOffset.UtcNow.AddDays(3);
        var res = await owner.Client.PostAsJsonAsync($"/api/v1/versions/{vid}/approvals",
            new { approverIds = new[] { a.UserId, b.UserId }, dueAt = due });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var rows = (await res.Content.ReadFromJsonAsync<List<ApprovalDto>>())!;
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ApproverId == a.UserId);
        Assert.Contains(rows, r => r.ApproverId == b.UserId);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var persisted = await db.ApprovalRequests.Where(x => x.VersionId == vid).ToListAsync();
        Assert.Equal(2, persisted.Count);
        Assert.All(persisted, x => Assert.NotNull(x.DueAt));
    }

    [Fact]
    public async Task Respond_records_immutable_decision()
    {
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync("Doc");
        var approver = await MemberAsync(owner, docId);
        var (vid, _) = await owner.Client.UploadAsync(docId, DocxFixtures.Base());
        await PublishAsync(owner, vid);

        var id = (await RequestAsync(owner, vid, approver.UserId))[0].Id;

        var res = await approver.Client.PostAsJsonAsync($"/api/v1/approvals/{id}:respond",
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
        var again = await approver.Client.PostAsJsonAsync($"/api/v1/approvals/{id}:respond",
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
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync("Doc");
        var approver = await MemberAsync(owner, docId);
        var other = await MemberAsync(owner, docId); // a member, but not the named approver
        var (vid, _) = await owner.Client.UploadAsync(docId, DocxFixtures.Base());
        await PublishAsync(owner, vid);

        var id = (await RequestAsync(owner, vid, approver.UserId))[0].Id;

        var res = await other.Client.PostAsJsonAsync($"/api/v1/approvals/{id}:respond", new { decision = "approved" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Cancel_closes_request()
    {
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync("Doc");
        var approver = await MemberAsync(owner, docId);
        var (vid, _) = await owner.Client.UploadAsync(docId, DocxFixtures.Base());
        await PublishAsync(owner, vid);

        var id = (await RequestAsync(owner, vid, approver.UserId))[0].Id;

        var res = await owner.Client.PostAsJsonAsync($"/api/v1/approvals/{id}:cancel", new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var row = await db.ApprovalRequests.FirstAsync(x => x.Id == id);
            Assert.NotNull(row.CancelledAt);
        }

        // A cancelled request can't be responded to.
        var respond = await approver.Client.PostAsJsonAsync($"/api/v1/approvals/{id}:respond", new { decision = "approved" });
        Assert.Equal(HttpStatusCode.Conflict, respond.StatusCode);
    }

    [Fact]
    public async Task A_non_member_cannot_be_named_as_an_approver()
    {
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync("Members only");
        var (vid, _) = await owner.Client.UploadAsync(docId, DocxFixtures.Base());
        (await owner.Client.PostAsJsonAsync($"/api/v1/versions/{vid}/publish", new { kind = "minor" }))
            .EnsureSuccessStatusCode();

        // Same org, never added to this document.
        var stranger = await _f.SeedOrgUserAsync(owner.OrgId);

        var res = await owner.Client.PostAsJsonAsync(
            $"/api/v1/versions/{vid}/approvals", new { approverIds = new[] { stranger.UserId } });

        // Otherwise this user receives a decision right on a document they cannot even read, and
        // POST /api/v1/approvals/{id}:respond authorizes on ApproverId alone.
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
    }

    // Defence in depth for the same hole: a row that predates the creation-time guard above (this
    // product is already deployed) must still not let a non-member decide. spec §11.
    [Fact]
    public async Task A_pre_existing_row_naming_a_non_member_cannot_be_decided()
    {
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync("Members only");
        var (vid, _) = await owner.Client.UploadAsync(docId, DocxFixtures.Base());
        (await owner.Client.PostAsJsonAsync($"/api/v1/versions/{vid}/publish", new { kind = "minor" }))
            .EnsureSuccessStatusCode();

        var stranger = await _f.SeedOrgUserAsync(owner.OrgId);

        // Inserted straight into the table, bypassing the endpoint — exactly what an install that ran
        // the vulnerable build already has on disk.
        var id = Guid.NewGuid();
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            db.Add(new ApprovalRequest
            {
                Id = id, VersionId = vid, ApproverId = stranger.UserId,
                RequestedBy = owner.UserId, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden,
            (await stranger.Client.GetAsync($"/api/v1/documents/{docId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await stranger.Client.PostAsJsonAsync($"/api/v1/approvals/{id}:respond",
                new { decision = "approved" })).StatusCode);
    }
}
