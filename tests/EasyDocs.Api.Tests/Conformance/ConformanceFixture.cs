using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Tests;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests.Conformance;

// The v1 conformance profile (spec §12.1, E1-E12). Every criterion is driven through the public REST
// API with an `ed_` personal access token — no DbContext shortcuts for the *actions*, so a green suite
// is a direct proof of the M3 exit gate ("the API drives the full document flow unattended").
//
// One documented deviation:
//   * Criteria that assert internal state the API deliberately does not expose (branch closure in E4)
//     read the DB to *verify*. The actions that produced that state still go through the API.
//
// E3/E4 still play Collabora's side of the WOPI contract here (LOCK + PutFile against /wopi/files/{sid})
// because that is the fastest way to assert the protocol's branches. It is NO LONGER a substitute for
// spec §12.3's headless-browser driver, and must never be described as one again: for four milestones
// this suite was green while no browser could open a document at all, because it deserialises
// CheckFileInfo case-insensitively and the real host emitted camelCase. The browser driver now lives in
// web/e2e/collabora.spec.ts, drives the real Collabora editor, and is the thing that proves E3.
[CollectionDefinition(Name)]
public class ConformanceCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "conformance";
}

public record FolderDto(Guid Id, string Name, Guid? ParentId);
public record DocumentDto(Guid Id, string Name, Guid? FolderId);
public record VersionRefDto(Guid VersionId, int Major, int Minor, int Revision);
public record VersionDto(
    Guid Id, Guid DocumentId, int Major, int Minor, int Revision, string? Name, string Source,
    string? PublishedKind, DateTimeOffset? PublishedAt, string? PublishName, bool HasPdf,
    Guid? ParentVersionId, DateTimeOffset CreatedAt, Guid CreatedBy);
// The nested per-row diff summary (Task 2: E3 "list shows summary"). Null until DiffSummaryWorker
// has drained the job for that row's parent->child pair.
public record ChangeSummaryDto(int Insertions, int Deletions, int Moves, int FormatChanges);

// Task 1 (spec §9): branch identity, publish state, resolved names and ordering, so a criterion can
// assert on the console row the SPA actually renders, not just the bare version tuple. Existing fields
// keep their original positions; the new ones are appended so no positional construction elsewhere breaks.
public record VersionListItem(
    Guid Id, int Major, int Minor, int Revision, string Source, DateTimeOffset CreatedAt, Guid CreatedBy,
    string Number, string? Name, string? PublishedKind, DateTimeOffset? PublishedAt, string? PublishName,
    bool HasPdf, Guid? ParentVersionId, Guid BranchId, string BranchKind, int BranchOrdinal,
    Guid? BranchMergedIntoVersionId, string CreatedByName, ChangeSummaryDto? Summary);
public record VersionListDto(VersionListItem[] Items, string? NextCursor);
public record PublicationItem(Guid VersionId, int Major, int Minor, int Revision, string? Name, Guid PublishedBy, DateTimeOffset PublishedAt, string Kind);
public record PublicationListDto(PublicationItem[] Items, string? NextCursor);
public record SummaryDto(int Insertions, int Deletions, int Moves, int FormatChanges);
public record SessionDto(Guid SessionId, string EditorUrl, string AccessToken, int AccessTokenTtlSeconds);
public record ApprovalDto(Guid Id, Guid VersionId, Guid ApproverId, DateTimeOffset? DueAt);
public record ShareLinkDto(string Token, string Url);
// M5: one row of GET /documents/{id}/share-links — the list that makes the row id, and therefore
// revocation, reachable without a database. Carries no token and no hash.
public record ShareLinkRowDto(
    Guid Id, Guid VersionId, string VersionNumber, Guid CreatedBy, string CreatedByName,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, int ViewCount);
public record ShareLinkListDto(ShareLinkRowDto[] Items, string? NextCursor);
public record MemberDto(Guid UserId, string Email, string DisplayName, string Role);
public record AuditItemDto(Guid Id, string Action, Guid? ActorUserId, string? TargetType, string? TargetId, string? Metadata, DateTimeOffset CreatedAt);
public record AuditListDto(AuditItemDto[] Items, string? NextCursor);
public record PublishedDto(Guid VersionId, int Major, int Minor, int Revision, string Kind);
public record CopyDto(Guid Id, string Name, Guid ParentDocumentId, Guid ForkedFromVersionId, Guid? VersionId);
public record PushRequestDto(
    Guid Id, string Status, Guid CopyDocumentId, Guid TargetDocumentId, Guid SourceVersionId,
    Guid? MaterializedVersionId, Guid PushedBy, DateTimeOffset? DecidedAt);

// Task 4 (spec §9): one dashboard tile / trash row.
public record DocumentTileDto(
    Guid Id, string Name, Guid? FolderId, string? CurrentNumber, int VersionCount,
    DateTimeOffset? UpdatedAt, string? LastAuthorName, DateTimeOffset? DeletedAt);
public record DocumentTileListDto(DocumentTileDto[] Items, string? NextCursor);

// Task 5 (spec §9): one approvals-inbox row, resolved names and derived status included.
public record ApprovalListItemDto(
    Guid Id, Guid VersionId, Guid DocumentId, string DocumentName, string VersionNumber,
    Guid ApproverId, string ApproverName, Guid RequestedBy, string RequestedByName,
    string? Decision, string? DecisionComment, DateTimeOffset? DueAt,
    DateTimeOffset? DecidedAt, DateTimeOffset? CancelledAt, string Status, DateTimeOffset CreatedAt);
public record ApprovalListDto(ApprovalListItemDto[] Items, string? NextCursor);

// Task 6 (spec §9 settings screen).
public record OrgDto(Guid Id, string Name, string Slug, string MyRole);
public record OrgMemberDto(Guid UserId, string Email, string DisplayName, string Role, DateTimeOffset CreatedAt);
public record OrgInviteDto(string Email, string Role, string InvitationToken);

// A typed client over the public v1 surface, authenticated with an `ed_` PAT. Methods that a criterion
// expects to succeed throw on failure; `Http` is exposed for the negative/role-matrix assertions.
public sealed class EdApi
{
    public HttpClient Http { get; }
    public Guid UserId { get; }
    public Guid OrgId { get; }
    public string Email { get; }

    private EdApi(HttpClient http, Guid userId, Guid orgId, string email)
    {
        Http = http;
        UserId = userId;
        OrgId = orgId;
        Email = email;
    }

    // Registers a fresh org and returns a client that talks to it using only an ed_ PAT.
    public static async Task<EdApi> NewAsync(ApiFactory f, string? email = null)
    {
        var account = await f.RegisterAsync(email);
        var pat = await f.PatClientAsync(account.Client);
        return new EdApi(pat, account.UserId, account.OrgId, account.Email);
    }

    // A PAT client for an existing member of this org (used by E12's role matrix).
    public static async Task<EdApi> ForSeededMemberAsync(ApiFactory f, Guid orgId)
    {
        var account = await f.SeedOrgUserAsync(orgId);
        var pat = await f.PatClientAsync(account.Client);
        return new EdApi(pat, account.UserId, account.OrgId, account.Email);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage res)
    {
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)res.StatusCode} {res.StatusCode} for {res.RequestMessage?.RequestUri}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    // ---- Folders (§10.1) ----
    public async Task<FolderDto> CreateFolderAsync(string name, Guid? parentId = null) =>
        await ReadAsync<FolderDto>(await Http.PostAsJsonAsync("/api/v1/folders", new { name, parentId }));

    public async Task<FolderDto[]> ListFoldersAsync(Guid? parentId = null) =>
        await ReadAsync<FolderDto[]>(await Http.GetAsync(parentId is null ? "/api/v1/folders" : $"/api/v1/folders?parentId={parentId}"));

    public Task<HttpResponseMessage> DeleteFolderRawAsync(Guid id, string? mode = null) =>
        Http.DeleteAsync(mode is null ? $"/api/v1/folders/{id}" : $"/api/v1/folders/{id}?mode={mode}");

    // ---- Documents (§10.1) ----
    public async Task<DocumentDto> CreateDocumentAsync(string name, Guid? folderId = null) =>
        await ReadAsync<DocumentDto>(await Http.PostAsJsonAsync("/api/v1/documents", new { name, folderId }));

    public async Task<DocumentDto> GetDocumentAsync(Guid id) =>
        await ReadAsync<DocumentDto>(await Http.GetAsync($"/api/v1/documents/{id}"));

    public async Task<DocumentDto> MoveDocumentAsync(Guid id, Guid folderId) =>
        await ReadAsync<DocumentDto>(await Http.PatchAsJsonAsync($"/api/v1/documents/{id}", new { folderId }));

    public async Task<DocumentDto> RenameDocumentAsync(Guid id, string name) =>
        await ReadAsync<DocumentDto>(await Http.PatchAsJsonAsync($"/api/v1/documents/{id}", new { name }));

    public Task<HttpResponseMessage> TrashDocumentRawAsync(Guid id) => Http.DeleteAsync($"/api/v1/documents/{id}");
    public Task<HttpResponseMessage> RestoreDocumentRawAsync(Guid id) => Http.PostAsync($"/api/v1/documents/{id}:restore", null);

    // Task 4: `?trashed=true` swaps the dashboard's DeletedAt filter so the SPA's trash view can reach
    // :restore.
    public async Task<DocumentTileListDto> ListTrashAsync() =>
        await ReadAsync<DocumentTileListDto>(await Http.GetAsync("/api/v1/documents?trashed=true"));

    // ---- Versions (§10.1, §10.3 multipart ingest) ----
    public async Task<VersionRefDto> UploadAsync(Guid docId, byte[]? bytes = null, string fileName = "doc.docx") =>
        await ReadAsync<VersionRefDto>(await Http.PostAsync($"/api/v1/documents/{docId}/versions", TestAuth.DocxForm(bytes, fileName)));

    public async Task<VersionRefDto> ImportAsync(Guid docId, byte[]? bytes = null) =>
        await ReadAsync<VersionRefDto>(await Http.PostAsync($"/api/v1/documents/{docId}/versions:import", TestAuth.DocxForm(bytes)));

    // `order=desc` (Task 1) is opt-in; omitted, the API keeps its ascending default.
    public async Task<VersionListDto> ListVersionsAsync(Guid docId, int? limit = 100, string? order = null) =>
        await ReadAsync<VersionListDto>(await Http.GetAsync(
            $"/api/v1/documents/{docId}/versions?limit={limit}" + (order is null ? "" : $"&order={order}")));

    public async Task<VersionDto> GetVersionAsync(Guid vid) =>
        await ReadAsync<VersionDto>(await Http.GetAsync($"/api/v1/versions/{vid}"));

    public async Task<VersionDto> NameVersionAsync(Guid vid, string name)
    {
        var res = await Http.PatchAsJsonAsync($"/api/v1/versions/{vid}", new { name });
        res.EnsureSuccessStatusCode();
        return await GetVersionAsync(vid);
    }

    public async Task<VersionRefDto> RevertAsync(Guid vid) =>
        await ReadAsync<VersionRefDto>(await Http.PostAsync($"/api/v1/versions/{vid}/revert", null));

    public async Task SetCounterAsync(Guid docId, int major, int minor, int rev) =>
        (await Http.PutAsJsonAsync($"/api/v1/documents/{docId}/version-counter", new { major, minor, rev }))
            .EnsureSuccessStatusCode();

    public Task<HttpResponseMessage> DownloadRawAsync(Guid vid, string? format = null) =>
        Http.GetAsync(format is null ? $"/api/v1/versions/{vid}/download" : $"/api/v1/versions/{vid}/download?format={format}");

    public async Task<SummaryDto> CompareAsync(Guid docId, Guid from, Guid to) =>
        await ReadAsync<SummaryDto>(await Http.GetAsync($"/api/v1/documents/{docId}/compare?from={from}&to={to}&format=summary"));

    // ---- Editing: the Collabora/WOPI round trip (§6, §6.1) ----
    public async Task<SessionDto> MintSessionAsync(Guid vid) =>
        await ReadAsync<SessionDto>(await Http.PostAsync($"/api/v1/versions/{vid}/sessions", null));

    // Plays Collabora's side of the WOPI contract: LOCK, then PutFile. `wopi` must be an unauthenticated
    // client — WOPI authorizes on the access_token query param, not the session cookie.
    public static async Task<HttpResponseMessage> WopiSaveAsync(HttpClient wopi, SessionDto s, byte[] bytes, string lockId = "L1")
    {
        var lockReq = new HttpRequestMessage(HttpMethod.Post, $"/wopi/files/{s.SessionId}?access_token={s.AccessToken}");
        lockReq.Headers.Add("X-WOPI-Override", "LOCK");
        lockReq.Headers.Add("X-WOPI-Lock", lockId);
        (await wopi.SendAsync(lockReq)).EnsureSuccessStatusCode();

        var put = new HttpRequestMessage(HttpMethod.Post, $"/wopi/files/{s.SessionId}/contents?access_token={s.AccessToken}")
        {
            Content = new ByteArrayContent(bytes),
        };
        put.Headers.Add("X-WOPI-Lock", lockId);
        return await wopi.SendAsync(put);
    }

    public Task<HttpResponseMessage> CloseSessionRawAsync(Guid sid) => Http.DeleteAsync($"/api/v1/sessions/{sid}");

    // ---- Publish / approvals (§10.1) ----
    public async Task<PublishedDto> PublishAsync(Guid vid, string kind, string? name = null) =>
        await ReadAsync<PublishedDto>(await Http.PostAsJsonAsync($"/api/v1/versions/{vid}/publish", new { kind, name }));

    public Task<HttpResponseMessage> PublishRawAsync(Guid vid, string kind) =>
        Http.PostAsJsonAsync($"/api/v1/versions/{vid}/publish", new { kind });

    public async Task<PublicationListDto> ListPublicationsAsync(Guid docId) =>
        await ReadAsync<PublicationListDto>(await Http.GetAsync($"/api/v1/documents/{docId}/publications"));

    public async Task<ApprovalDto[]> RequestApprovalAsync(Guid vid, Guid[] approverIds, DateTimeOffset? dueAt = null) =>
        await ReadAsync<ApprovalDto[]>(await Http.PostAsJsonAsync($"/api/v1/versions/{vid}/approvals", new { approverIds, dueAt }));

    public Task<HttpResponseMessage> RequestApprovalRawAsync(Guid vid, Guid[] approverIds, DateTimeOffset? dueAt = null) =>
        Http.PostAsJsonAsync($"/api/v1/versions/{vid}/approvals", new { approverIds, dueAt });

    public Task<HttpResponseMessage> RespondRawAsync(Guid approvalId, string decision, string? comment = null) =>
        Http.PostAsJsonAsync($"/api/v1/approvals/{approvalId}:respond", new { decision, comment });

    public Task<HttpResponseMessage> CancelApprovalRawAsync(Guid approvalId) =>
        Http.PostAsync($"/api/v1/approvals/{approvalId}:cancel", null);

    // Task 5: the approvals inbox. `filter=assigned|requested`, `status=open|closed`, both optional.
    public async Task<ApprovalListDto> ListApprovalsAsync(string? filter = null, string? status = null)
    {
        var qs = new List<string>();
        if (filter is not null) qs.Add($"filter={filter}");
        if (status is not null) qs.Add($"status={status}");
        var query = qs.Count == 0 ? "" : "?" + string.Join("&", qs);
        return await ReadAsync<ApprovalListDto>(await Http.GetAsync($"/api/v1/approvals{query}"));
    }

    // Task 5: the approvals panel on one version — a bare array, not cursor-paginated.
    public async Task<ApprovalListItemDto[]> ListVersionApprovalsAsync(Guid vid) =>
        await ReadAsync<ApprovalListItemDto[]>(await Http.GetAsync($"/api/v1/versions/{vid}/approvals"));

    // ---- Sharing (§10.1) ----
    public async Task<ShareLinkDto> CreateShareLinkAsync(Guid vid, DateTimeOffset? expiresAt = null) =>
        await ReadAsync<ShareLinkDto>(await Http.PostAsJsonAsync($"/api/v1/versions/{vid}/share-links", new { expiresAt }));

    public async Task<ShareLinkListDto> ListShareLinksAsync(Guid docId) =>
        await ReadAsync<ShareLinkListDto>(await Http.GetAsync($"/api/v1/documents/{docId}/share-links"));

    public Task<HttpResponseMessage> RevokeShareLinkRawAsync(Guid id) => Http.DeleteAsync($"/api/v1/share-links/{id}");

    // ---- Members / audit (§10.1) ----
    public async Task<MemberDto[]> ListMembersAsync(Guid docId) =>
        await ReadAsync<MemberDto[]>(await Http.GetAsync($"/api/v1/documents/{docId}/members"));

    public Task<HttpResponseMessage> AddMemberRawAsync(Guid docId, string email, string role) =>
        Http.PostAsJsonAsync($"/api/v1/documents/{docId}/members", new { email, role });

    public async Task AddMemberAsync(Guid docId, string email, string role) =>
        (await AddMemberRawAsync(docId, email, role)).EnsureSuccessStatusCode();

    public Task<HttpResponseMessage> SetMemberRoleRawAsync(Guid docId, Guid uid, string role) =>
        Http.PatchAsJsonAsync($"/api/v1/documents/{docId}/members/{uid}", new { role });

    public Task<HttpResponseMessage> RemoveMemberRawAsync(Guid docId, Guid uid) =>
        Http.DeleteAsync($"/api/v1/documents/{docId}/members/{uid}");

    public async Task<AuditListDto> AuditAsync(Guid docId, int limit = 100) =>
        await ReadAsync<AuditListDto>(await Http.GetAsync($"/api/v1/documents/{docId}/audit?limit={limit}"));

    public async Task<string[]> AuditActionsAsync(Guid docId) =>
        (await AuditAsync(docId)).Items.Select(i => i.Action).ToArray();

    // ---- Merge (§5.3) ----
    public Task<HttpResponseMessage> MergeRawAsync(Guid docId, Guid left, Guid right) =>
        Http.PostAsJsonAsync($"/api/v1/documents/{docId}/merges", new { left, right });

    // ---- Copies & push (§8, §10.1) ----
    public Task<HttpResponseMessage> ForkRawAsync(Guid vid, string name) =>
        Http.PostAsJsonAsync($"/api/v1/versions/{vid}/copies", new { name });

    public async Task<CopyDto> ForkAsync(Guid vid, string name) =>
        await ReadAsync<CopyDto>(await ForkRawAsync(vid, name));

    public async Task<CopyDto[]> ListCopiesAsync(Guid docId) =>
        await ReadAsync<CopyDto[]>(await Http.GetAsync($"/api/v1/documents/{docId}/copies"));

    public Task<HttpResponseMessage> PushRawAsync(Guid copyId, Guid targetDocumentId, Guid versionId) =>
        Http.PostAsJsonAsync($"/api/v1/documents/{copyId}/pushes", new { targetDocumentId, versionId });

    public async Task<PushRequestDto> PushAsync(Guid copyId, Guid targetDocumentId, Guid versionId) =>
        await ReadAsync<PushRequestDto>(await PushRawAsync(copyId, targetDocumentId, versionId));

    public async Task<PushRequestDto[]> ListPushRequestsAsync(Guid docId, string? status = null) =>
        await ReadAsync<PushRequestDto[]>(await Http.GetAsync(status is null
            ? $"/api/v1/documents/{docId}/push-requests"
            : $"/api/v1/documents/{docId}/push-requests?status={status}"));

    public Task<HttpResponseMessage> DecidePushRawAsync(Guid pushRequestId, string decision) =>
        Http.PostAsync($"/api/v1/push-requests/{pushRequestId}:{decision}", null);

    public async Task<PushRequestDto> DecidePushAsync(Guid pushRequestId, string decision) =>
        await ReadAsync<PushRequestDto>(await DecidePushRawAsync(pushRequestId, decision));

    // ---- Org (§10.1, settings screen) ----
    public async Task<OrgDto> GetOrgAsync() =>
        await ReadAsync<OrgDto>(await Http.GetAsync("/api/v1/org"));

    public async Task<OrgMemberDto[]> ListOrgMembersAsync() =>
        await ReadAsync<OrgMemberDto[]>(await Http.GetAsync("/api/v1/org/members"));

    public Task<HttpResponseMessage> InviteOrgMemberRawAsync(string email, string role) =>
        Http.PostAsJsonAsync("/api/v1/org/members", new { email, role });

    public async Task<OrgInviteDto> InviteOrgMemberAsync(string email, string role) =>
        await ReadAsync<OrgInviteDto>(await InviteOrgMemberRawAsync(email, role));

    // Convenience: a document with one uploaded base version.
    public async Task<(Guid DocId, Guid VersionId)> NewDocumentWithBaseAsync(string name = "Doc", byte[]? bytes = null)
    {
        var doc = await CreateDocumentAsync(name);
        var v = await UploadAsync(doc.Id, bytes ?? DocxFixtures.Base());
        return (doc.Id, v.VersionId);
    }
}
