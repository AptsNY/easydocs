using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class CommitSaveTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public CommitSaveTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"commit-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "D", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    private static MultipartFormDataContent Docx(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        return new MultipartFormDataContent { { part, "file", "d.docx" } };
    }

    private static async Task<Guid> CreateDocAsync(HttpClient c)
    {
        var create = await c.PostAsJsonAsync("/api/v1/documents", new { name = "Doc" });
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<DocDto>())!.Id;
    }

    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId, int Major, int Minor, int Revision);
    private record VersionDto(Guid Id, int Major, int Minor, int Revision, string Source);
    private record VersionPage(List<VersionDto> Items, string? NextCursor);
    private record MintDto(Guid SessionId, string AccessToken);

    // Upload a base version, returning (docId, headVersionId).
    private static async Task<(Guid docId, Guid headVid)> DocWithHeadAsync(HttpClient c, byte[] bytes)
    {
        var docId = await CreateDocAsync(c);
        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(bytes));
        up.EnsureSuccessStatusCode();
        return (docId, (await up.Content.ReadFromJsonAsync<UploadDto>())!.VersionId);
    }

    private static async Task<(Guid sid, string token)> MintSessionAsync(HttpClient c, Guid vid)
    {
        var mint = (await (await c.PostAsync($"/api/v1/versions/{vid}/sessions", null))
            .Content.ReadFromJsonAsync<MintDto>())!;
        return (mint.SessionId, mint.AccessToken);
    }

    // Drive a session save through WOPI (LOCK is idempotent, so re-locking the same value is fine).
    // Returns the created/deduped version id from X-WOPI-ItemVersion.
    private async Task<Guid> WopiSaveAsync(Guid sid, string token, byte[] bytes)
    {
        var wopi = _f.CreateClient();
        var lockReq = new HttpRequestMessage(HttpMethod.Post, $"/wopi/files/{sid}?access_token={token}");
        lockReq.Headers.Add("X-WOPI-Override", "LOCK");
        lockReq.Headers.Add("X-WOPI-Lock", "L1");
        (await wopi.SendAsync(lockReq)).EnsureSuccessStatusCode();

        var putReq = new HttpRequestMessage(HttpMethod.Post, $"/wopi/files/{sid}/contents?access_token={token}")
        {
            Content = new ByteArrayContent(bytes),
        };
        putReq.Headers.Add("X-WOPI-Lock", "L1");
        var put = await wopi.SendAsync(putReq);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        return Guid.Parse(put.Headers.GetValues("X-WOPI-ItemVersion").Single());
    }

    [Fact]
    public async Task Two_sessions_from_same_head_produce_two_branches()
    {
        var c = await AuthedClientAsync();
        var (docId, headVid) = await DocWithHeadAsync(c, new byte[] { 5, 0 }); // H = 0.0.1
        var (sidA, tokA) = await MintSessionAsync(c, headVid);
        var (sidB, tokB) = await MintSessionAsync(c, headVid);

        var vA = await WopiSaveAsync(sidA, tokA, new byte[] { 5, 1 }); // base==head -> fast-forward main (0.0.2)
        var vB = await WopiSaveAsync(sidB, tokB, new byte[] { 5, 2 }); // base stale -> new concurrent branch (0.0.3)

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var branches = await db.Branches.Where(b => b.DocumentId == docId).OrderBy(b => b.Ordinal).ToListAsync();
        Assert.Equal(2, branches.Count);
        var main = branches[0];
        var concurrent = branches[1];
        Assert.Equal(0, main.Ordinal);
        Assert.Equal(1, concurrent.Ordinal);
        Assert.Equal(BranchKind.Concurrent, concurrent.Kind);
        Assert.Equal(headVid, concurrent.RootVersionId);

        var verA = await db.Versions.FirstAsync(v => v.Id == vA);
        var verB = await db.Versions.FirstAsync(v => v.Id == vB);
        Assert.Equal(main.Id, verA.BranchId);
        Assert.Equal(concurrent.Id, verB.BranchId);
        Assert.Equal(headVid, verB.ParentVersionId);
        Assert.True(await db.Blobs.AnyAsync(bl => bl.Sha256 == verA.BlobSha256));
        Assert.True(await db.Blobs.AnyAsync(bl => bl.Sha256 == verB.BlobSha256));

        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        Assert.Equal(3, doc.VersionCounterRev);
    }

    [Fact]
    public async Task Session_pins_to_its_branch_after_first_stale_commit()
    {
        var c = await AuthedClientAsync();
        var (docId, headVid) = await DocWithHeadAsync(c, new byte[] { 6, 0 });
        var (sidA, tokA) = await MintSessionAsync(c, headVid);
        var (sidB, tokB) = await MintSessionAsync(c, headVid);

        await WopiSaveAsync(sidA, tokA, new byte[] { 6, 1 }); // fast-forward main
        var vB1 = await WopiSaveAsync(sidB, tokB, new byte[] { 6, 2 }); // -> new concurrent branch
        var vB2 = await WopiSaveAsync(sidB, tokB, new byte[] { 6, 3 }); // fast-forward on ITS branch

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        Assert.Equal(2, await db.Branches.CountAsync(b => b.DocumentId == docId)); // no third branch

        var b1 = await db.Versions.FirstAsync(v => v.Id == vB1);
        var b2 = await db.Versions.FirstAsync(v => v.Id == vB2);
        Assert.Equal(b1.BranchId, b2.BranchId);
        Assert.Equal(1, b1.SeqInBranch);
        Assert.Equal(2, b2.SeqInBranch);
        Assert.Equal(b1.Id, b2.ParentVersionId);
    }

    [Fact]
    public async Task Wopi_reput_same_bytes_on_branch_is_deduped()
    {
        var c = await AuthedClientAsync();
        var (docId, headVid) = await DocWithHeadAsync(c, new byte[] { 7, 0 });
        var (sidA, tokA) = await MintSessionAsync(c, headVid);
        var (sidB, tokB) = await MintSessionAsync(c, headVid);

        await WopiSaveAsync(sidA, tokA, new byte[] { 7, 1 }); // fast-forward main
        var vB = await WopiSaveAsync(sidB, tokB, new byte[] { 7, 2 }); // -> concurrent branch
        var vBAgain = await WopiSaveAsync(sidB, tokB, new byte[] { 7, 2 }); // identical -> deduped

        Assert.Equal(vB, vBAgain);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var branchB = (await db.Versions.FirstAsync(v => v.Id == vB)).BranchId;
        Assert.Equal(1, await db.Versions.CountAsync(v => v.BranchId == branchB));
    }

    [Fact]
    public async Task Second_save_of_same_sha_creates_no_new_version()
    {
        var c = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);

        var up1 = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(new byte[] { 9, 9, 9 }));
        Assert.Equal(HttpStatusCode.Created, up1.StatusCode);
        var v1 = (await up1.Content.ReadFromJsonAsync<UploadDto>())!;
        Assert.Equal((0, 0, 1), (v1.Major, v1.Minor, v1.Revision));

        // Identical bytes -> no new version.
        var up2 = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(new byte[] { 9, 9, 9 }));
        Assert.Equal(HttpStatusCode.Created, up2.StatusCode);
        var v2 = (await up2.Content.ReadFromJsonAsync<UploadDto>())!;
        Assert.Equal(v1.VersionId, v2.VersionId);

        var versions = await c.GetFromJsonAsync<VersionPage>($"/api/v1/documents/{docId}/versions");
        Assert.Single(versions!.Items);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        Assert.Equal(1, doc.VersionCounterRev);
    }

    [Fact]
    public async Task Import_creates_next_revision_source_import()
    {
        var c = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);

        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(new byte[] { 1, 1, 1 }));
        var head = (await up.Content.ReadFromJsonAsync<UploadDto>())!;
        Assert.Equal((0, 0, 1), (head.Major, head.Minor, head.Revision));

        var imp = await c.PostAsync($"/api/v1/documents/{docId}/versions:import", Docx(new byte[] { 2, 2, 2 }));
        Assert.Equal(HttpStatusCode.Created, imp.StatusCode);
        var iv = (await imp.Content.ReadFromJsonAsync<UploadDto>())!;
        Assert.Equal((0, 0, 2), (iv.Major, iv.Minor, iv.Revision));

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var imported = await db.Versions.FirstAsync(v => v.Id == iv.VersionId);
        Assert.Equal(VersionSource.Import, imported.Source);
        Assert.Equal(head.VersionId, imported.ParentVersionId);
    }

    [Fact]
    public async Task Fast_forward_saves_advance_seq_on_main()
    {
        var c = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);

        var up1 = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(new byte[] { 4, 0 }));
        var v1 = (await up1.Content.ReadFromJsonAsync<UploadDto>())!;
        var up2 = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(new byte[] { 4, 1 }));
        var v2 = (await up2.Content.ReadFromJsonAsync<UploadDto>())!;

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var main = await db.Branches.FirstAsync(bch => bch.DocumentId == docId && bch.Ordinal == 0);
        var ver1 = await db.Versions.FirstAsync(v => v.Id == v1.VersionId);
        var ver2 = await db.Versions.FirstAsync(v => v.Id == v2.VersionId);
        Assert.Equal(main.Id, ver1.BranchId);
        Assert.Equal(main.Id, ver2.BranchId);
        Assert.Equal(1, ver1.SeqInBranch);
        Assert.Equal(2, ver2.SeqInBranch);
        Assert.Equal(ver1.Id, ver2.ParentVersionId);
    }
}
