using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Data;
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
