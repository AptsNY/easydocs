using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EasyDocs.Api.Tests;

// Admin-issued password reset (spec 2026-09-08). easydocs has no mailer, so an Owner or Admin mints a
// single-use link and relays it out of band.
public class PasswordResetTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public PasswordResetTests(ApiFactory f) => _f = f;

    private record MintDto(string Token, string Url, DateTimeOffset ExpiresAt);

    private static Task<HttpResponseMessage> MintAsync(HttpClient c, Guid uid) =>
        c.PostAsync($"/api/v1/org/members/{uid}/password-reset", null);

    private static async Task<MintDto> MintOkAsync(HttpClient c, Guid uid)
    {
        var res = await MintAsync(c, uid);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<MintDto>())!;
    }

    [Fact]
    public async Task Member_may_not_mint()
    {
        var owner = await _f.RegisterAsync();
        var member = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        Assert.Equal(HttpStatusCode.Forbidden, (await MintAsync(member.Client, target.UserId)).StatusCode);
    }

    // The escalation gate. An Admin who could reset an Owner would sign in as them and self-promote,
    // which is why this endpoint is NOT gated like Invite (Owner-or-Admin, the weakest org action).
    [Fact]
    public async Task Admin_may_reset_a_member_but_not_an_owner_or_a_peer_admin()
    {
        var owner = await _f.RegisterAsync();
        var admin = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Admin);
        var peer = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Admin);
        var member = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        Assert.Equal(HttpStatusCode.Forbidden, (await MintAsync(admin.Client, owner.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await MintAsync(admin.Client, peer.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MintAsync(admin.Client, member.UserId)).StatusCode);
    }

    [Fact]
    public async Task Owner_may_reset_an_admin_and_an_owner()
    {
        var owner = await _f.RegisterAsync();
        var admin = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Admin);
        var coOwner = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Owner);

        var dto = await MintOkAsync(owner.Client, admin.UserId);
        Assert.NotEmpty(dto.Token);
        Assert.Equal($"/password-reset/{dto.Token}", dto.Url); // relative, like ShareEndpoints
        Assert.True(dto.ExpiresAt > DateTimeOffset.UtcNow);

        Assert.Equal(HttpStatusCode.OK, (await MintAsync(owner.Client, coOwner.UserId)).StatusCode);
    }

    // 404 not 403: a caller must not learn whether an account they cannot see exists (SwitchOrg's rule).
    [Fact]
    public async Task Target_in_another_org_is_404()
    {
        var owner = await _f.RegisterAsync();
        var stranger = await _f.RegisterAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await MintAsync(owner.Client, stranger.UserId)).StatusCode);
    }

    [Fact]
    public async Task Target_active_on_another_team_is_409()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        // A second org that has someone else in it: its registering owner, plus the target.
        var other = await _f.RegisterAsync();
        await _f.AddOrgMemberAsync(other.OrgId, target.UserId, OrgRole.Member);

        Assert.Equal(HttpStatusCode.Conflict, (await MintAsync(owner.Client, target.UserId)).StatusCode);
    }

    // The gate counts other orgs THAT HAVE SOMEONE ELSE IN THEM. This is the test that fails if anyone
    // simplifies it to "belongs to more than one org" — which would refuse every invited member alive,
    // since Register always hands out a personal org before an invitation can be accepted.
    [Fact]
    public async Task Target_whose_only_other_org_is_their_own_solo_one_succeeds()
    {
        var owner = await _f.RegisterAsync();

        // Exactly what an invited colleague looks like: registered (creating a solo personal org),
        // then added to this org.
        var invitee = await _f.RegisterAsync();
        await _f.AddOrgMemberAsync(owner.OrgId, invitee.UserId, OrgRole.Member);

        Assert.Equal(HttpStatusCode.OK, (await MintAsync(owner.Client, invitee.UserId)).StatusCode);
    }

    [Fact]
    public async Task Sso_only_target_is_409()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        await _f.ClearPasswordHashAsync(target.UserId);

        Assert.Equal(HttpStatusCode.Conflict, (await MintAsync(owner.Client, target.UserId)).StatusCode);
    }

    // ---- consume -------------------------------------------------------------------------------

    private record CompleteRequest(string Token, string Password);

    private Task<HttpResponseMessage> CompleteAsync(string token, string password) =>
        _f.CreateClient().PostAsJsonAsync("/api/v1/auth/password-reset:complete",
            new CompleteRequest(token, password));

    [Fact]
    public async Task Issuing_a_second_link_invalidates_the_first()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        var first = await MintOkAsync(owner.Client, target.UserId);
        await MintOkAsync(owner.Client, target.UserId);

        Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(first.Token, "brand-new-password")).StatusCode);
    }

    [Fact]
    public async Task Consume_sets_a_working_password()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        var dto = await MintOkAsync(owner.Client, target.UserId);

        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(dto.Token, "a-brand-new-password")).StatusCode);

        var login = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = target.Email, password = "a-brand-new-password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // The uniform 404 is only uniform if the BODIES match. Problem.Of takes a free-text detail, and
    // three helpfully-worded details would rebuild the oracle the shared status exists to remove.
    [Fact]
    public async Task Unknown_expired_and_used_tokens_are_byte_identical_404s()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        var used = (await MintOkAsync(owner.Client, target.UserId)).Token;
        await CompleteAsync(used, "first-password-set");

        var expired = (await MintOkAsync(owner.Client, target.UserId)).Token;
        await _f.ExpireResetsAsync(target.UserId);

        var bodies = new List<string>();
        foreach (var t in new[] { "totally-unknown-token", used, expired })
        {
            var res = await CompleteAsync(t, "another-password1");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            bodies.Add(await res.Content.ReadAsStringAsync());
        }
        Assert.Single(bodies.Distinct());
    }

    // The cross-team gate is re-evaluated at consume, because the target can join a team inside the
    // token's one-hour life. Untested, that check is one careless refactor from vanishing.
    [Fact]
    public async Task A_token_issued_before_the_target_joined_a_team_is_dead()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        var dto = await MintOkAsync(owner.Client, target.UserId);

        var other = await _f.RegisterAsync();
        await _f.AddOrgMemberAsync(other.OrgId, target.UserId, OrgRole.Member);

        // 404, not the mint's 409: this caller is anonymous and must learn nothing about the account.
        Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(dto.Token, "a-brand-new-password")).StatusCode);

        // And nothing changed — the old password still works.
        Assert.Equal(HttpStatusCode.OK, (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = target.Email, password = "pw-at-least-12" })).StatusCode);
    }

    [Fact]
    public async Task Short_password_is_400()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        var dto = await MintOkAsync(owner.Client, target.UserId);

        Assert.Equal(HttpStatusCode.BadRequest, (await CompleteAsync(dto.Token, "short")).StatusCode);
    }

    // The half of this feature with no visible UI, so it gets the explicit test.
    [Fact]
    public async Task Consume_revokes_the_targets_api_tokens()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        var pat = await _f.PatClientAsync(target.Client);
        Assert.Equal(HttpStatusCode.OK, (await pat.GetAsync("/api/v1/me")).StatusCode);

        var dto = await MintOkAsync(owner.Client, target.UserId);
        await CompleteAsync(dto.Token, "a-brand-new-password");

        Assert.Equal(HttpStatusCode.Unauthorized, (await pat.GetAsync("/api/v1/me")).StatusCode);
    }

    // A reset must not be an MFA bypass.
    [Fact]
    public async Task Mfa_still_challenges_after_a_reset()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        await _f.ArmMfaAsync(target.UserId);

        var dto = await MintOkAsync(owner.Client, target.UserId);
        await CompleteAsync(dto.Token, "a-brand-new-password");

        var login = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = target.Email, password = "a-brand-new-password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("mfaRequired", await login.Content.ReadAsStringAsync());
    }

    // RateLimits.Auth partitions on request path. If the token ever moves back into the path, each
    // guess gets its own fresh bucket and the second request below returns 404 instead of 429.
    [Fact]
    public async Task Bogus_tokens_share_one_rate_limit_bucket()
    {
        using var host = _f.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:Auth:BurstLimit"] = "1",
                ["RateLimit:Auth:TokensPerPeriod"] = "1",
                ["RateLimit:Auth:ReplenishmentSeconds"] = "3600",
            })));
        var c = host.CreateClient();

        var first = await c.PostAsJsonAsync("/api/v1/auth/password-reset:complete",
            new CompleteRequest("bogus-token-a", "a-valid-length-pw"));
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);

        // A DIFFERENT token, same fixed path: it must draw from the bucket the first guess emptied.
        var second = await c.PostAsJsonAsync("/api/v1/auth/password-reset:complete",
            new CompleteRequest("bogus-token-b", "a-valid-length-pw"));
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    // ---- the authority behind a link can move while the link is alive -------------------------

    // The mint gate refuses an Admin against an Owner. That gate is worth nothing if a link minted
    // legitimately against a Member survives that Member's promotion: the Admin consumes the stale
    // link, signs in as an Owner and promotes themselves. Reproduced against a live install before
    // this was fixed. The consume endpoint is anonymous and has no caller to re-check, so the
    // invalidation happens in UpdateRole instead.
    [Fact]
    public async Task A_link_minted_before_the_target_was_promoted_is_dead()
    {
        var owner = await _f.RegisterAsync();
        var admin = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Admin);
        var member = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        // Legitimate when minted: an Admin may reset a Member.
        var dto = await MintOkAsync(admin.Client, member.UserId);

        var promote = await owner.Client.PatchAsJsonAsync(
            $"/api/v1/org/members/{member.UserId}", new { role = "Owner" });
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);

        // The Admin could not mint this link now, so it must not still work.
        Assert.Equal(HttpStatusCode.NotFound,
            (await CompleteAsync(dto.Token, "seized-by-the-admin")).StatusCode);

        // And the account still has the password it had.
        Assert.Equal(HttpStatusCode.OK, (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = member.Email, password = "pw-at-least-12" })).StatusCode);
    }

    // Same root cause, other direction: a link must not outlive the membership that justified it, or
    // whoever issued it keeps a working takeover on someone who has left the org.
    [Fact]
    public async Task A_link_does_not_outlive_the_targets_membership()
    {
        var owner = await _f.RegisterAsync();
        var member = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        var dto = await MintOkAsync(owner.Client, member.UserId);

        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.Client.DeleteAsync($"/api/v1/org/members/{member.UserId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await CompleteAsync(dto.Token, "works-after-removal")).StatusCode);
    }

    // ---- the operator break-glass path ---------------------------------------------------------

    // deploy/scripts/issue-password-reset.sh does not call the API — it writes a PasswordResets row
    // straight into the database and prints the link, because the accounts it exists for (a sole
    // owner, anyone the cross-org gate refuses) are exactly the ones no caller is allowed to mint for.
    //
    // That makes the row shape and the token hashing a contract between a shell script and this code,
    // with nothing in the type system holding the two together. This test is that hold: it builds the
    // row the way the script does — independently, from the script's own recipe of uppercase hex
    // SHA-256 — and proves the ordinary endpoint accepts it. If HashToken's encoding or the table's
    // shape ever moves, the script silently starts minting dead links, and an operator finds out while
    // locked out of their own install. This fails first instead.
    [Fact]
    public async Task A_reset_row_written_the_way_the_operator_script_writes_it_is_consumable()
    {
        var owner = await _f.RegisterAsync();

        // The script's recipe, reimplemented rather than reused: openssl base64url of 24 random bytes,
        // then `openssl dgst -sha256` upper-cased.
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            db.Add(new PasswordReset
            {
                UserId = owner.UserId,
                OrgId = owner.OrgId,
                TokenHash = hash,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent,
            (await CompleteAsync(token, "set-by-the-operator")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = owner.Email, password = "set-by-the-operator" })).StatusCode);
    }
}
