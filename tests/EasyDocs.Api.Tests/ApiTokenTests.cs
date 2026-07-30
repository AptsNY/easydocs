using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using EasyDocs.Api.Tests;

public class ApiTokenTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ApiTokenTests(ApiFactory f) => _f = f;

    // Register, then send the issued JWT as Bearer (Secure cookies aren't carried over http in tests).
    private async Task<HttpClient> AuthedClientAsync(string org)
    {
        var client = _f.CreateClient();
        var email = $"{org}-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "T", password = "pw-at-least-12", orgName = org });
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    private static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [Fact]
    public async Task Create_token_returns_raw_token_once_and_stores_only_hash()
    {
        var client = await AuthedClientAsync("MintOrg");
        var res = await client.PostAsJsonAsync("/api/v1/tokens",
            new { name = "ci", scopes = new[] { "documents:read" } });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<CreateResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.Id);
        Assert.StartsWith("ed_", body.Token);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var row = await db.ApiTokens.SingleAsync(t => t.Id == body.Id);
        Assert.NotEqual(body.Token, row.TokenHash);          // raw never stored
        Assert.Equal(Sha256Hex(body.Token), row.TokenHash);  // stored hash is SHA-256 hex of raw
        Assert.Equal(new[] { "documents:read" }, row.Scopes);
    }

    [Fact]
    public async Task List_tokens_never_returns_secret()
    {
        var client = await AuthedClientAsync("ListOrg");
        await client.PostAsJsonAsync("/api/v1/tokens",
            new { name = "svc", scopes = new[] { "documents:read" } });

        var res = await client.GetAsync("/api/v1/tokens");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var raw = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase); // no token/tokenHash field
        Assert.Contains("svc", raw);
    }

    [Fact]
    public async Task Delete_revokes_token()
    {
        var client = await AuthedClientAsync("RevokeOrg");
        var created = await (await client.PostAsJsonAsync("/api/v1/tokens",
            new { name = "toRevoke", scopes = new[] { "documents:read" } }))
            .Content.ReadFromJsonAsync<CreateResponse>();

        var del = await client.DeleteAsync($"/api/v1/tokens/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var row = await db.ApiTokens.SingleAsync(t => t.Id == created.Id);
        Assert.NotNull(row.RevokedAt);
    }

    // TokenHash is looked up on every `ed_` request, so it carries a unique index like the sibling
    // token-hash columns on ShareLinks/Invitations (spec §11). Mint() can't collide, but the schema
    // is what guarantees it — and the index is also what keeps PAT auth off a table scan.
    [Fact]
    public async Task Duplicate_token_hash_is_rejected_by_the_database()
    {
        var client = await AuthedClientAsync("DupOrg");
        var created = await (await client.PostAsJsonAsync("/api/v1/tokens",
            new { name = "first", scopes = new[] { "documents:read" } }))
            .Content.ReadFromJsonAsync<CreateResponse>();

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var row = await db.ApiTokens.SingleAsync(t => t.Id == created!.Id);

        db.ApiTokens.Add(new ApiToken
        {
            OrgId = row.OrgId,
            UserId = row.UserId,
            ServiceName = row.ServiceName,
            TokenHash = row.TokenHash, // same hash as an existing row
            Scopes = row.Scopes,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // Spec §11 treats a PAT as a per-user capability whose authority never exceeds its owner's. The list
    // was `Where(t => t.OrgId == orgId)`, so any Member could enumerate every colleague's token names,
    // scopes, creation and last-used times — never a value, but not a per-user capability either.
    [Fact]
    public async Task List_shows_only_the_callers_own_tokens()
    {
        var owner = await _f.RegisterAsync();
        var member = await _f.SeedOrgUserAsync(owner.OrgId);

        await owner.Client.PostAsJsonAsync("/api/v1/tokens", new { name = "owners-ci", scopes = Array.Empty<string>() });
        await member.Client.PostAsJsonAsync("/api/v1/tokens", new { name = "members-laptop", scopes = Array.Empty<string>() });

        var mine = await member.Client.GetStringAsync("/api/v1/tokens");
        Assert.Contains("members-laptop", mine);
        Assert.DoesNotContain("owners-ci", mine);

        // Symmetric: an org Owner does not get to read a member's tokens either — the point is ownership,
        // not seniority.
        var theirs = await owner.Client.GetStringAsync("/api/v1/tokens");
        Assert.Contains("owners-ci", theirs);
        Assert.DoesNotContain("members-laptop", theirs);
    }

    // A scoped list with an unscoped delete would be theatre: Revoke checked only OrgId, so a Member who
    // guessed or remembered an id could kill a colleague's token.
    [Fact]
    public async Task Delete_refuses_a_token_the_caller_does_not_own()
    {
        var owner = await _f.RegisterAsync();
        var member = await _f.SeedOrgUserAsync(owner.OrgId);

        var created = await (await owner.Client.PostAsJsonAsync("/api/v1/tokens",
            new { name = "not-yours", scopes = Array.Empty<string>() })).Content.ReadFromJsonAsync<CreateResponse>();

        // 404, not 403: it is not listed for this caller, so it does not exist for them.
        Assert.Equal(HttpStatusCode.NotFound, (await member.Client.DeleteAsync($"/api/v1/tokens/{created!.Id}")).StatusCode);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        Assert.Null((await db.ApiTokens.SingleAsync(t => t.Id == created.Id)).RevokedAt);
    }

    // ApiToken.UserId is nullable — an org-level service token has no owning user, so scoping the list to
    // `UserId == caller` alone would make one invisible to everybody. Org Owner/Admin manage those, the
    // same pair that manages the org itself (OrgEndpoints).
    [Fact]
    public async Task Service_tokens_belong_to_the_org_owners_and_admins()
    {
        var owner = await _f.RegisterAsync();
        var member = await _f.SeedOrgUserAsync(owner.OrgId);

        Guid serviceTokenId;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var row = new ApiToken
            {
                OrgId = owner.OrgId,
                UserId = null, // service account: nobody's personal capability
                ServiceName = "nightly-export",
                TokenHash = Sha256Hex($"svc-{Guid.NewGuid():N}"),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Add(row);
            await db.SaveChangesAsync();
            serviceTokenId = row.Id;
        }

        Assert.Contains("nightly-export", await owner.Client.GetStringAsync("/api/v1/tokens"));
        Assert.DoesNotContain("nightly-export", await member.Client.GetStringAsync("/api/v1/tokens"));

        Assert.Equal(HttpStatusCode.NotFound,
            (await member.Client.DeleteAsync($"/api/v1/tokens/{serviceTokenId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.Client.DeleteAsync($"/api/v1/tokens/{serviceTokenId}")).StatusCode);
    }

    [Fact]
    public void Verify_matches_only_the_right_token()
    {
        var svc = new ApiTokenService();
        var (raw, hash) = svc.Mint();
        Assert.StartsWith("ed_", raw);
        Assert.Equal(hash, svc.Hash(raw));           // stable
        Assert.NotEqual(hash, svc.Hash(raw + "x"));  // wrong raw -> different hash
    }

    private sealed record CreateResponse(Guid Id, string Token);
}
