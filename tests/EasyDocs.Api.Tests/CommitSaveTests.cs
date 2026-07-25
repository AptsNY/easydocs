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

        var versions = await c.GetFromJsonAsync<List<VersionDto>>($"/api/v1/documents/{docId}/versions");
        Assert.Single(versions!);

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
