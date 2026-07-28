using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class RevertTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public RevertTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private async Task<(HttpClient client, Guid userId)> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"rev-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "D", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var user = await db.Users.OrderByDescending(u => u.CreatedAt).FirstAsync(u => u.Email == email);
        return (client, user.Id);
    }

    private static MultipartFormDataContent Docx(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        return new MultipartFormDataContent { { part, "file", "d.docx" } };
    }

    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId, int Major, int Minor, int Revision);

    private static async Task<Guid> CreateDocAsync(HttpClient c)
    {
        var create = await c.PostAsJsonAsync("/api/v1/documents", new { name = "Doc" });
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<DocDto>())!.Id;
    }

    private static async Task<UploadDto> UploadAsync(HttpClient c, Guid docId, byte[] bytes)
    {
        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(bytes));
        up.EnsureSuccessStatusCode();
        return (await up.Content.ReadFromJsonAsync<UploadDto>())!;
    }

    private async Task MakeViewerAsync(Guid docId, Guid userId)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var m = await db.DocumentMembers.SingleAsync(x => x.DocumentId == docId && x.UserId == userId);
        m.Role = DocRole.Viewer;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Name_this_version_sets_label()
    {
        var (c, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);
        var v = await UploadAsync(c, docId, new byte[] { 1, 2, 3 });

        var res = await c.PatchAsJsonAsync($"/api/v1/versions/{v.VersionId}", new { name = "Post-legal-review" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var stored = await db.Versions.FirstAsync(x => x.Id == v.VersionId);
        Assert.Equal("Post-legal-review", stored.Name);
    }

    [Fact]
    public async Task Revert_creates_new_head_equal_to_target_content_history_intact()
    {
        var (c, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);
        var a = await UploadAsync(c, docId, new byte[] { 1 });
        var b = await UploadAsync(c, docId, new byte[] { 2 });
        var cc = await UploadAsync(c, docId, new byte[] { 3 });
        Assert.Equal((0, 0, 1), (a.Major, a.Minor, a.Revision));
        Assert.Equal((0, 0, 3), (cc.Major, cc.Minor, cc.Revision));

        var res = await c.PostAsync($"/api/v1/versions/{a.VersionId}/revert", null);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var head = (await res.Content.ReadFromJsonAsync<UploadDto>())!;
        Assert.Equal((0, 0, 4), (head.Major, head.Minor, head.Revision));

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var aSha = await db.Versions.Where(x => x.Id == a.VersionId).Select(x => x.BlobSha256).FirstAsync();
        var newHead = await db.Versions.FirstAsync(x => x.Id == head.VersionId);
        Assert.Equal(aSha, newHead.BlobSha256);
        Assert.Equal(VersionSource.Revert, newHead.Source);

        // History intact: A, B, C and the new head all still exist.
        var all = await db.Versions.Where(x => x.DocumentId == docId).Select(x => x.Id).ToListAsync();
        Assert.Contains(a.VersionId, all);
        Assert.Contains(b.VersionId, all);
        Assert.Contains(cc.VersionId, all);
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public async Task Name_and_revert_require_editor()
    {
        var (c, userId) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);
        var v = await UploadAsync(c, docId, new byte[] { 1 });
        await MakeViewerAsync(docId, userId);

        var name = await c.PatchAsJsonAsync($"/api/v1/versions/{v.VersionId}", new { name = "X" });
        Assert.Equal(HttpStatusCode.Forbidden, name.StatusCode);

        var revert = await c.PostAsync($"/api/v1/versions/{v.VersionId}/revert", null);
        Assert.Equal(HttpStatusCode.Forbidden, revert.StatusCode);
    }
}
