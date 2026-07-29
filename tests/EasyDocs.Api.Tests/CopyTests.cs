using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// Push To Copy — the fork half of M4 (spec §8, E9). A copy is an ordinary documents row carrying
// ParentDocumentId + ForkedFromVersionId, referencing the SAME immutable blob (zero-copy), with its
// own membership that starts and stays independent of the master's.
public class CopyTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public CopyTests(ApiFactory f) => _f = f;

    // VersionId is the fork's first version — present on the fork response, absent from the copies list.
    private record CopyDto(Guid Id, string Name, Guid ParentDocumentId, Guid ForkedFromVersionId, Guid? VersionId);
    private record UploadDto(Guid VersionId, int Major, int Minor, int Revision);

    private async Task<(HttpClient Client, Account Account, Guid DocId, Guid VersionId, byte[] Bytes)> MasterAsync()
    {
        var account = await _f.RegisterAsync();
        var docId = await account.Client.CreateDocAsync("Master agreement");
        // DocxFixtures.Base() rebuilds the zip per call (timestamps), so capture the bytes we uploaded.
        var bytes = DocxFixtures.Base();
        var (vid, _) = await account.Client.UploadAsync(docId, bytes);
        return (account.Client, account, docId, vid, bytes);
    }

    private static Task<HttpResponseMessage> ForkAsync(HttpClient c, Guid vid, string? name = "Reviewer copy") =>
        c.PostAsJsonAsync($"/api/v1/versions/{vid}/copies", new { name });

    [Fact]
    public async Task Fork_creates_a_copy_document_pointing_at_the_source_version()
    {
        var (c, _, docId, vid, _) = await MasterAsync();

        var res = await ForkAsync(c, vid);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var copy = (await res.Content.ReadFromJsonAsync<CopyDto>())!;
        Assert.Equal("Reviewer copy", copy.Name);
        Assert.Equal(docId, copy.ParentDocumentId);
        Assert.Equal(vid, copy.ForkedFromVersionId);
        Assert.NotEqual(docId, copy.Id);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var stored = await db.Documents.FirstAsync(d => d.Id == copy.Id);
        Assert.Equal(docId, stored.ParentDocumentId);
        Assert.Equal(vid, stored.ForkedFromVersionId);

        // A copy is a real document: it gets a main branch of its own.
        var main = await db.Branches.SingleAsync(b => b.DocumentId == copy.Id);
        Assert.Equal(BranchKind.Main, main.Kind);
        Assert.Equal(0, main.Ordinal);
    }

    [Fact]
    public async Task Fork_is_zero_copy_and_starts_the_copys_own_history_at_0_0_1()
    {
        var (c, _, _, vid, bytes) = await MasterAsync();

        var copy = (await (await ForkAsync(c, vid)).Content.ReadFromJsonAsync<CopyDto>())!;

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var sourceSha = await db.Versions.Where(v => v.Id == vid).Select(v => v.BlobSha256).FirstAsync();
        var first = await db.Versions.SingleAsync(v => v.DocumentId == copy.Id);

        Assert.Equal(copy.VersionId, first.Id);
        Assert.Equal(sourceSha, first.BlobSha256);          // same blob — nothing re-stored
        Assert.Equal(VersionSource.CopyPush, first.Source);
        Assert.Equal((0, 0, 1), (first.Major, first.Minor, first.Revision)); // its own counter, from scratch

        // And the content really is the master's version, byte for byte.
        var download = await c.GetAsync($"/api/v1/versions/{first.Id}/download");
        download.EnsureSuccessStatusCode();
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_copy_starts_with_only_its_creator_as_owner()
    {
        var (c, account, docId, vid, _) = await MasterAsync();

        // A second master member must NOT be carried into the fork (spec §11: copies do not inherit
        // master membership).
        var masterEditor = await _f.SeedOrgUserAsync(account.OrgId);
        (await c.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email = masterEditor.Email, role = "Editor" })).EnsureSuccessStatusCode();

        var copy = (await (await ForkAsync(c, vid)).Content.ReadFromJsonAsync<CopyDto>())!;

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var members = await db.DocumentMembers.Where(m => m.DocumentId == copy.Id).ToListAsync();

        var only = Assert.Single(members);
        Assert.Equal(account.UserId, only.UserId);
        Assert.Equal(DocRole.Owner, only.Role);
    }

    [Fact]
    public async Task A_copy_only_member_cannot_see_master_drafts()
    {
        var (c, account, docId, vid, _) = await MasterAsync();
        var copy = (await (await ForkAsync(c, vid)).Content.ReadFromJsonAsync<CopyDto>())!;

        // The external reviewer is invited to the COPY only (spec §8).
        var reviewerAccount = await _f.SeedOrgUserAsync(account.OrgId);
        (await c.PostAsJsonAsync($"/api/v1/documents/{copy.Id}/members",
            new { email = reviewerAccount.Email, role = "Editor" })).EnsureSuccessStatusCode();
        var reviewer = reviewerAccount.Client;

        // A draft lands on the master AFTER the fork — the reviewer must never see it.
        var (draftId, _) = await c.UploadAsync(docId, DocxFixtures.Edited());

        Assert.Equal(HttpStatusCode.OK, (await reviewer.GetAsync($"/api/v1/documents/{copy.Id}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync($"/api/v1/documents/{docId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync($"/api/v1/documents/{docId}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync($"/api/v1/versions/{draftId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync($"/api/v1/versions/{draftId}/download")).StatusCode);
    }

    [Fact]
    public async Task Listing_copies_returns_the_forks_of_a_document()
    {
        var (c, _, docId, vid, _) = await MasterAsync();
        var first = (await (await ForkAsync(c, vid, "Counsel copy")).Content.ReadFromJsonAsync<CopyDto>())!;
        var second = (await (await ForkAsync(c, vid, "Vendor copy")).Content.ReadFromJsonAsync<CopyDto>())!;

        var res = await c.GetAsync($"/api/v1/documents/{docId}/copies");

        res.EnsureSuccessStatusCode();
        var items = (await res.Content.ReadFromJsonAsync<CopyDto[]>())!;
        Assert.Equal(2, items.Length);
        Assert.Equal(
            new[] { first.Id, second.Id }.Order(),
            items.Select(i => i.Id).Order());
        Assert.Contains("Counsel copy", items.Select(i => i.Name));
        Assert.Contains("Vendor copy", items.Select(i => i.Name));

        // The copies list is scoped to its own document — a fork is not listed under an unrelated one.
        var other = await c.CreateDocAsync("Unrelated");
        Assert.Empty((await (await c.GetAsync($"/api/v1/documents/{other}/copies")).Content.ReadFromJsonAsync<CopyDto[]>())!);
    }

    [Fact]
    public async Task Forking_requires_edit_on_the_source_and_membership_at_all()
    {
        var (c, account, docId, vid, _) = await MasterAsync();

        // Viewer on the master may read it but may not fork content out of it.
        var viewerAccount = await _f.SeedOrgUserAsync(account.OrgId);
        (await c.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email = viewerAccount.Email, role = "Viewer" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await ForkAsync(viewerAccount.Client, vid)).StatusCode);

        // Same org, not a member -> 403; another org -> 404 (existence is not leaked).
        var stranger = await _f.SeedOrgUserAsync(account.OrgId);
        Assert.Equal(HttpStatusCode.Forbidden, (await ForkAsync(stranger.Client, vid)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.Client.GetAsync($"/api/v1/documents/{docId}/copies")).StatusCode);

        var outsider = await _f.RegisterAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await ForkAsync(outsider.Client, vid)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Client.GetAsync($"/api/v1/documents/{docId}/copies")).StatusCode);

        // Unauthenticated is 401 on both.
        var anon = _f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await ForkAsync(anon, vid)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/v1/documents/{docId}/copies")).StatusCode);
    }

    [Fact]
    public async Task Forking_an_unknown_version_is_404()
    {
        var (c, _, _, _, _) = await MasterAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await ForkAsync(c, Guid.NewGuid())).StatusCode);
    }
}
