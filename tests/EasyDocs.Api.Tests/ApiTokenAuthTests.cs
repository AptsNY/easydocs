using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// Task 2: a request with `Authorization: Bearer ed_...` authenticates as the token's OWNER and never
// exceeds the owner's document role; the JWT/cookie scheme keeps working alongside it.
public class ApiTokenAuthTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ApiTokenAuthTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private record RegisterDto(Guid Id);
    private record DocDto(Guid Id);
    private record CreateTokenDto(Guid Id, string Token);
    private record MeDto(Guid Id, Guid OrgId);

    // Register a fresh user/org; return a JWT-authed client + the user id (used only to mint ed_ tokens).
    private async Task<(HttpClient jwt, Guid userId)> JwtClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"pat-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "U", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var body = await reg.Content.ReadFromJsonAsync<RegisterDto>();
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return (client, body!.Id);
    }

    private static async Task<CreateTokenDto> MintAsync(HttpClient jwt, DateTimeOffset? expiresAt = null)
    {
        var res = await jwt.PostAsJsonAsync("/api/v1/tokens",
            new { name = "pat", scopes = new[] { "documents:write" }, expiresAt });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<CreateTokenDto>())!;
    }

    private HttpClient EdClient(string raw)
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);
        return c;
    }

    private static MultipartFormDataContent Docx()
    {
        var part = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 });
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        return new MultipartFormDataContent { { part, "file", "f.docx" } };
    }

    [Fact]
    public async Task Ed_token_authenticates_protected_endpoint()
    {
        var (jwt, userId) = await JwtClientAsync();
        var raw = (await MintAsync(jwt)).Token;

        var res = await EdClient(raw).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var me = await res.Content.ReadFromJsonAsync<MeDto>();
        Assert.Equal(userId, me!.Id); // identity resolves to the token owner
    }

    [Fact]
    public async Task Revoked_or_expired_ed_token_401()
    {
        var (jwt, _) = await JwtClientAsync();

        // Revoked
        var revoked = await MintAsync(jwt);
        (await jwt.DeleteAsync($"/api/v1/tokens/{revoked.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await EdClient(revoked.Token).GetAsync("/api/v1/me")).StatusCode);

        // Expired
        var expired = await MintAsync(jwt, DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await EdClient(expired.Token).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Ed_token_owner_role_is_enforced()
    {
        var (jwt, userId) = await JwtClientAsync();
        var docId = (await (await jwt.PostAsJsonAsync("/api/v1/documents", new { name = "Doc" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;

        // Demote the owner to Viewer on their own doc.
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var m = await db.DocumentMembers.SingleAsync(x => x.DocumentId == docId && x.UserId == userId);
            m.Role = DocRole.Viewer;
            await db.SaveChangesAsync();
        }

        var raw = (await MintAsync(jwt)).Token;
        var up = await EdClient(raw).PostAsync($"/api/v1/documents/{docId}/versions", Docx());
        Assert.Equal(HttpStatusCode.Forbidden, up.StatusCode); // token cannot escalate beyond owner's Viewer role
    }

    [Fact]
    public async Task Existing_jwt_cookie_auth_still_works()
    {
        var (jwt, _) = await JwtClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await jwt.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Garbage_bearer_401()
        => Assert.Equal(HttpStatusCode.Unauthorized,
            (await EdClient("ed_garbage").GetAsync("/api/v1/me")).StatusCode);
}
