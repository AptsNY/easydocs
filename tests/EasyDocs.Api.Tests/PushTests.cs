using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// Push back — the review half of M4 (spec §8, §11, E9). The load-bearing rule under test: a push is
// authorized on membership of the SOURCE COPY, not the target. That is the one sanctioned bypass of the
// target authorization chokepoint, so these tests pin both what it permits (a copy member with no target
// role may push) and what it must still refuse (a non-member of the copy; a target that is not the
// document the copy was forked from).
public class PushTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public PushTests(ApiFactory f) => _f = f;

    private record CopyDto(Guid Id, Guid ParentDocumentId, Guid ForkedFromVersionId, Guid VersionId);
    private record PushDto(Guid Id, string Status, Guid CopyDocumentId, Guid TargetDocumentId,
        Guid SourceVersionId, Guid? MaterializedVersionId);

    // Alice owns a master with one version and forks a copy from it.
    private sealed record Fixture(Account Alice, Guid MasterId, Guid MasterVersionId, Guid CopyId);

    private async Task<Fixture> ForkedAsync()
    {
        var alice = await _f.RegisterAsync();
        var masterId = await alice.Client.CreateDocAsync("Master agreement");
        var (masterVid, _) = await alice.Client.UploadAsync(masterId, DocxFixtures.Base());

        var res = await alice.Client.PostAsJsonAsync($"/api/v1/versions/{masterVid}/copies", new { name = "Reviewer copy" });
        res.EnsureSuccessStatusCode();
        var copy = (await res.Content.ReadFromJsonAsync<CopyDto>())!;

        return new Fixture(alice, masterId, masterVid, copy.Id);
    }

    // An external reviewer who is a member of the COPY ONLY — never of the master.
    private async Task<Account> CopyOnlyReviewerAsync(Fixture f, string role = "Editor")
    {
        var reviewer = await _f.SeedOrgUserAsync(f.Alice.OrgId);
        (await f.Alice.Client.PostAsJsonAsync($"/api/v1/documents/{f.CopyId}/members",
            new { email = reviewer.Email, role })).EnsureSuccessStatusCode();
        return reviewer;
    }

    private static Task<HttpResponseMessage> PushAsync(HttpClient c, Guid copyId, Guid targetId, Guid versionId) =>
        c.PostAsJsonAsync($"/api/v1/documents/{copyId}/pushes",
            new { targetDocumentId = targetId, versionId });

    private async Task<PushDto> PushOkAsync(HttpClient c, Guid copyId, Guid targetId, Guid versionId)
    {
        var res = await PushAsync(c, copyId, targetId, versionId);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<PushDto>())!;
    }

    [Fact]
    public async Task A_copy_member_with_no_target_role_may_push_and_it_waits_for_review()
    {
        var f = await ForkedAsync();
        var reviewer = await CopyOnlyReviewerAsync(f);
        var (redlined, _) = await reviewer.Client.UploadAsync(f.CopyId, DocxFixtures.Edited());

        // The bypass: the reviewer resolves NO role on the master...
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reviewer.Client.GetAsync($"/api/v1/documents/{f.MasterId}")).StatusCode);
        // ...yet may still push into it, because authorization is on the source copy (spec §8/§11).
        var push = await PushOkAsync(reviewer.Client, f.CopyId, f.MasterId, redlined);

        Assert.Equal("pending", push.Status);
        Assert.Null(push.MaterializedVersionId);
        Assert.Equal(f.CopyId, push.CopyDocumentId);
        Assert.Equal(f.MasterId, push.TargetDocumentId);

        // Nothing has entered the target's history yet.
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        Assert.Equal(1, await db.Versions.CountAsync(v => v.DocumentId == f.MasterId));
        Assert.Equal(1, await db.Branches.CountAsync(b => b.DocumentId == f.MasterId));
    }

    [Fact]
    public async Task Target_members_see_pending_requests_and_the_pusher_sees_the_decision_on_the_copy()
    {
        var f = await ForkedAsync();
        var reviewer = await CopyOnlyReviewerAsync(f);
        var (redlined, _) = await reviewer.Client.UploadAsync(f.CopyId, DocxFixtures.Edited());
        var push = await PushOkAsync(reviewer.Client, f.CopyId, f.MasterId, redlined);

        // The target's members find it under the target document, filterable by status.
        var inbound = (await (await f.Alice.Client.GetAsync(
            $"/api/v1/documents/{f.MasterId}/push-requests?status=pending")).Content.ReadFromJsonAsync<PushDto[]>())!;
        Assert.Equal(push.Id, Assert.Single(inbound).Id);

        // The pusher, who has no target role, reads the same row under the COPY they do belong to.
        var outbound = (await (await reviewer.Client.GetAsync(
            $"/api/v1/documents/{f.CopyId}/push-requests")).Content.ReadFromJsonAsync<PushDto[]>())!;
        Assert.Equal(push.Id, Assert.Single(outbound).Id);

        // And cannot reach the list on the target itself.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reviewer.Client.GetAsync($"/api/v1/documents/{f.MasterId}/push-requests")).StatusCode);
    }

    [Fact]
    public async Task A_pusher_who_also_holds_a_target_role_materializes_immediately()
    {
        var f = await ForkedAsync();
        // Alice owns both the master and the copy, so her push needs no review.
        var (redlined, _) = await f.Alice.Client.UploadAsync(f.CopyId, DocxFixtures.Edited());

        var push = await PushOkAsync(f.Alice.Client, f.CopyId, f.MasterId, redlined);

        Assert.Equal("auto_accepted", push.Status);
        Assert.NotNull(push.MaterializedVersionId);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var materialized = await db.Versions.FirstAsync(v => v.Id == push.MaterializedVersionId);
        var branch = await db.Branches.FirstAsync(b => b.Id == materialized.BranchId);

        Assert.Equal(f.MasterId, materialized.DocumentId);
        Assert.Equal(VersionSource.CopyPush, materialized.Source);
        Assert.Equal(BranchKind.IncomingPush, branch.Kind);
        Assert.NotEqual(0, branch.Ordinal); // never main

        // The pushed content, not a re-render of it.
        var sourceSha = await db.Versions.Where(v => v.Id == redlined).Select(v => v.BlobSha256).FirstAsync();
        Assert.Equal(sourceSha, materialized.BlobSha256);
    }

    [Fact]
    public async Task Accepting_a_push_materializes_it_on_an_incoming_branch()
    {
        var f = await ForkedAsync();
        var reviewer = await CopyOnlyReviewerAsync(f);
        var (redlined, _) = await reviewer.Client.UploadAsync(f.CopyId, DocxFixtures.Edited());
        var push = await PushOkAsync(reviewer.Client, f.CopyId, f.MasterId, redlined);

        var res = await f.Alice.Client.PostAsync($"/api/v1/push-requests/{push.Id}:accept", null);

        res.EnsureSuccessStatusCode();
        var accepted = (await res.Content.ReadFromJsonAsync<PushDto>())!;
        Assert.Equal("accepted", accepted.Status);
        Assert.NotNull(accepted.MaterializedVersionId);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var row = await db.PushRequests.FirstAsync(p => p.Id == push.Id);
        Assert.Equal("accepted", row.Status);
        Assert.NotNull(row.DecidedAt);

        var materialized = await db.Versions.FirstAsync(v => v.Id == accepted.MaterializedVersionId);
        Assert.Equal(BranchKind.IncomingPush,
            (await db.Branches.FirstAsync(b => b.Id == materialized.BranchId)).Kind);
    }

    [Fact]
    public async Task Rejecting_a_push_keeps_it_out_of_the_target_history_and_notifies_the_pusher()
    {
        var f = await ForkedAsync();
        var reviewer = await CopyOnlyReviewerAsync(f);
        var (redlined, _) = await reviewer.Client.UploadAsync(f.CopyId, DocxFixtures.Edited());
        var push = await PushOkAsync(reviewer.Client, f.CopyId, f.MasterId, redlined);

        // The pusher holds no target role, so the notification has to reach them on the COPY.
        var events = await _f.CaptureEventsAsync(f.CopyId,
            () => f.Alice.Client.PostAsync($"/api/v1/push-requests/{push.Id}:reject", null),
            until: "push.reviewed");
        Assert.Contains("push.reviewed", events);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var row = await db.PushRequests.FirstAsync(p => p.Id == push.Id);
        Assert.Equal("rejected", row.Status);
        Assert.Null(row.MaterializedVersionId);
        Assert.NotNull(row.DecidedAt);

        // Nothing entered the target's history and no branch was created for it.
        Assert.Equal(1, await db.Versions.CountAsync(v => v.DocumentId == f.MasterId));
        Assert.False(await db.Branches.AnyAsync(b => b.DocumentId == f.MasterId && b.Kind == BranchKind.IncomingPush));

        // And the pusher can read the decision themselves.
        var outbound = (await (await reviewer.Client.GetAsync(
            $"/api/v1/documents/{f.CopyId}/push-requests")).Content.ReadFromJsonAsync<PushDto[]>())!;
        Assert.Equal("rejected", Assert.Single(outbound).Status);
    }

    [Fact]
    public async Task A_push_requested_event_reaches_the_target_consoles()
    {
        var f = await ForkedAsync();
        var reviewer = await CopyOnlyReviewerAsync(f);
        var (redlined, _) = await reviewer.Client.UploadAsync(f.CopyId, DocxFixtures.Edited());

        var events = await _f.CaptureEventsAsync(f.MasterId,
            () => PushAsync(reviewer.Client, f.CopyId, f.MasterId, redlined),
            until: "push.requested");

        Assert.Contains("push.requested", events);
    }

    [Fact]
    public async Task A_non_member_of_the_copy_cannot_push_even_with_a_target_role()
    {
        var f = await ForkedAsync();
        var reviewer = await CopyOnlyReviewerAsync(f);
        var (redlined, _) = await reviewer.Client.UploadAsync(f.CopyId, DocxFixtures.Edited());

        // A master Editor who is NOT on the copy: the source is what authorizes, so this is a 403.
        var masterEditor = await _f.SeedOrgUserAsync(f.Alice.OrgId);
        (await f.Alice.Client.PostAsJsonAsync($"/api/v1/documents/{f.MasterId}/members",
            new { email = masterEditor.Email, role = "Editor" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PushAsync(masterEditor.Client, f.CopyId, f.MasterId, redlined)).StatusCode);

        // A Viewer on the copy may read it but not push out of it.
        var copyViewer = await CopyOnlyReviewerAsync(f, "Viewer");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PushAsync(copyViewer.Client, f.CopyId, f.MasterId, redlined)).StatusCode);

        // Another org never learns the copy exists.
        var outsider = await _f.RegisterAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await PushAsync(outsider.Client, f.CopyId, f.MasterId, redlined)).StatusCode);

        // Unauthenticated.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await PushAsync(_f.CreateClient(), f.CopyId, f.MasterId, redlined)).StatusCode);
    }

    [Fact]
    public async Task A_push_must_target_the_document_the_copy_was_forked_from()
    {
        // Without this the bypass would be a privilege escalation: membership of ANY copy would grant a
        // write into ANY document, since the target's own chokepoint is deliberately skipped.
        var f = await ForkedAsync();
        var reviewer = await CopyOnlyReviewerAsync(f);
        var (redlined, _) = await reviewer.Client.UploadAsync(f.CopyId, DocxFixtures.Edited());

        var unrelated = await f.Alice.Client.CreateDocAsync("Someone else's document");
        await f.Alice.Client.UploadAsync(unrelated, DocxFixtures.Base());

        var res = await PushAsync(reviewer.Client, f.CopyId, unrelated, redlined);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        Assert.False(await db.PushRequests.AnyAsync(p => p.TargetDocumentId == unrelated));
        Assert.Equal(1, await db.Versions.CountAsync(v => v.DocumentId == unrelated));
    }

    [Fact]
    public async Task A_push_source_version_must_belong_to_the_copy()
    {
        var f = await ForkedAsync();
        var reviewer = await CopyOnlyReviewerAsync(f);

        // The master's own version is not the copy's to push.
        Assert.Equal(HttpStatusCode.NotFound,
            (await PushAsync(reviewer.Client, f.CopyId, f.MasterId, f.MasterVersionId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await PushAsync(reviewer.Client, f.CopyId, f.MasterId, Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Pushing_content_identical_to_the_target_head_is_refused()
    {
        var f = await ForkedAsync();
        var reviewer = await CopyOnlyReviewerAsync(f);

        // The copy's first version IS the master's head content (zero-copy fork), so there is nothing to
        // push. Materializing it would dedupe inside the write path and leave an empty incoming branch.
        var copyFirst = await CopyFirstVersionAsync(f.CopyId);
        var res = await PushAsync(reviewer.Client, f.CopyId, f.MasterId, copyFirst);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        Assert.False(await db.Branches.AnyAsync(b => b.DocumentId == f.MasterId && b.Kind == BranchKind.IncomingPush));
        Assert.Empty(await db.PushRequests.Where(p => p.CopyDocumentId == f.CopyId).ToListAsync());
    }

    [Fact]
    public async Task Accept_and_reject_require_edit_on_the_target()
    {
        var f = await ForkedAsync();
        var reviewer = await CopyOnlyReviewerAsync(f);
        var (redlined, _) = await reviewer.Client.UploadAsync(f.CopyId, DocxFixtures.Edited());
        var push = await PushOkAsync(reviewer.Client, f.CopyId, f.MasterId, redlined);

        // The pusher cannot approve their own push into a document they hold no role on.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reviewer.Client.PostAsync($"/api/v1/push-requests/{push.Id}:accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reviewer.Client.PostAsync($"/api/v1/push-requests/{push.Id}:reject", null)).StatusCode);

        // Nor may a Viewer on the target.
        var targetViewer = await _f.SeedOrgUserAsync(f.Alice.OrgId);
        (await f.Alice.Client.PostAsJsonAsync($"/api/v1/documents/{f.MasterId}/members",
            new { email = targetViewer.Email, role = "Viewer" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await targetViewer.Client.PostAsync($"/api/v1/push-requests/{push.Id}:accept", null)).StatusCode);

        // Another org gets 404, and an unknown id is 404.
        var outsider = await _f.RegisterAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await outsider.Client.PostAsync($"/api/v1/push-requests/{push.Id}:accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await f.Alice.Client.PostAsync($"/api/v1/push-requests/{Guid.NewGuid()}:accept", null)).StatusCode);
    }

    [Fact]
    public async Task A_decided_push_cannot_be_decided_again()
    {
        var f = await ForkedAsync();
        var reviewer = await CopyOnlyReviewerAsync(f);
        var (redlined, _) = await reviewer.Client.UploadAsync(f.CopyId, DocxFixtures.Edited());
        var push = await PushOkAsync(reviewer.Client, f.CopyId, f.MasterId, redlined);

        (await f.Alice.Client.PostAsync($"/api/v1/push-requests/{push.Id}:reject", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict,
            (await f.Alice.Client.PostAsync($"/api/v1/push-requests/{push.Id}:accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await f.Alice.Client.PostAsync($"/api/v1/push-requests/{push.Id}:reject", null)).StatusCode);
    }

    [Fact]
    public async Task Pushes_are_audited_against_both_documents()
    {
        var f = await ForkedAsync();
        var reviewer = await CopyOnlyReviewerAsync(f);
        var (redlined, _) = await reviewer.Client.UploadAsync(f.CopyId, DocxFixtures.Edited());
        var push = await PushOkAsync(reviewer.Client, f.CopyId, f.MasterId, redlined);
        (await f.Alice.Client.PostAsync($"/api/v1/push-requests/{push.Id}:accept", null)).EnsureSuccessStatusCode();

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();

        var onTarget = await db.AuditEvents.Where(a => a.DocumentId == f.MasterId).Select(a => a.Action).ToListAsync();
        var onCopy = await db.AuditEvents.Where(a => a.DocumentId == f.CopyId).Select(a => a.Action).ToListAsync();

        Assert.Contains("push.requested", onTarget);
        Assert.Contains("push.accepted", onTarget);
        Assert.Contains("push.requested", onCopy);
        Assert.Contains("push.accepted", onCopy);
    }

    private async Task<Guid> CopyFirstVersionAsync(Guid copyId)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        return await db.Versions.Where(v => v.DocumentId == copyId)
            .OrderBy(v => v.CreatedAt).Select(v => v.Id).FirstAsync();
    }
}
