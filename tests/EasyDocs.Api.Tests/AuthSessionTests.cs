using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

// Sign-out and org-switching: the two halves of "which session am I, and can I end it".
//
// These clients deliberately use the COOKIE rather than TestAuth's Bearer header. The whole point of
// logout is what happens to the browser's `ed_session` cookie, and a Bearer client cannot observe it.
//
// Hence the https base address: `ed_session` is Secure, and a CookieContainer will not return a Secure
// cookie to an http:// origin — over the default base address every one of these tests would 401 for a
// reason that has nothing to do with what they assert. TestServer does no real TLS; the scheme is all
// the cookie policy looks at.
public class AuthSessionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AuthSessionTests(ApiFactory f) => _f = f;

    private HttpClient Browser() =>
        _f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private record OrgRow(Guid Id, string Name, string Slug, string MyRole, bool Current);
    private record OrgList(List<OrgRow> Items);
    private record RegisterDto(Guid Id, string Email, string DisplayName, Guid OrgId);

    private async Task<(HttpClient Client, RegisterDto Dto, string Email, string Password)> RegisterCookieAsync(
        string? email = null)
    {
        var client = Browser(); // WebApplicationFactory clients keep a cookie container
        email ??= $"sess-{Guid.NewGuid():N}@example.com";
        const string password = "pw-at-least-12";
        var res = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            displayName = "Sess",
            password,
            orgName = $"Org-{Guid.NewGuid():N}",
        });
        res.EnsureSuccessStatusCode();
        return (client, (await res.Content.ReadFromJsonAsync<RegisterDto>())!, email, password);
    }

    // The regression this exists for: sign-out used to be client-side only. The cookie survived, so a
    // reload — or the Back button on a shared machine — put the previous user straight back in.
    [Fact]
    public async Task Logout_ends_the_session_so_me_stops_answering()
    {
        var (client, _, _, _) = await RegisterCookieAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);

        var bye = await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, bye.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    // A Set-Cookie deletion the browser ignores is indistinguishable from no logout at all: the delete
    // has to present the same Path (and flags) the append used, which is why one shared CookieOptions
    // now backs every writer of this cookie.
    [Fact]
    public async Task Logout_expires_the_cookie_on_the_same_path_it_was_set()
    {
        var (client, _, _, _) = await RegisterCookieAsync();

        var bye = await client.PostAsync("/api/v1/auth/logout", null);
        var cookie = bye.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith("ed_session=", StringComparison.Ordinal));

        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase); // i.e. in the past
    }

    // Signing out must not depend on being signed in — a stale or corrupt cookie is exactly when the
    // user most needs the button to work, and a 401 here would strand them.
    [Fact]
    public async Task Logout_without_a_session_still_succeeds()
    {
        var res = await Browser().PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task Orgs_lists_only_my_memberships_and_marks_the_current_one()
    {
        var (client, dto, _, _) = await RegisterCookieAsync();
        var (_, other, _, _) = await RegisterCookieAsync(); // a stranger's org, must not appear

        var list = (await client.GetFromJsonAsync<OrgList>("/api/v1/orgs"))!;

        Assert.Single(list.Items);
        Assert.Equal(dto.OrgId, list.Items[0].Id);
        Assert.True(list.Items[0].Current);
        Assert.DoesNotContain(list.Items, o => o.Id == other.OrgId);
    }

    // The invited-colleague regression. Accepting an invitation rebinds the session to the inviting org,
    // but Login deterministically picks the caller's OLDEST membership — which for anyone who registered
    // before being invited is their own org. Without a switch they could reach the org that invited them
    // for exactly one session, and every collaborative feature became unreachable after a logout.
    [Fact]
    public async Task A_member_of_two_orgs_can_switch_between_them_after_signing_in_again()
    {
        var (_, hostDto, _, _) = await RegisterCookieAsync();
        var (_, guestDto, guestEmail, guestPassword) = await RegisterCookieAsync();

        // Stand in for "accepted an invitation into the host's org".
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            db.Add(new OrgMember
            {
                OrgId = hostDto.OrgId,
                UserId = guestDto.Id,
                Role = OrgRole.Member,
                CreatedAt = DateTimeOffset.UtcNow, // newer than their own org: Login will not pick it
            });
            await db.SaveChangesAsync();
        }

        // A fresh sign-in, i.e. the next day. Lands in their own org, as Login documents.
        var again = Browser();
        (await again.PostAsJsonAsync("/api/v1/auth/login",
            new { email = guestEmail, password = guestPassword })).EnsureSuccessStatusCode();

        var list = (await again.GetFromJsonAsync<OrgList>("/api/v1/orgs"))!;
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(guestDto.OrgId, list.Items.Single(o => o.Current).Id);

        var switched = await again.PostAsJsonAsync("/api/v1/auth/switch-org", new { orgId = hostDto.OrgId });
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);

        // The new cookie is what proves it: the session now answers as the host org.
        var after = (await again.GetFromJsonAsync<OrgList>("/api/v1/orgs"))!;
        Assert.Equal(hostDto.OrgId, after.Items.Single(o => o.Current).Id);
    }

    // This endpoint mints a session for an org, so an unverified orgId would be a straight cross-org
    // escalation. 404 rather than 403: a non-member must not learn the org exists.
    [Fact]
    public async Task Switching_to_an_org_you_do_not_belong_to_is_404()
    {
        var (client, _, _, _) = await RegisterCookieAsync();
        var (_, stranger, _, _) = await RegisterCookieAsync();

        var res = await client.PostAsJsonAsync("/api/v1/auth/switch-org", new { orgId = stranger.OrgId });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        var res2 = await client.PostAsJsonAsync("/api/v1/auth/switch-org", new { orgId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, res2.StatusCode);
    }

    [Fact]
    public async Task Orgs_and_switch_require_a_session()
    {
        var anon = Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/orgs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/v1/auth/switch-org", new { orgId = Guid.NewGuid() })).StatusCode);
    }
}
