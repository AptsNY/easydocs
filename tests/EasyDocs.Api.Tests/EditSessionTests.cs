using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Editing;
using EasyDocs.Api.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public class EditSessionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public EditSessionTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private async Task<(HttpClient client, Guid userId)> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"sess-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "D", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var body = await reg.Content.ReadFromJsonAsync<RegisterDto>();
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return (client, body!.Id);
    }

    // Create a document + first version, returning (docId, versionId).
    private static async Task<(Guid docId, Guid versionId)> CreateDocWithVersionAsync(HttpClient c)
    {
        var docId = (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Lease" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var part = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 });
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        var form = new MultipartFormDataContent { { part, "file", "lease.docx" } };
        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions", form);
        up.EnsureSuccessStatusCode();
        return (docId, (await up.Content.ReadFromJsonAsync<UploadDto>())!.VersionId);
    }

    private record RegisterDto(Guid Id);
    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId);
    private record MintDto(Guid SessionId, string EditorUrl, string AccessToken, int AccessTokenTtlSeconds);

    [Fact]
    public async Task Editor_mints_session_with_editor_url_and_token()
    {
        var (c, _) = await AuthedClientAsync();
        var (_, vid) = await CreateDocWithVersionAsync(c);

        var resp = await c.PostAsync($"/api/v1/versions/{vid}/sessions", null);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var mint = (await resp.Content.ReadFromJsonAsync<MintDto>())!;

        Assert.NotEqual(Guid.Empty, mint.SessionId);
        Assert.False(string.IsNullOrEmpty(mint.AccessToken));
        Assert.Equal(1800, mint.AccessTokenTtlSeconds);
        Assert.Contains($"WOPISrc=http://localhost/wopi/files/{mint.SessionId}", mint.EditorUrl);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var session = await db.EditSessions.SingleAsync(s => s.Id == mint.SessionId);
        Assert.Equal(vid, session.BaseVersionId);
    }

    [Fact]
    public async Task Viewer_cannot_mint_session_403()
    {
        var (owner, _) = await AuthedClientAsync();
        var (docId, vid) = await CreateDocWithVersionAsync(owner);

        // A second real user, added to the owner's document as Viewer, with a JWT scoped to the doc's org.
        var (_, viewerId) = await AuthedClientAsync();
        Guid orgId;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            orgId = (await db.Documents.SingleAsync(d => d.Id == docId)).OrgId;
            db.DocumentMembers.Add(new DocumentMember
            {
                DocumentId = docId, UserId = viewerId, Role = DocRole.Viewer, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var jwt = _f.Services.GetRequiredService<JwtService>().Issue(viewerId, orgId);
        var viewer = _f.CreateClient();
        viewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var resp = await viewer.PostAsync($"/api/v1/versions/{vid}/sessions", null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public void Access_token_roundtrips_sid_uid_perms()
    {
        var wopi = new WopiAccessToken(TestConfig());
        var sid = Guid.NewGuid();
        var uid = Guid.NewGuid();

        var token = wopi.Issue(sid, uid, "w");
        var parsed = wopi.Validate(token);

        Assert.NotNull(parsed);
        Assert.Equal((sid, uid, "w"), (parsed!.Value.Sid, parsed.Value.Uid, parsed.Value.Perms));
    }

    [Fact]
    public void Login_jwt_is_rejected_as_wopi_token()
    {
        var cfg = TestConfig();
        var login = new JwtService(cfg).Issue(Guid.NewGuid(), Guid.NewGuid());
        Assert.Null(new WopiAccessToken(cfg).Validate(login));
    }

    [Fact]
    public async Task Close_session_sets_closed_at()
    {
        var (c, _) = await AuthedClientAsync();
        var (_, vid) = await CreateDocWithVersionAsync(c);
        var mint = (await (await c.PostAsync($"/api/v1/versions/{vid}/sessions", null))
            .Content.ReadFromJsonAsync<MintDto>())!;

        var del = await c.DeleteAsync($"/api/v1/sessions/{mint.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var session = await db.EditSessions.SingleAsync(s => s.Id == mint.SessionId);
        Assert.NotNull(session.ClosedAt);
    }

    private static IConfiguration TestConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "test-secret-at-least-32-bytes-long-xxxxx",
        }).Build();
}
