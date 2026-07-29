using System.Net.Http.Json;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests;

// The approvals screen (spec §9) needs to READ approvals. M0-M4 shipped request/respond/cancel and
// no GET at all, so an approver had no way to learn they had been asked for anything.
public class ApprovalReadTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ApprovalReadTests(ApiFactory f) => _f = f;

    private record Item(
        Guid Id, Guid VersionId, Guid DocumentId, string DocumentName, string VersionNumber,
        Guid ApproverId, string ApproverName, Guid RequestedBy, string RequestedByName,
        string? Decision, string? DecisionComment, DateTimeOffset? DueAt,
        DateTimeOffset? DecidedAt, DateTimeOffset? CancelledAt, string Status,
        DateTimeOffset CreatedAt);
    private record Page(Item[] Items, string? NextCursor);
    private record CreatedApproval(Guid Id);

    private async Task<(Account Owner, Account Approver, Guid DocId, Guid Vid, Guid ApprovalId)> SeedAsync()
    {
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync("Needs sign-off");
        var (vid, _) = await owner.Client.UploadAsync(docId, DocxFixtures.Base());
        (await owner.Client.PostAsJsonAsync($"/api/v1/versions/{vid}/publish", new { kind = "major" }))
            .EnsureSuccessStatusCode();

        var approver = await _f.SeedOrgUserAsync(owner.OrgId);
        (await owner.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email = approver.Email, role = "Viewer" })).EnsureSuccessStatusCode();

        var created = await owner.Client.PostAsJsonAsync(
            $"/api/v1/versions/{vid}/approvals",
            new { approverIds = new[] { approver.UserId }, dueAt = DateTimeOffset.UtcNow.AddDays(3) });
        created.EnsureSuccessStatusCode();
        var rows = await created.Content.ReadFromJsonAsync<CreatedApproval[]>();

        return (owner, approver, docId, vid, rows![0].Id);
    }

    [Fact]
    public async Task An_approver_can_find_what_they_have_been_asked_to_approve()
    {
        var (_, approver, docId, vid, approvalId) = await SeedAsync();

        var page = await approver.Client.GetFromJsonAsync<Page>("/api/v1/approvals?filter=assigned");
        var item = page!.Items.Single(i => i.Id == approvalId);

        Assert.Equal(vid, item.VersionId);
        Assert.Equal(docId, item.DocumentId);
        Assert.Equal("Needs sign-off", item.DocumentName);  // renderable without a second request
        Assert.Equal("1.0.0", item.VersionNumber);
        Assert.Equal("open", item.Status);
        Assert.NotNull(item.DueAt);
        Assert.Equal("U", item.RequestedByName);
    }

    [Fact]
    public async Task A_requester_can_track_what_they_asked_for()
    {
        var (owner, _, _, _, approvalId) = await SeedAsync();

        var page = await owner.Client.GetFromJsonAsync<Page>("/api/v1/approvals?filter=requested");
        Assert.Contains(page!.Items, i => i.Id == approvalId);

        // ...and does NOT see it under `assigned` — they are not the approver.
        var assigned = await owner.Client.GetFromJsonAsync<Page>("/api/v1/approvals?filter=assigned");
        Assert.DoesNotContain(assigned!.Items, i => i.Id == approvalId);
    }

    [Fact]
    public async Task Status_reflects_the_decision_and_open_filters_it_out()
    {
        var (_, approver, _, _, approvalId) = await SeedAsync();

        (await approver.Client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}:respond",
            new { decision = "approved", comment = "Looks right" })).EnsureSuccessStatusCode();

        var all = await approver.Client.GetFromJsonAsync<Page>("/api/v1/approvals?filter=assigned");
        var item = all!.Items.Single(i => i.Id == approvalId);
        Assert.Equal("approved", item.Status);
        Assert.Equal("Looks right", item.DecisionComment);
        Assert.NotNull(item.DecidedAt);

        var open = await approver.Client.GetFromJsonAsync<Page>("/api/v1/approvals?filter=assigned&status=open");
        Assert.DoesNotContain(open!.Items, i => i.Id == approvalId);
    }

    [Fact]
    public async Task The_per_version_list_is_readable_by_any_document_member()
    {
        var (_, approver, _, vid, approvalId) = await SeedAsync();

        // The approver is only a Viewer — the panel is a read, so Viewer is enough.
        var rows = await approver.Client.GetFromJsonAsync<Item[]>($"/api/v1/versions/{vid}/approvals");
        Assert.Contains(rows!, i => i.Id == approvalId);
    }

    [Fact]
    public async Task Approvals_never_cross_an_org_boundary()
    {
        var (_, _, _, vid, approvalId) = await SeedAsync();
        var outsider = await _f.RegisterAsync(); // own org

        var page = await outsider.Client.GetFromJsonAsync<Page>("/api/v1/approvals?filter=assigned");
        Assert.DoesNotContain(page!.Items, i => i.Id == approvalId);

        // And the per-version list is a 404, not a 403 — no existence leak (§11).
        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await outsider.Client.GetAsync($"/api/v1/versions/{vid}/approvals")).StatusCode);
    }
}
