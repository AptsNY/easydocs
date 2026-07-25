using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using EasyDocs.Api.Tests;

public class AuthTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AuthTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task Register_creates_user_org_and_owner_membership()
    {
        var client = _f.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email = "rob@example.com", displayName = "Rob", password = "pw-at-least-12", orgName = "Aces" });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == "rob@example.com");
        Assert.NotNull(user.PasswordHash);                     // hash present, never plaintext
        Assert.NotEqual("pw-at-least-12", user.PasswordHash);
        var member = await db.OrgMembers.SingleAsync(m => m.UserId == user.Id);
        Assert.Equal(OrgRole.Owner, member.Role);
    }

    [Fact]
    public async Task Register_duplicate_email_returns_409()
    {
        var client = _f.CreateClient();
        var body = new { email = "dup@example.com", displayName = "D", password = "pw-at-least-12", orgName = "X" };
        await client.PostAsJsonAsync("/api/v1/auth/register", body);
        var res = await client.PostAsJsonAsync("/api/v1/auth/register", body);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Register_sets_session_cookie()
    {
        var res = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/register",
            new { email = "cookie@example.com", displayName = "C", password = "pw-at-least-12", orgName = "CookieOrg" });
        Assert.Contains(res.Headers.GetValues("Set-Cookie"), c => c.StartsWith("ed_session="));
    }
}
