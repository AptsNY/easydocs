using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EasyDocs.Api.Tests.Conformance;

// E12 Security (spec §12.1): the §10/§11 role matrix is enforced per endpoint x role, and copies never
// leak master drafts (the copies clause is M4 — see E9).
//
// The five callers that matter:
//   Owner   — full control including membership
//   Editor  — may write content, may not manage membership or trash the document
//   Viewer  — may read only
//   Stranger— same org, NOT a document member  -> 403 (org role grants no implicit access, §11)
//   Outsider— another org                      -> 404 (never leak that the document exists)
[Collection(ConformanceCollection.Name)]
public class E12_Security
{
    private readonly ApiFactory _f;
    public E12_Security(ApiFactory f) => _f = f;

    private sealed record Cast(
        EdApi Owner, EdApi Editor, EdApi Viewer, EdApi Stranger, EdApi Outsider,
        Guid DocId, Guid VersionId, Guid PublishedVersionId);

    private async Task<Cast> CastAsync()
    {
        var owner = await EdApi.NewAsync(_f);
        var doc = await owner.CreateDocumentAsync("Role matrix");
        var v = await owner.UploadAsync(doc.Id, DocxFixtures.Base());
        var published = await owner.UploadAsync(doc.Id, DocxFixtures.Edited());
        await owner.PublishAsync(published.VersionId, "minor");

        var editor = await EdApi.ForSeededMemberAsync(_f, owner.OrgId);
        await owner.AddMemberAsync(doc.Id, editor.Email, "Editor");

        var viewer = await EdApi.ForSeededMemberAsync(_f, owner.OrgId);
        await owner.AddMemberAsync(doc.Id, viewer.Email, "Viewer");

        var stranger = await EdApi.ForSeededMemberAsync(_f, owner.OrgId); // in the org, not on the doc
        var outsider = await EdApi.NewAsync(_f);                          // different org entirely

        return new Cast(owner, editor, viewer, stranger, outsider, doc.Id, v.VersionId, published.VersionId);
    }

    private static void AssertOk(HttpResponseMessage res, string what) =>
        Assert.True(res.IsSuccessStatusCode, $"{what}: expected success, got {(int)res.StatusCode}");

    private static void AssertStatus(HttpStatusCode expected, HttpResponseMessage res, string what) =>
        Assert.True(res.StatusCode == expected, $"{what}: expected {(int)expected}, got {(int)res.StatusCode}");

    [Fact]
    public async Task Reads_are_allowed_for_every_member_and_denied_to_everyone_else()
    {
        var c = await CastAsync();

        foreach (var (api, name) in new[] { (c.Owner, "owner"), (c.Editor, "editor"), (c.Viewer, "viewer") })
        {
            AssertOk(await api.Http.GetAsync($"/api/v1/documents/{c.DocId}"), $"{name} GET document");
            AssertOk(await api.Http.GetAsync($"/api/v1/documents/{c.DocId}/versions"), $"{name} GET versions");
            AssertOk(await api.Http.GetAsync($"/api/v1/versions/{c.VersionId}"), $"{name} GET version");
            AssertOk(await api.DownloadRawAsync(c.VersionId), $"{name} download");
            AssertOk(await api.Http.GetAsync($"/api/v1/documents/{c.DocId}/publications"), $"{name} GET publications");
            AssertOk(await api.Http.GetAsync($"/api/v1/documents/{c.DocId}/audit"), $"{name} GET audit");
            AssertOk(await api.Http.GetAsync($"/api/v1/documents/{c.DocId}/members"), $"{name} GET members");
        }

        // Same org but not a member: 403 on every read.
        foreach (var path in new[]
        {
            $"/api/v1/documents/{c.DocId}", $"/api/v1/documents/{c.DocId}/versions",
            $"/api/v1/documents/{c.DocId}/audit", $"/api/v1/documents/{c.DocId}/members",
            $"/api/v1/documents/{c.DocId}/publications",
        })
            AssertStatus(HttpStatusCode.Forbidden, await c.Stranger.Http.GetAsync(path), $"stranger GET {path}");

        AssertStatus(HttpStatusCode.Forbidden, await c.Stranger.Http.GetAsync($"/api/v1/versions/{c.VersionId}"), "stranger GET version");

        // Another org: 404 everywhere — existence is not leaked.
        foreach (var path in new[]
        {
            $"/api/v1/documents/{c.DocId}", $"/api/v1/documents/{c.DocId}/versions",
            $"/api/v1/documents/{c.DocId}/audit", $"/api/v1/documents/{c.DocId}/members",
            $"/api/v1/documents/{c.DocId}/publications", $"/api/v1/versions/{c.VersionId}",
        })
            AssertStatus(HttpStatusCode.NotFound, await c.Outsider.Http.GetAsync(path), $"outsider GET {path}");
    }

    [Fact]
    public async Task Content_writes_need_editor_and_are_denied_to_viewer_stranger_outsider()
    {
        var c = await CastAsync();

        // Editor may write.
        AssertOk(await c.Editor.Http.PostAsync($"/api/v1/documents/{c.DocId}/versions", TestAuth.DocxForm(DocxFixtures.EditedPlusEcho())), "editor upload");
        AssertOk(await c.Editor.Http.PatchAsJsonAsync($"/api/v1/versions/{c.VersionId}", new { name = "by editor" }), "editor name version");
        AssertOk(await c.Editor.Http.PostAsync($"/api/v1/versions/{c.VersionId}/sessions", null), "editor mint session");

        // Viewer may not.
        AssertStatus(HttpStatusCode.Forbidden, await c.Viewer.Http.PostAsync($"/api/v1/documents/{c.DocId}/versions", TestAuth.DocxForm()), "viewer upload");
        AssertStatus(HttpStatusCode.Forbidden, await c.Viewer.Http.PostAsync($"/api/v1/documents/{c.DocId}/versions:import", TestAuth.DocxForm()), "viewer import");
        AssertStatus(HttpStatusCode.Forbidden, await c.Viewer.Http.PatchAsJsonAsync($"/api/v1/versions/{c.VersionId}", new { name = "nope" }), "viewer name version");
        AssertStatus(HttpStatusCode.Forbidden, await c.Viewer.Http.PostAsync($"/api/v1/versions/{c.VersionId}/revert", null), "viewer revert");
        AssertStatus(HttpStatusCode.Forbidden, await c.Viewer.PublishRawAsync(c.VersionId, "minor"), "viewer publish");
        AssertStatus(HttpStatusCode.Forbidden, await c.Viewer.Http.PostAsync($"/api/v1/versions/{c.VersionId}/sessions", null), "viewer mint session");
        AssertStatus(HttpStatusCode.Forbidden, await c.Viewer.Http.PatchAsJsonAsync($"/api/v1/documents/{c.DocId}", new { name = "nope" }), "viewer rename document");
        AssertStatus(HttpStatusCode.Forbidden, await c.Viewer.Http.PutAsJsonAsync($"/api/v1/documents/{c.DocId}/version-counter", new { major = 9, minor = 9, rev = 9 }), "viewer set counter");

        // Stranger (same org, no membership): 403.
        AssertStatus(HttpStatusCode.Forbidden, await c.Stranger.Http.PostAsync($"/api/v1/documents/{c.DocId}/versions", TestAuth.DocxForm()), "stranger upload");
        AssertStatus(HttpStatusCode.Forbidden, await c.Stranger.PublishRawAsync(c.VersionId, "minor"), "stranger publish");

        // Outsider (other org): 404.
        AssertStatus(HttpStatusCode.NotFound, await c.Outsider.Http.PostAsync($"/api/v1/documents/{c.DocId}/versions", TestAuth.DocxForm()), "outsider upload");
        AssertStatus(HttpStatusCode.NotFound, await c.Outsider.PublishRawAsync(c.VersionId, "minor"), "outsider publish");
    }

    [Fact]
    public async Task Membership_and_trash_are_owner_only()
    {
        var c = await CastAsync();
        var candidate = await EdApi.ForSeededMemberAsync(_f, c.Owner.OrgId);

        // Editor and Viewer may read the roster but not change it.
        foreach (var (api, name) in new[] { (c.Editor, "editor"), (c.Viewer, "viewer") })
        {
            AssertStatus(HttpStatusCode.Forbidden, await api.AddMemberRawAsync(c.DocId, candidate.Email, "Viewer"), $"{name} add member");
            AssertStatus(HttpStatusCode.Forbidden, await api.SetMemberRoleRawAsync(c.DocId, c.Viewer.UserId, "Owner"), $"{name} change role");
            AssertStatus(HttpStatusCode.Forbidden, await api.RemoveMemberRawAsync(c.DocId, c.Viewer.UserId), $"{name} remove member");
            AssertStatus(HttpStatusCode.Forbidden, await api.TrashDocumentRawAsync(c.DocId), $"{name} trash");
            AssertStatus(HttpStatusCode.Forbidden, await api.RestoreDocumentRawAsync(c.DocId), $"{name} restore");
        }

        // Nobody outside the document can touch membership.
        AssertStatus(HttpStatusCode.Forbidden, await c.Stranger.AddMemberRawAsync(c.DocId, candidate.Email, "Viewer"), "stranger add member");
        AssertStatus(HttpStatusCode.NotFound, await c.Outsider.AddMemberRawAsync(c.DocId, candidate.Email, "Viewer"), "outsider add member");

        // The owner can.
        AssertOk(await c.Owner.AddMemberRawAsync(c.DocId, candidate.Email, "Viewer"), "owner add member");
        AssertOk(await c.Owner.SetMemberRoleRawAsync(c.DocId, candidate.UserId, "Editor"), "owner change role");
        AssertOk(await c.Owner.RemoveMemberRawAsync(c.DocId, candidate.UserId), "owner remove member");
        AssertOk(await c.Owner.TrashDocumentRawAsync(c.DocId), "owner trash");
        AssertOk(await c.Owner.RestoreDocumentRawAsync(c.DocId), "owner restore");
    }

    [Fact]
    public async Task Only_the_named_approver_may_respond_regardless_of_role()
    {
        var c = await CastAsync();
        var row = (await c.Owner.RequestApprovalAsync(c.PublishedVersionId, [c.Viewer.UserId])).Single();

        // Even the owner and an editor cannot decide on someone else's behalf.
        AssertStatus(HttpStatusCode.Forbidden, await c.Owner.RespondRawAsync(row.Id, "approved"), "owner responds for viewer");
        AssertStatus(HttpStatusCode.Forbidden, await c.Editor.RespondRawAsync(row.Id, "approved"), "editor responds for viewer");
        AssertStatus(HttpStatusCode.Forbidden, await c.Outsider.RespondRawAsync(row.Id, "approved"), "outsider responds");

        // A Viewer *can* decide — approving is not a content write.
        AssertOk(await c.Viewer.RespondRawAsync(row.Id, "approved", "fine by me"), "named approver responds");
    }

    [Fact]
    public async Task An_org_role_grants_no_implicit_document_access()
    {
        // Spec §11: org owner/admin is for org/member management only. An org OWNER who is not a
        // document member still gets 403 — this is the clause most easily broken by a refactor.
        var owner = await EdApi.NewAsync(_f);
        var doc = await owner.CreateDocumentAsync("No implicit access");
        await owner.UploadAsync(doc.Id, DocxFixtures.Base());

        var orgOwnerAccount = await _f.SeedOrgUserAsync(owner.OrgId, EasyDocs.Api.Domain.OrgRole.Owner);
        var orgOwner = await _f.PatClientAsync(orgOwnerAccount.Client);

        AssertStatus(HttpStatusCode.Forbidden, await orgOwner.GetAsync($"/api/v1/documents/{doc.Id}"), "org owner GET document");
        AssertStatus(HttpStatusCode.Forbidden, await orgOwner.PostAsync($"/api/v1/documents/{doc.Id}/versions", TestAuth.DocxForm()), "org owner upload");
        AssertStatus(HttpStatusCode.Forbidden, await orgOwner.GetAsync($"/api/v1/documents/{doc.Id}/audit"), "org owner GET audit");
    }

    [Fact]
    public async Task Unauthenticated_requests_are_401_on_every_protected_route()
    {
        var c = await CastAsync();
        var anon = _f.CreateClient();

        foreach (var path in new[]
        {
            "/api/v1/me", "/api/v1/documents", "/api/v1/folders", "/api/v1/tokens",
            $"/api/v1/documents/{c.DocId}", $"/api/v1/documents/{c.DocId}/audit",
            $"/api/v1/documents/{c.DocId}/members", $"/api/v1/versions/{c.VersionId}",
        })
            AssertStatus(HttpStatusCode.Unauthorized, await anon.GetAsync(path), $"anonymous GET {path}");

        AssertStatus(HttpStatusCode.Unauthorized, await anon.PostAsync($"/api/v1/documents/{c.DocId}/versions", TestAuth.DocxForm()), "anonymous upload");
    }

    [Fact]
    public async Task A_revoked_or_expired_token_stops_working()
    {
        var owner = await EdApi.NewAsync(_f);
        var doc = await owner.CreateDocumentAsync("Token lifecycle");

        // A fresh PAT works...
        AssertOk(await owner.Http.GetAsync($"/api/v1/documents/{doc.Id}"), "live PAT");

        // ...until it is revoked, after which it is 401 rather than a lingering credential.
        var tokens = await owner.Http.GetFromJsonAsync<TokenRow[]>("/api/v1/tokens");
        foreach (var t in tokens!)
            (await owner.Http.DeleteAsync($"/api/v1/tokens/{t.Id}")).EnsureSuccessStatusCode();

        AssertStatus(HttpStatusCode.Unauthorized, await owner.Http.GetAsync($"/api/v1/documents/{doc.Id}"), "revoked PAT");
    }

    private record TokenRow(Guid Id);

    [Fact]
    public async Task A_tokens_reach_never_exceeds_its_owners_document_role()
    {
        // Spec §10: "a token never exceeds its owner's document role". Demote the owner of a live PAT
        // and the same credential must lose the privilege immediately.
        var owner = await EdApi.NewAsync(_f);
        var doc = await owner.CreateDocumentAsync("Token follows role");
        await owner.UploadAsync(doc.Id, DocxFixtures.Base());

        var editor = await EdApi.ForSeededMemberAsync(_f, owner.OrgId);
        await owner.AddMemberAsync(doc.Id, editor.Email, "Editor");

        AssertOk(await editor.Http.PostAsync($"/api/v1/documents/{doc.Id}/versions", TestAuth.DocxForm(DocxFixtures.Edited())), "editor PAT writes");

        (await owner.SetMemberRoleRawAsync(doc.Id, editor.UserId, "Viewer")).EnsureSuccessStatusCode();

        AssertStatus(HttpStatusCode.Forbidden,
            await editor.Http.PostAsync($"/api/v1/documents/{doc.Id}/versions", TestAuth.DocxForm(DocxFixtures.EditedPlusEcho())),
            "demoted PAT writes");
        AssertOk(await editor.Http.GetAsync($"/api/v1/documents/{doc.Id}"), "demoted PAT still reads");
    }

    [Fact]
    public async Task Capability_tokens_are_hashed_at_rest()
    {
        // Spec §11: share/WOPI/invitation tokens are capability tokens — never stored in the clear.
        var owner = await EdApi.NewAsync(_f);
        var (docId, vid) = await owner.NewDocumentWithBaseAsync("Hashed secrets");
        var share = await owner.CreateShareLinkAsync(vid);
        var invited = $"invitee-{Guid.NewGuid():N}@example.com";
        var inviteRes = await owner.AddMemberRawAsync(docId, invited, "Viewer");
        inviteRes.EnsureSuccessStatusCode();
        var inviteToken = (await inviteRes.Content.ReadFromJsonAsync<InviteRow>())!.InvitationToken;

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();

        Assert.False(await db.ShareLinks.AnyAsync(s => s.TokenHash == share.Token));
        Assert.False(await db.Invitations.AnyAsync(i => i.TokenHash == inviteToken));
        Assert.True(await db.ShareLinks.AnyAsync(s => s.VersionId == vid));      // it IS stored, hashed
        Assert.True(await db.Invitations.AnyAsync(i => i.Email == invited));

        // Tokens are hex SHA-256 at rest, never the raw value.
        var storedShare = await db.ShareLinks.Where(s => s.VersionId == vid).Select(s => s.TokenHash).SingleAsync();
        Assert.Equal(64, storedShare.Length);
        Assert.DoesNotContain(share.Token, storedShare);
    }

    private record InviteRow(string Email, string Role, string InvitationToken);

    [Fact]
    public async Task Errors_are_rfc7807_problem_json()
    {
        var c = await CastAsync();

        foreach (var res in new[]
        {
            await c.Outsider.Http.GetAsync($"/api/v1/documents/{c.DocId}"),          // 404
            await c.Stranger.Http.GetAsync($"/api/v1/documents/{c.DocId}"),          // 403
            await c.Viewer.PublishRawAsync(c.VersionId, "minor"),                   // 403
            await c.Owner.PublishRawAsync(c.VersionId, "sideways"),                 // 400
            await c.Owner.Http.GetAsync($"/api/v1/versions/{Guid.NewGuid()}"),      // 404
        })
        {
            Assert.False(res.IsSuccessStatusCode);
            Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
            var body = await res.Content.ReadFromJsonAsync<ProblemShape>();
            Assert.False(string.IsNullOrWhiteSpace(body!.Title));
            Assert.True(body.Status >= 400);
        }
    }

    private record ProblemShape(string? Title, int Status, string? Detail);

    // ---- Endpoints the matrix above never reached (M5 gap close) ----
    //
    // "per endpoint x role" is only true of the endpoints actually named. These five Facts cover the
    // §10.1 routes that had no role assertion anywhere in the suite: the folder routes, compare,
    // share-link revocation, approval *requests*, and the version-scoped mutations that E12 previously
    // exercised only through publish and upload.

    [Fact]
    public async Task Folder_routes_are_org_scoped_and_leak_nothing_across_the_boundary()
    {
        // Folders are the one §10.1 group with no document-membership chokepoint — they authorize on
        // the session's org id alone, which is exactly the shape that a refactor drops silently.
        var owner = await EdApi.NewAsync(_f);
        var outsider = await EdApi.NewAsync(_f);
        var folder = await owner.CreateFolderAsync($"Leases {Guid.NewGuid():N}");

        Assert.DoesNotContain(await outsider.ListFoldersAsync(), f => f.Id == folder.Id);
        Assert.Empty(await outsider.ListFoldersAsync(folder.Id)); // asking under it reveals nothing

        AssertStatus(HttpStatusCode.NotFound,
            await outsider.Http.PatchAsJsonAsync($"/api/v1/folders/{folder.Id}", new { name = "seized" }), "outsider renames folder");
        AssertStatus(HttpStatusCode.NotFound, await outsider.DeleteFolderRawAsync(folder.Id, "trash"), "outsider deletes folder");

        // Nor can it be adopted as a parent: 400 "not in your org", never a 403 that would confirm it exists.
        AssertStatus(HttpStatusCode.BadRequest,
            await outsider.Http.PostAsJsonAsync("/api/v1/folders", new { name = "Child", parentId = folder.Id }),
            "outsider creates under a foreign parent");

        Assert.Equal(folder.Name, (await owner.ListFoldersAsync()).Single(f => f.Id == folder.Id).Name);
    }

    [Fact]
    public async Task Compare_is_a_read_open_to_every_member_and_shut_to_everyone_else()
    {
        var c = await CastAsync();
        var url = $"/api/v1/documents/{c.DocId}/compare?from={c.VersionId}&to={c.PublishedVersionId}&format=summary";

        foreach (var (api, name) in new[] { (c.Owner, "owner"), (c.Editor, "editor"), (c.Viewer, "viewer") })
            AssertOk(await api.Http.GetAsync(url), $"{name} compare");

        AssertStatus(HttpStatusCode.Forbidden, await c.Stranger.Http.GetAsync(url), "stranger compare");
        AssertStatus(HttpStatusCode.NotFound, await c.Outsider.Http.GetAsync(url), "outsider compare");
    }

    [Fact]
    public async Task Revoking_a_share_link_belongs_to_its_creator_or_to_an_editor()
    {
        // Creating a link is Viewer+ (E10), so revoking cannot be: a Viewer who may share must be able
        // to unshare their own link, and must not be able to pull down someone else's.
        var c = await CastAsync();
        await c.Viewer.CreateShareLinkAsync(c.VersionId);
        await c.Owner.CreateShareLinkAsync(c.VersionId);

        var rows = (await c.Owner.ListShareLinksAsync(c.DocId)).Items;
        var viewersLink = rows.Single(r => r.CreatedBy == c.Viewer.UserId).Id;
        var ownersLink = rows.Single(r => r.CreatedBy == c.Owner.UserId).Id;

        AssertStatus(HttpStatusCode.Forbidden, await c.Viewer.RevokeShareLinkRawAsync(ownersLink), "viewer revokes the owner's link");
        AssertStatus(HttpStatusCode.Forbidden, await c.Stranger.RevokeShareLinkRawAsync(ownersLink), "stranger revokes");
        AssertStatus(HttpStatusCode.NotFound, await c.Outsider.RevokeShareLinkRawAsync(ownersLink), "outsider revokes");

        AssertOk(await c.Viewer.RevokeShareLinkRawAsync(viewersLink), "viewer revokes their own link");
        AssertOk(await c.Editor.RevokeShareLinkRawAsync(ownersLink), "editor revokes another member's link");
    }

    [Fact]
    public async Task Requesting_an_approval_is_a_write_and_needs_editor()
    {
        // E7 pins who may *decide*; nothing pinned who may *ask*. A Viewer who could raise an approval
        // would be putting a decision obligation on the roster from a read-only seat.
        var c = await CastAsync();
        Guid[] approver = [c.Viewer.UserId];

        AssertStatus(HttpStatusCode.Forbidden, await c.Viewer.RequestApprovalRawAsync(c.PublishedVersionId, approver), "viewer requests approval");
        AssertStatus(HttpStatusCode.Forbidden, await c.Stranger.RequestApprovalRawAsync(c.PublishedVersionId, approver), "stranger requests approval");
        AssertStatus(HttpStatusCode.NotFound, await c.Outsider.RequestApprovalRawAsync(c.PublishedVersionId, approver), "outsider requests approval");
        AssertOk(await c.Editor.RequestApprovalRawAsync(c.PublishedVersionId, approver), "editor requests approval");
    }

    [Fact]
    public async Task Every_version_and_document_scoped_route_is_404_for_another_org()
    {
        // The existence-leak rule is a property of the whole surface, not of the two routes E12 happened
        // to name. Each of these resolves the document through the chokepoint, so each must 404.
        var c = await CastAsync();
        var o = c.Outsider.Http;

        AssertStatus(HttpStatusCode.NotFound, await o.GetAsync($"/api/v1/versions/{c.VersionId}/download"), "outsider download");
        AssertStatus(HttpStatusCode.NotFound, await o.PatchAsJsonAsync($"/api/v1/versions/{c.VersionId}", new { name = "seized" }), "outsider names version");
        AssertStatus(HttpStatusCode.NotFound, await o.PostAsync($"/api/v1/versions/{c.VersionId}/revert", null), "outsider reverts");
        AssertStatus(HttpStatusCode.NotFound, await o.PostAsync($"/api/v1/versions/{c.VersionId}/sessions", null), "outsider mints a session");
        AssertStatus(HttpStatusCode.NotFound, await o.PostAsJsonAsync($"/api/v1/versions/{c.VersionId}/share-links", new { }), "outsider shares");
        AssertStatus(HttpStatusCode.NotFound, await o.GetAsync($"/api/v1/versions/{c.VersionId}/approvals"), "outsider reads the approvals panel");
        AssertStatus(HttpStatusCode.NotFound, await c.Outsider.ForkRawAsync(c.VersionId, "seized copy"), "outsider forks a copy");
        AssertStatus(HttpStatusCode.NotFound, await c.Outsider.MergeRawAsync(c.DocId, c.VersionId, c.PublishedVersionId), "outsider merges");
        AssertStatus(HttpStatusCode.NotFound, await o.GetAsync($"/api/v1/documents/{c.DocId}/share-links"), "outsider lists share links");
        AssertStatus(HttpStatusCode.NotFound, await o.GetAsync($"/api/v1/documents/{c.DocId}/copies"), "outsider lists copies");
        AssertStatus(HttpStatusCode.NotFound, await o.GetAsync($"/api/v1/documents/{c.DocId}/push-requests"), "outsider lists push requests");
        AssertStatus(HttpStatusCode.NotFound, await o.GetAsync($"/api/v1/documents/{c.DocId}/events"), "outsider opens the event stream");
    }

    [Fact]
    public async Task The_approvals_inbox_and_a_decision_both_stop_at_removal_from_the_document()
    {
        // ApprovalEndpoints scopes the inbox to documents the caller is STILL a member of, and Respond
        // re-runs the chokepoint after matching the approver id. Both halves matter: the row must not
        // carry a document name to someone who lost access, and being *named* is not itself access.
        var c = await CastAsync();
        var row = (await c.Owner.RequestApprovalAsync(c.PublishedVersionId, [c.Viewer.UserId])).Single();
        Assert.Contains((await c.Viewer.ListApprovalsAsync("assigned")).Items, i => i.Id == row.Id);

        (await c.Owner.RemoveMemberRawAsync(c.DocId, c.Viewer.UserId)).EnsureSuccessStatusCode();

        Assert.DoesNotContain((await c.Viewer.ListApprovalsAsync("assigned")).Items, i => i.Id == row.Id);
        AssertStatus(HttpStatusCode.Forbidden, await c.Viewer.RespondRawAsync(row.Id, "approved"), "removed approver responds");
    }
}
