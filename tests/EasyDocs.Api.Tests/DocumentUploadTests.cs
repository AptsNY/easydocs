using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class DocumentUploadTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public DocumentUploadTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    // A multipart body that never reaches its closing boundary raises IOException from the body reader,
    // not the InvalidDataException a bad Content-Disposition raises -- so this route answered client
    // garbage with a 500 until both were caught. Found while hardening documents:import, which runs the
    // same bodies through the same block.
    [Fact]
    public async Task An_unterminated_multipart_body_is_a_400_and_never_a_500()
    {
        var (client, _) = await AuthedClientAsync();
        var docId = await client.CreateDocAsync("Truncated");

        var body = new StringContent("not a multipart body at all");
        body.Headers.Remove("Content-Type");
        body.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=--nonsense");

        var res = await client.PostAsync($"/api/v1/documents/{docId}/versions", body);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    private async Task<(HttpClient client, Guid userId)> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"doc-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "D", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var body = await reg.Content.ReadFromJsonAsync<RegisterDto>();
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return (client, body!.Id);
    }

    private static MultipartFormDataContent Docx(string name, byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        return new MultipartFormDataContent { { part, "file", name } };
    }

    private record RegisterDto(Guid Id);
    private record DocDto(Guid Id, string Name, Guid? FolderId);
    private record FolderDto(Guid Id);
    private record UploadDto(Guid VersionId, int Major, int Minor, int Revision);
    private record VersionDto(Guid Id, int Major, int Minor, int Revision, string Source);
    private record VersionPage(List<VersionDto> Items, string? NextCursor);

    [Fact]
    public async Task Create_then_upload_produces_first_version_0_0_1()
    {
        var (c, _) = await AuthedClientAsync();

        var create = await c.PostAsJsonAsync("/api/v1/documents", new { name = "Lease" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var docId = (await create.Content.ReadFromJsonAsync<DocDto>())!.Id;

        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions",
            Docx("lease.docx", new byte[] { 1, 2, 3, 4, 5 }));
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var upBody = (await up.Content.ReadFromJsonAsync<UploadDto>())!;
        Assert.Equal((0, 0, 1), (upBody.Major, upBody.Minor, upBody.Revision));

        var versions = await c.GetFromJsonAsync<VersionPage>($"/api/v1/documents/{docId}/versions");
        var v = Assert.Single(versions!.Items);
        Assert.Equal((0, 0, 1), (v.Major, v.Minor, v.Revision));
        Assert.Equal("Upload", v.Source);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var doc = await db.Documents.FirstAsync(d => d.Id == docId);
        Assert.Equal(1, doc.VersionCounterRev);
        var branch = await db.Branches.SingleAsync(bch => bch.DocumentId == docId && bch.Ordinal == 0);
        Assert.Equal(BranchKind.Main, branch.Kind);
        var version = await db.Versions.SingleAsync(vr => vr.DocumentId == docId);
        Assert.True(await db.Blobs.AnyAsync(bl => bl.Sha256 == version.BlobSha256));
    }

    [Fact]
    public async Task Upload_creator_is_document_owner_member()
    {
        var (c, userId) = await AuthedClientAsync();
        var create = await c.PostAsJsonAsync("/api/v1/documents", new { name = "Owned" });
        var docId = (await create.Content.ReadFromJsonAsync<DocDto>())!.Id;

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var member = await db.DocumentMembers.SingleAsync(m => m.DocumentId == docId && m.UserId == userId);
        Assert.Equal(DocRole.Owner, member.Role);
    }

    [Fact]
    public async Task Move_document_between_folders_preserves_document()
    {
        var (c, _) = await AuthedClientAsync();
        var a = (await (await c.PostAsJsonAsync("/api/v1/folders", new { name = "A" }))
            .Content.ReadFromJsonAsync<FolderDto>())!.Id;
        var b = (await (await c.PostAsJsonAsync("/api/v1/folders", new { name = "B" }))
            .Content.ReadFromJsonAsync<FolderDto>())!.Id;

        var docId = (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Movable", folderId = a }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;

        var move = await c.PatchAsJsonAsync($"/api/v1/documents/{docId}", new { folderId = b });
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);

        var doc = await c.GetFromJsonAsync<DocDto>($"/api/v1/documents/{docId}");
        Assert.Equal(docId, doc!.Id);
        Assert.Equal(b, doc.FolderId);
    }
}
