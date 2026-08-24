using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Tests;
using EasyDocs.Api.Tests.Fixtures;
using EasyDocs.Api.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// GET /api/v1/documents/{id}/merges/preview — the read half of merging. The helpers below are
// deliberately duplicated from MergeTests rather than shared: MergeTests is the behaviour guard for
// an earlier refactor and has to stay byte-identical, so it cannot be edited to extract them.
public class MergePreviewTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public MergePreviewTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private record RegisterDto(Guid Id, Guid OrgId);
    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId);

    // Mirrors MergePreviewService.Preview over the wire; ReadFromJsonAsync uses JsonSerializerDefaults.Web,
    // so these PascalCase names bind to the camelCase the endpoint emits.
    private record SummaryDto(int Insertions, int Deletions, int Moves, int FormatChanges);
    private record BaseDto(Guid Id, string Number);
    private record SideDto(Guid Id, string Number, string AuthorName, SummaryDto? Summary);
    private record OverlapDto(int Ordinal, string Text);
    private record PreviewDto(bool Available, BaseDto? Base, SideDto Main, SideDto Incoming,
        IReadOnlyList<OverlapDto>? Overlaps);

    private async Task<(HttpClient client, Guid userId, Guid orgId)> RegisterAsync(string displayName)
    {
        var client = _f.CreateClient();
        var email = $"mpv-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName, password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var dto = (await reg.Content.ReadFromJsonAsync<RegisterDto>())!;
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return (client, dto.Id, dto.OrgId);
    }

    private static MultipartFormDataContent Docx(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        return new MultipartFormDataContent { { part, "file", "d.docx" } };
    }

    private async Task<Guid> AddMemberAsync(Guid orgId, Guid? docId, string displayName, DocRole role)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var user = new User { Id = Guid.NewGuid(), Email = $"m-{Guid.NewGuid():N}@x.com", DisplayName = displayName, CreatedAt = now };
        db.Add(user);
        db.Add(new OrgMember { OrgId = orgId, UserId = user.Id, Role = OrgRole.Member, CreatedAt = now });
        // docId null => in the org but on no document, the "same org, not a member" case.
        if (docId is { } d) db.Add(new DocumentMember { DocumentId = d, UserId = user.Id, Role = role, CreatedAt = now });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private HttpClient ClientFor(Guid userId, Guid orgId)
    {
        using var scope = _f.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<JwtService>().Issue(userId, orgId);
        var client = _f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    // Commit `bytes` as `authorId` on a concurrent branch forked at `baseVersionId` (stale-base branch).
    private async Task<Guid> CommitConcurrentAsync(Guid docId, Guid baseVersionId, Guid authorId, byte[] bytes)
    {
        using var scope = _f.Services.CreateScope();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var versioning = scope.ServiceProvider.GetRequiredService<VersioningService>();
        var stored = await blobs.PutAsync(new MemoryStream(bytes));
        var res = await versioning.CommitSaveAsync(
            new CommitInput(docId, stored.Sha256, stored.SizeBytes, VersionSource.EditWopi, authorId, BaseVersionId: baseVersionId),
            default);
        return res.VersionId;
    }

    // A owns the doc. Base @ H (main, the fork point). A imports Edited -> LEFT on main. Bob commits
    // `rightBytes` at the stale base H -> RIGHT on a concurrent branch.
    private async Task<(Guid docId, Guid forkId, Guid left, Guid right)> SetupConcurrentAsync(
        HttpClient a, Guid orgId, byte[] rightBytes)
    {
        var docId = (await (await a.PostAsJsonAsync("/api/v1/documents", new { name = "MergePreview" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var up = await a.PostAsync($"/api/v1/documents/{docId}/versions", Docx(DocxFixtures.Base()));
        up.EnsureSuccessStatusCode();
        var h = (await up.Content.ReadFromJsonAsync<UploadDto>())!.VersionId;
        var imp = await a.PostAsync($"/api/v1/documents/{docId}/versions:import", Docx(DocxFixtures.Edited()));
        imp.EnsureSuccessStatusCode();
        var left = (await imp.Content.ReadFromJsonAsync<UploadDto>())!.VersionId;

        var bobId = await AddMemberAsync(orgId, docId, "Bob", DocRole.Editor);
        var right = await CommitConcurrentAsync(docId, h, bobId, rightBytes);
        return (docId, h, left, right);
    }

    private static string Url(Guid docId, Guid left, Guid right) =>
        $"/api/v1/documents/{docId}/merges/preview?left={left}&right={right}";

    [Fact]
    public async Task Editor_gets_a_preview_with_base_and_both_sides()
    {
        var (a, _, orgId) = await RegisterAsync("Alice");
        var (docId, forkId, left, right) = await SetupConcurrentAsync(a, orgId, DocxFixtures.EditedPlusEcho());

        var resp = await a.GetAsync(Url(docId, left, right));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var p = (await resp.Content.ReadFromJsonAsync<PreviewDto>())!;

        Assert.True(p.Available);
        Assert.NotNull(p.Base);
        Assert.Equal(forkId, p.Base!.Id);
        Assert.NotNull(p.Main.Summary);
        Assert.NotNull(p.Incoming.Summary);
    }

    [Fact]
    public async Task Viewer_is_forbidden()
    {
        var (a, _, orgId) = await RegisterAsync("Alice");
        var (docId, _, left, right) = await SetupConcurrentAsync(a, orgId, DocxFixtures.EditedPlusEcho());

        var viewer = ClientFor(await AddMemberAsync(orgId, docId, "Vera", DocRole.Viewer), orgId);

        var resp = await viewer.GetAsync(Url(docId, left, right));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Non_member_in_the_same_org_gets_403()
    {
        var (a, _, orgId) = await RegisterAsync("Alice");
        var (docId, _, left, right) = await SetupConcurrentAsync(a, orgId, DocxFixtures.EditedPlusEcho());

        var outsider = ClientFor(await AddMemberAsync(orgId, null, "Nora", DocRole.Editor), orgId);

        var resp = await outsider.GetAsync(Url(docId, left, right));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Unknown_document_is_404()
    {
        var (a, _, _) = await RegisterAsync("Alice");

        var resp = await a.GetAsync(Url(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Cross_org_document_is_404()
    {
        var (a, _, _) = await RegisterAsync("Alice");
        var (b, _, bOrg) = await RegisterAsync("Bea");
        var (bDoc, _, bLeft, bRight) = await SetupConcurrentAsync(b, bOrg, DocxFixtures.EditedPlusEcho());

        // 404, not 403: a foreign document must not be confirmed to exist.
        var resp = await a.GetAsync(Url(bDoc, bLeft, bRight));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task A_version_from_another_document_is_409()
    {
        var (a, _, orgId) = await RegisterAsync("Alice");
        var (docId, _, _, right) = await SetupConcurrentAsync(a, orgId, DocxFixtures.EditedPlusEcho());
        var (_, _, otherLeft, _) = await SetupConcurrentAsync(a, orgId, DocxFixtures.EditedPlusEcho());

        var resp = await a.GetAsync(Url(docId, otherLeft, right));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Neither_side_on_an_incoming_branch_is_409()
    {
        var (a, _, orgId) = await RegisterAsync("Alice");
        var (docId, _, left, _) = await SetupConcurrentAsync(a, orgId, DocxFixtures.EditedPlusEcho());

        // Main's head against itself: nothing to bring onto main, so there is no merge to preview.
        var resp = await a.GetAsync(Url(docId, left, left));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Null_root_version_yields_null_base_but_stays_available()
    {
        var (a, _, orgId) = await RegisterAsync("Alice");
        var (docId, _, left, right) = await SetupConcurrentAsync(a, orgId, DocxFixtures.EditedPlusEcho());

        // Legacy/IncomingPush shape: a branch with no recorded fork point. The base panel goes away,
        // the merge itself does not.
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var branchId = (await db.Versions.FirstAsync(v => v.Id == right)).BranchId;
            var branch = await db.Branches.FirstAsync(b => b.Id == branchId);
            branch.RootVersionId = null;
            await db.SaveChangesAsync();
        }

        var resp = await a.GetAsync(Url(docId, left, right));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var p = (await resp.Content.ReadFromJsonAsync<PreviewDto>())!;

        Assert.True(p.Available);
        Assert.Null(p.Base);
        Assert.Null(p.Overlaps);
        Assert.Null(p.Main.Summary);
        Assert.Null(p.Incoming.Summary);
    }

    [Fact]
    public async Task A_malformed_incoming_blob_yields_available_false()
    {
        var (a, _, orgId) = await RegisterAsync("Alice");
        // The merge would fail on this pair, so the preview must say so rather than let a user click
        // into a guaranteed failure.
        var (docId, _, left, right) = await SetupConcurrentAsync(a, orgId, DocxFixtures.Malformed());

        var resp = await a.GetAsync(Url(docId, left, right));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var p = (await resp.Content.ReadFromJsonAsync<PreviewDto>())!;

        Assert.False(p.Available);
    }

    [Fact]
    public async Task The_preview_writes_no_version_and_no_audit_row()
    {
        var (a, _, orgId) = await RegisterAsync("Alice");
        var (docId, _, left, right) = await SetupConcurrentAsync(a, orgId, DocxFixtures.EditedPlusEcho());

        var (versionsBefore, auditBefore) = await CountsAsync(docId);
        (await a.GetAsync(Url(docId, left, right))).EnsureSuccessStatusCode();
        var (versionsAfter, auditAfter) = await CountsAsync(docId);

        // This is what keeps a GET a GET: the preview commits nothing and logs nothing.
        Assert.Equal(versionsBefore, versionsAfter);
        Assert.Equal(auditBefore, auditAfter);
    }

    private async Task<(int Versions, int Audit)> CountsAsync(Guid docId)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        return (await db.Versions.CountAsync(v => v.DocumentId == docId),
                await db.AuditEvents.CountAsync(e => e.DocumentId == docId));
    }
}
