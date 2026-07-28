using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EasyDocs.Api.Tests;

public record Account(HttpClient Client, Guid UserId, Guid OrgId, string Email);

// Shared registration / PAT / upload helpers for the M3 test files and the conformance suite.
// The M0-M2 test files keep their own local copies; not worth churning them.
public static class TestAuth
{
    public const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private record RegisterDto(Guid Id, string Email, string DisplayName, Guid OrgId);
    private record TokenDto(Guid Id, string Token);
    private record IdDto(Guid Id);

    // Registers a fresh user + org; the returned client carries the session JWT as a Bearer header.
    public static async Task<Account> RegisterAsync(this ApiFactory f, string? email = null)
    {
        var client = f.CreateClient();
        email ??= $"u-{Guid.NewGuid():N}@example.com";
        var res = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            displayName = "U",
            password = "pw-at-least-12",
            orgName = $"Org-{Guid.NewGuid():N}",
        });
        res.EnsureSuccessStatusCode();
        var dto = (await res.Content.ReadFromJsonAsync<RegisterDto>())!;

        var cookie = res.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session=", StringComparison.Ordinal));
        var jwt = cookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        return new Account(client, dto.Id, dto.OrgId, email);
    }

    // A separate client for the same account authenticated with an `ed_` PAT instead of the session JWT.
    // This is how the conformance suite proves the API drives everything unattended.
    public static async Task<HttpClient> PatClientAsync(this ApiFactory f, HttpClient authed, string name = "conformance")
    {
        var res = await authed.PostAsJsonAsync("/api/v1/tokens", new { name, scopes = Array.Empty<string>() });
        res.EnsureSuccessStatusCode();
        var raw = (await res.Content.ReadFromJsonAsync<TokenDto>())!.Token;

        var client = f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);
        return client;
    }

    // Seeds a second user directly into an existing org and logs them in. Registration always creates a
    // *new* org, so until POST /invitations/{token}:accept lands this is the only way to get two members
    // of one org — which E12's role matrix needs (a same-org non-member must be distinguishable from a
    // cross-org caller: 403 vs 404).
    public static async Task<Account> SeedOrgUserAsync(this ApiFactory f, Guid orgId, OrgRole role = OrgRole.Member)
    {
        var email = $"seed-{Guid.NewGuid():N}@example.com";
        const string password = "pw-at-least-12";
        Guid userId;

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var now = DateTimeOffset.UtcNow;
            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                DisplayName = "Seed",
                PasswordHash = hasher.Hash(password),
                CreatedAt = now,
            };
            db.Add(user);
            db.Add(new OrgMember { OrgId = orgId, UserId = user.Id, Role = role, CreatedAt = now });
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var client = f.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();
        var cookie = res.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session=", StringComparison.Ordinal));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", cookie["ed_session=".Length..].Split(';')[0]);

        return new Account(client, userId, orgId, email);
    }

    // Directly sets a caller's role on a document, for tests that need to observe a role they cannot
    // yet reach through the API.
    public static async Task SetRoleAsync(this ApiFactory f, Guid docId, Guid userId, DocRole role)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var m = await db.DocumentMembers.SingleAsync(x => x.DocumentId == docId && x.UserId == userId);
        m.Role = role;
        await db.SaveChangesAsync();
    }

    public static MultipartFormDataContent DocxForm(byte[]? bytes = null, string fileName = "f.docx")
    {
        var part = new ByteArrayContent(bytes ?? DocxFixtures.Base());
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        return new MultipartFormDataContent { { part, "file", fileName } };
    }

    public static async Task<Guid> CreateDocAsync(this HttpClient c, string name = "Doc", Guid? folderId = null)
    {
        var res = await c.PostAsJsonAsync("/api/v1/documents", new { name, folderId });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private record UploadDto(Guid VersionId, int Major, int Minor, int Revision);

    // Uploads a .docx and returns the new version id plus its X.Y.Z numbers.
    public static async Task<(Guid VersionId, string Number)> UploadAsync(
        this HttpClient c, Guid docId, byte[]? bytes = null)
    {
        var res = await c.PostAsync($"/api/v1/documents/{docId}/versions", DocxForm(bytes));
        res.EnsureSuccessStatusCode();
        var dto = (await res.Content.ReadFromJsonAsync<UploadDto>())!;
        return (dto.VersionId, $"{dto.Major}.{dto.Minor}.{dto.Revision}");
    }
}
