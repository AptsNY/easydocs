using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Xml.Linq;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Tests;
using EasyDocs.Api.Tests.Fixtures;
using EasyDocs.Api.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class MergeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public MergeTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private record RegisterDto(Guid Id, Guid OrgId);
    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId);
    private record MergeDto(Guid MergeVersionId);

    // Register user A (Alice, doc owner) and return an authed client + A's ids.
    private async Task<(HttpClient client, Guid userId, Guid orgId)> RegisterAsync(string displayName)
    {
        var client = _f.CreateClient();
        var email = $"merge-{Guid.NewGuid():N}@example.com";
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

    // Create an in-org member (own DisplayName + DocRole) and return their id.
    private async Task<Guid> AddMemberAsync(Guid orgId, Guid docId, string displayName, DocRole role)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var user = new User { Id = Guid.NewGuid(), Email = $"m-{Guid.NewGuid():N}@x.com", DisplayName = displayName, CreatedAt = now };
        db.Add(user);
        db.Add(new OrgMember { OrgId = orgId, UserId = user.Id, Role = OrgRole.Member, CreatedAt = now });
        db.Add(new DocumentMember { DocumentId = docId, UserId = user.Id, Role = role, CreatedAt = now });
        await db.SaveChangesAsync();
        return user.Id;
    }

    // Mint a bearer client for an existing in-org user.
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

    // A owns the doc. Base @ H (main). A imports EditedA -> LEFT on main. B commits `rightBytes` at base H -> RIGHT on a concurrent branch.
    private async Task<(Guid docId, Guid left, Guid right, Guid bobId)> SetupConcurrentAsync(
        HttpClient a, Guid orgId, string bobName, byte[] rightBytes)
    {
        var docId = (await (await a.PostAsJsonAsync("/api/v1/documents", new { name = "Merge" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var up = await a.PostAsync($"/api/v1/documents/{docId}/versions", Docx(DocxFixtures.Base()));
        up.EnsureSuccessStatusCode();
        var h = (await up.Content.ReadFromJsonAsync<UploadDto>())!.VersionId;
        var imp = await a.PostAsync($"/api/v1/documents/{docId}/versions:import", Docx(DocxFixtures.Edited()));
        imp.EnsureSuccessStatusCode();
        var left = (await imp.Content.ReadFromJsonAsync<UploadDto>())!.VersionId;

        var bobId = await AddMemberAsync(orgId, docId, bobName, DocRole.Editor);
        var right = await CommitConcurrentAsync(docId, h, bobId, rightBytes); // stale base H -> concurrent branch
        return (docId, left, right, bobId);
    }

    [Fact]
    public async Task Merge_shows_incoming_branch_changes_as_tracked_changes_by_its_author()
    {
        var (a, _, orgId) = await RegisterAsync("Alice");
        // Main head = Edited (Alice). Incoming concurrent branch = Edited + "Echo" (Bob).
        var (docId, left, right, _) = await SetupConcurrentAsync(a, orgId, "Bob", DocxFixtures.EditedPlusEcho());

        var resp = await a.PostAsJsonAsync($"/api/v1/documents/{docId}/merges", new { left, right });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var mergeVersionId = (await resp.Content.ReadFromJsonAsync<MergeDto>())!.MergeVersionId;

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();

        // Two-parent merge commit on main: parent = main head (left/Alice), merge-parent = incoming (right/Bob).
        var mv = await db.Versions.FirstAsync(v => v.Id == mergeVersionId);
        Assert.Equal(VersionSource.Merge, mv.Source);
        Assert.Equal(left, mv.ParentVersionId);
        Assert.Equal(right, mv.MergeParentVersionId);
        var mainBranch = await db.Branches.FirstAsync(b => b.DocumentId == docId && b.Ordinal == 0);
        Assert.Equal(mainBranch.Id, mv.BranchId);

        // The incoming concurrent branch is closed.
        var rightBranchId = (await db.Versions.FirstAsync(v => v.Id == right)).BranchId;
        var rightBranch = await db.Branches.FirstAsync(b => b.Id == rightBranchId);
        Assert.Equal(mergeVersionId, rightBranch.MergedIntoVersionId);
        Assert.Equal(BranchKind.Concurrent, rightBranch.Kind);

        // Single-author incoming redline: Bob's distinctive edit ("Echo") is a tracked change attributed
        // to Bob; Alice's edits (already the accepted main content) are the clean base, NOT tracked.
        var bytes = await ReadBlobAsync(blobs, mv.BlobSha256);
        var (revText, authors) = Revisions(bytes);
        Assert.Contains("Echo", revText);       // incoming edit present as a tracked change
        Assert.DoesNotContain("Delta", revText); // main-only edit is clean base, not tracked
        Assert.Contains("Bob", authors);         // attributed to the incoming author
        Assert.DoesNotContain("Alice", authors);
    }

    [Fact]
    public async Task Nothing_is_lost()
    {
        var (a, _, orgId) = await RegisterAsync("Alice");
        var (docId, left, right, _) = await SetupConcurrentAsync(a, orgId, "Bob", DocxFixtures.EditedPlusEcho());

        (await a.PostAsJsonAsync($"/api/v1/documents/{docId}/merges", new { left, right })).EnsureSuccessStatusCode();

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        // Both the main head and the incoming-branch version still exist in history after the merge.
        Assert.True(await db.Versions.AnyAsync(v => v.Id == left));
        Assert.True(await db.Versions.AnyAsync(v => v.Id == right));
    }

    [Fact]
    public async Task Merge_degrades_when_comparer_fails()
    {
        var (a, _, orgId) = await RegisterAsync("Alice");
        // Incoming branch head has a malformed (non-docx) blob -> the Compare throws -> degrade.
        var (docId, left, right, _) = await SetupConcurrentAsync(a, orgId, "Bob", DocxFixtures.Malformed());

        var before = await CountVersionsAsync(docId);
        var resp = await a.PostAsJsonAsync($"/api/v1/documents/{docId}/merges", new { left, right });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        Assert.Equal(before, await db.Versions.CountAsync(v => v.DocumentId == docId)); // nothing committed
        var rightBranchId = (await db.Versions.FirstAsync(v => v.Id == right)).BranchId;
        var rightBranch = await db.Branches.FirstAsync(b => b.Id == rightBranchId);
        Assert.Null(rightBranch.MergedIntoVersionId); // still OPEN
    }

    [Fact]
    public async Task Merge_requires_editor_role()
    {
        var (a, _, orgId) = await RegisterAsync("Alice");
        var (docId, left, right, _) = await SetupConcurrentAsync(a, orgId, "Bob", DocxFixtures.EditedPlusEcho());

        var viewerId = await AddMemberAsync(orgId, docId, "Vera", DocRole.Viewer);
        var viewer = ClientFor(viewerId, orgId);

        var resp = await viewer.PostAsJsonAsync($"/api/v1/documents/{docId}/merges", new { left, right });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private async Task<int> CountVersionsAsync(Guid docId)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        return await db.Versions.CountAsync(v => v.DocumentId == docId);
    }

    private static async Task<byte[]> ReadBlobAsync(IBlobStore blobs, string sha)
    {
        await using var s = await blobs.OpenReadAsync(sha);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    // Concatenated text of every tracked change (w:ins run text + w:del delText), plus the set of w:author values.
    private static (string RevText, HashSet<string?> Authors) Revisions(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        using var s = zip.GetEntry("word/document.xml")!.Open();
        var doc = XDocument.Load(s);
        var revs = doc.Descendants().Where(e => e.Name == W + "ins" || e.Name == W + "del").ToList();
        var text = string.Concat(revs.SelectMany(e => e.Descendants().Where(t => t.Name == W + "t" || t.Name == W + "delText")).Select(t => t.Value));
        var authors = revs.Select(e => e.Attribute(W + "author")?.Value).ToHashSet();
        return (text, authors);
    }
}
