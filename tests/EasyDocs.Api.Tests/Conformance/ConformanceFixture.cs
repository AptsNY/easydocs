using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Tests;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests.Conformance;

// The v1 conformance profile (spec §12.1, E1-E12). Every criterion is driven through the public REST
// API with an `ed_` personal access token — no DbContext shortcuts for the *actions*, so a green suite
// is a direct proof of the M3 exit gate ("the API drives the full document flow unattended").
//
// Two documented deviations:
//   * Criteria that assert internal state the API deliberately does not expose (branch closure in E4)
//     read the DB to *verify*. The actions that produced that state still go through the API.
//   * E3/E4 need a Collabora editing round trip. Rather than drive a headless browser, the suite plays
//     Collabora's side of the WOPI contract directly (LOCK + PutFile against /wopi/files/{sid}), which
//     is the exact HTTP conversation Collabora has with the host. Spec §12.3 permits either; this
//     keeps CI free of a browser and a running Collabora for the pure-protocol assertions.
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
public record VersionListItem(Guid Id, int Major, int Minor, int Revision, string Source, DateTimeOffset CreatedAt, Guid CreatedBy);
public record VersionListDto(VersionListItem[] Items, string? NextCursor);
public record PublicationItem(Guid VersionId, int Major, int Minor, int Revision, string? Name, Guid PublishedBy, DateTimeOffset PublishedAt, string Kind);
public record PublicationListDto(PublicationItem[] Items, string? NextCursor);
public record SummaryDto(int Insertions, int Deletions, int Moves, int FormatChanges);
public record SessionDto(Guid SessionId, string EditorUrl, string AccessToken, int AccessTokenTtlSeconds);
public record ApprovalDto(Guid Id, Guid VersionId, Guid ApproverId, DateTimeOffset? DueAt);
public record ShareLinkDto(string Token, string Url);
public record MemberDto(Guid UserId, string Email, string DisplayName, string Role);
public record AuditItemDto(Guid Id, string Action, Guid? ActorUserId, string? TargetType, string? TargetId, string? Metadata, DateTimeOffset CreatedAt);
public record AuditListDto(AuditItemDto[] Items, string? NextCursor);
public record PublishedDto(Guid VersionId, int Major, int Minor, int Revision, string Kind);

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

    // ---- Versions (§10.1, §10.3 multipart ingest) ----
    public async Task<VersionRefDto> UploadAsync(Guid docId, byte[]? bytes = null, string fileName = "doc.docx") =>
        await ReadAsync<VersionRefDto>(await Http.PostAsync($"/api/v1/documents/{docId}/versions", TestAuth.DocxForm(bytes, fileName)));

    public async Task<VersionRefDto> ImportAsync(Guid docId, byte[]? bytes = null) =>
        await ReadAsync<VersionRefDto>(await Http.PostAsync($"/api/v1/documents/{docId}/versions:import", TestAuth.DocxForm(bytes)));

    public async Task<VersionListDto> ListVersionsAsync(Guid docId, int? limit = 100) =>
        await ReadAsync<VersionListDto>(await Http.GetAsync($"/api/v1/documents/{docId}/versions?limit={limit}"));

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

    // ---- Sharing (§10.1) ----
    public async Task<ShareLinkDto> CreateShareLinkAsync(Guid vid, DateTimeOffset? expiresAt = null) =>
        await ReadAsync<ShareLinkDto>(await Http.PostAsJsonAsync($"/api/v1/versions/{vid}/share-links", new { expiresAt }));

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

    // Convenience: a document with one uploaded base version.
    public async Task<(Guid DocId, Guid VersionId)> NewDocumentWithBaseAsync(string name = "Doc", byte[]? bytes = null)
    {
        var doc = await CreateDocumentAsync(name);
        var v = await UploadAsync(doc.Id, bytes ?? DocxFixtures.Base());
        return (doc.Id, v.VersionId);
    }
}
