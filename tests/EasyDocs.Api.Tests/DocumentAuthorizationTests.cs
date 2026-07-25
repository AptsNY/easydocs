using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class DocumentAuthorizationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public DocumentAuthorizationTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private record RegisterDto(Guid Id);
    private record DocDto(Guid Id, string Name, Guid? FolderId, Guid OrgId);

    private async Task<(HttpClient client, Guid userId)> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"auth-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "U", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var body = await reg.Content.ReadFromJsonAsync<RegisterDto>();
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return (client, body!.Id);
    }

    private static MultipartFormDataContent Docx()
    {
        var part = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 });
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        return new MultipartFormDataContent { { part, "file", "f.docx" } };
    }

    private async Task<Guid> CreateDocAsync(HttpClient c)
        => (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Doc" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;

    private async Task SetRoleAsync(Guid docId, Guid userId, DocRole role)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var m = await db.DocumentMembers.SingleAsync(x => x.DocumentId == docId && x.UserId == userId);
        m.Role = role;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Non_member_same_org_gets_forbidden()
    {
        // Faithful unit check of the chokepoint: same org as the doc, but not a document member -> Forbidden.
        var (a, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(a);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var orgId = (await db.Documents.SingleAsync(d => d.Id == docId)).OrgId;

        var (result, role) = await DocumentAuthorization.ResolveAsync(db, orgId, Guid.NewGuid(), docId);
        Assert.Equal(AccessResult.Forbidden, result);
        Assert.Null(role);
    }

    [Fact]
    public async Task Cross_org_gets_404()
    {
        var (a, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(a);

        var (c, _) = await AuthedClientAsync(); // C is in its own org2
        var res = await c.GetAsync($"/api/v1/documents/{docId}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Viewer_cannot_upload_403()
    {
        var (a, userId) = await AuthedClientAsync();
        var docId = await CreateDocAsync(a);
        await SetRoleAsync(docId, userId, DocRole.Viewer);

        var read = await a.GetAsync($"/api/v1/documents/{docId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode); // viewer may read

        var up = await a.PostAsync($"/api/v1/documents/{docId}/versions", Docx());
        Assert.Equal(HttpStatusCode.Forbidden, up.StatusCode); // viewer may not mutate
    }

    [Fact]
    public async Task Editor_can_upload()
    {
        var (a, userId) = await AuthedClientAsync();
        var docId = await CreateDocAsync(a);
        await SetRoleAsync(docId, userId, DocRole.Editor);

        var up = await a.PostAsync($"/api/v1/documents/{docId}/versions", Docx());
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
    }

    [Fact]
    public async Task Owner_can_read_and_upload()
    {
        var (a, _) = await AuthedClientAsync(); // creator is Owner
        var docId = await CreateDocAsync(a);

        var read = await a.GetAsync($"/api/v1/documents/{docId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var up = await a.PostAsync($"/api/v1/documents/{docId}/versions", Docx());
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
    }
}
