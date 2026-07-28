using System.Net;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests.Conformance;

// E7 Approvals (spec §12.1): only on published versions; one row per approver with a due date;
// a SINGLE decision + comment (no thread); decisions are immutable; cancel closes the request.
[Collection(ConformanceCollection.Name)]
public class E07_Approvals
{
    private readonly ApiFactory _f;
    public E07_Approvals(ApiFactory f) => _f = f;

    // Owner + a published version + two approvers who are members of the document.
    private async Task<(EdApi Owner, Guid DocId, Guid Vid, EdApi A1, EdApi A2)> PublishedWithApproversAsync()
    {
        var owner = await EdApi.NewAsync(_f);
        var (docId, vid) = await owner.NewDocumentWithBaseAsync("Needs sign-off");

        var a1 = await EdApi.ForSeededMemberAsync(_f, owner.OrgId);
        await owner.AddMemberAsync(docId, a1.Email, "Editor");
        var a2 = await EdApi.ForSeededMemberAsync(_f, owner.OrgId);
        await owner.AddMemberAsync(docId, a2.Email, "Viewer");

        await owner.PublishAsync(vid, "minor");
        return (owner, docId, vid, a1, a2);
    }

    [Fact]
    public async Task Approvals_cannot_be_requested_on_an_unpublished_version()
    {
        var owner = await EdApi.NewAsync(_f);
        var (docId, vid) = await owner.NewDocumentWithBaseAsync("Draft only");
        var approver = await EdApi.ForSeededMemberAsync(_f, owner.OrgId);
        await owner.AddMemberAsync(docId, approver.Email, "Editor");

        var res = await owner.RequestApprovalRawAsync(vid, [approver.UserId]);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("published", await res.Content.ReadAsStringAsync());

        // Publishing unlocks it.
        await owner.PublishAsync(vid, "minor");
        Assert.Equal(HttpStatusCode.Created, (await owner.RequestApprovalRawAsync(vid, [approver.UserId])).StatusCode);
    }

    [Fact]
    public async Task One_row_per_approver_each_carrying_the_due_date()
    {
        var (owner, _, vid, a1, a2) = await PublishedWithApproversAsync();
        var due = DateTimeOffset.UtcNow.AddDays(3);

        var rows = await owner.RequestApprovalAsync(vid, [a1.UserId, a2.UserId], due);

        Assert.Equal(2, rows.Length);
        Assert.Equal(
            new[] { a1.UserId, a2.UserId }.OrderBy(x => x).ToArray(),
            rows.Select(r => r.ApproverId).OrderBy(x => x).ToArray());
        Assert.All(rows, r => Assert.Equal(vid, r.VersionId));
        Assert.All(rows, r => Assert.NotNull(r.DueAt));
        Assert.All(rows, r => Assert.Equal(due.ToUnixTimeSeconds(), r.DueAt!.Value.ToUnixTimeSeconds()));
        Assert.Equal(2, rows.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task Duplicate_approver_ids_collapse_to_one_row()
    {
        var (owner, _, vid, a1, _) = await PublishedWithApproversAsync();

        var rows = await owner.RequestApprovalAsync(vid, [a1.UserId, a1.UserId, a1.UserId]);

        Assert.Single(rows);
    }

    [Fact]
    public async Task An_empty_approver_list_is_rejected()
    {
        var (owner, _, vid, _, _) = await PublishedWithApproversAsync();

        var res = await owner.RequestApprovalRawAsync(vid, []);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Only_the_named_approver_may_respond_and_the_decision_is_immutable()
    {
        var (owner, _, vid, a1, a2) = await PublishedWithApproversAsync();
        var row = (await owner.RequestApprovalAsync(vid, [a1.UserId])).Single();

        // Not the approver — not even the owner who asked.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.RespondRawAsync(row.Id, "approved")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a2.RespondRawAsync(row.Id, "approved")).StatusCode);

        // The named approver decides once, with a comment.
        var ok = await a1.RespondRawAsync(row.Id, "approved", "Looks good to me");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadAsStringAsync();
        Assert.Contains("approved", body);
        Assert.Contains("Looks good to me", body);

        // Immutable: no second decision, no reversal — and no thread to append to.
        Assert.Equal(HttpStatusCode.Conflict, (await a1.RespondRawAsync(row.Id, "rejected", "changed my mind")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a1.RespondRawAsync(row.Id, "approved", "again")).StatusCode);
    }

    [Fact]
    public async Task A_rejection_is_recorded_the_same_way()
    {
        var (owner, _, vid, a1, _) = await PublishedWithApproversAsync();
        var row = (await owner.RequestApprovalAsync(vid, [a1.UserId])).Single();

        var res = await a1.RespondRawAsync(row.Id, "rejected", "Section 4 is wrong");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("rejected", await res.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, (await a1.RespondRawAsync(row.Id, "approved")).StatusCode);
    }

    [Fact]
    public async Task An_invalid_decision_is_rejected()
    {
        var (owner, _, vid, a1, _) = await PublishedWithApproversAsync();
        var row = (await owner.RequestApprovalAsync(vid, [a1.UserId])).Single();

        Assert.Equal(HttpStatusCode.BadRequest, (await a1.RespondRawAsync(row.Id, "maybe")).StatusCode);
    }

    [Fact]
    public async Task Cancel_closes_the_request_and_blocks_a_later_decision()
    {
        var (owner, _, vid, a1, _) = await PublishedWithApproversAsync();
        var row = (await owner.RequestApprovalAsync(vid, [a1.UserId])).Single();

        Assert.Equal(HttpStatusCode.OK, (await owner.CancelApprovalRawAsync(row.Id)).StatusCode);

        // Closed: the approver can no longer decide, and it cannot be cancelled twice.
        Assert.Equal(HttpStatusCode.Conflict, (await a1.RespondRawAsync(row.Id, "approved")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.CancelApprovalRawAsync(row.Id)).StatusCode);
    }

    [Fact]
    public async Task A_decided_request_cannot_be_cancelled()
    {
        var (owner, _, vid, a1, _) = await PublishedWithApproversAsync();
        var row = (await owner.RequestApprovalAsync(vid, [a1.UserId])).Single();

        (await a1.RespondRawAsync(row.Id, "approved")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict, (await owner.CancelApprovalRawAsync(row.Id)).StatusCode);
    }

    [Fact]
    public async Task Approval_activity_is_audited()
    {
        var (owner, docId, vid, a1, _) = await PublishedWithApproversAsync();
        var row = (await owner.RequestApprovalAsync(vid, [a1.UserId])).Single();
        (await a1.RespondRawAsync(row.Id, "approved", "fine")).EnsureSuccessStatusCode();

        var actions = await owner.AuditActionsAsync(docId);
        Assert.Contains("approval.requested", actions);
        Assert.Contains("approval.responded", actions);
    }
}
