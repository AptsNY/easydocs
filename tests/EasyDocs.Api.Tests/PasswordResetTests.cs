using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Domain;

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
}
