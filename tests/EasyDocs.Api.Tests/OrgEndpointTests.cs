using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;

// Org-level read/manage endpoints (spec §10): org role grants no implicit document access, but nothing
// ever exposed the org/member management surface itself (list members, change role, remove, rename,
// invite without a document). Spec §9's settings screen and person pickers bind to these.
public class OrgEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public OrgEndpointTests(ApiFactory f) => _f = f;

    private record OrgDto(Guid Id, string Name, string Slug, string MyRole);
    private record OrgMemberDto(Guid UserId, string Email, string DisplayName, string Role, DateTimeOffset CreatedAt);
    private record InviteDto(string Email, string Role, string InvitationToken);
    private record AcceptDto(Guid OrgId, Guid? DocumentId, string? DocRole);

    private static async Task<OrgMemberDto[]> ListMembersAsync(HttpClient c)
    {
        var res = await c.GetAsync("/api/v1/org/members");
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<OrgMemberDto[]>())!;
    }

    private static void AdoptSession(HttpClient client, HttpResponseMessage res)
    {
        var cookie = res.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session=", StringComparison.Ordinal));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", cookie["ed_session=".Length..].Split(';')[0]);
    }

    [Fact]
    public async Task Registering_owner_reads_the_org_with_their_role()
    {
        var a = await _f.RegisterAsync();

        var res = await a.Client.GetAsync("/api/v1/org");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var dto = (await res.Content.ReadFromJsonAsync<OrgDto>())!;
        Assert.Equal(a.OrgId, dto.Id);
        Assert.Equal("Owner", dto.MyRole);
        Assert.False(string.IsNullOrEmpty(dto.Slug));
    }

    [Fact]
    public async Task An_ordinary_member_can_list_org_members_for_person_pickers()
    {
        var a = await _f.RegisterAsync();
        var member = await _f.SeedOrgUserAsync(a.OrgId, OrgRole.Member);

        var members = await ListMembersAsync(member.Client);
        Assert.Equal(2, members.Length);
        Assert.Contains(members, m => m.UserId == a.UserId && m.Role == "Owner");
        Assert.Contains(members, m => m.UserId == member.UserId && m.Role == "Member" && m.Email == member.Email);
    }

    [Fact]
    public async Task Owner_invites_a_new_org_member_and_the_raw_token_is_returned_once()
    {
        var a = await _f.RegisterAsync();
        var email = $"invitee-{Guid.NewGuid():N}@example.com";

        var res = await a.Client.PostAsJsonAsync("/api/v1/org/members", new { email, role = "Member" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var dto = (await res.Content.ReadFromJsonAsync<InviteDto>())!;
        Assert.False(string.IsNullOrEmpty(dto.InvitationToken));
        Assert.Equal(email, dto.Email);
    }

    [Fact]
    public async Task A_plain_member_cannot_invite()
    {
        var a = await _f.RegisterAsync();
        var member = await _f.SeedOrgUserAsync(a.OrgId, OrgRole.Member);

        var res = await member.Client.PostAsJsonAsync("/api/v1/org/members",
            new { email = "nobody@example.com", role = "Member" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Org_only_invitation_is_accepted_by_the_existing_accept_route()
    {
        var a = await _f.RegisterAsync();
        var email = $"invitee-{Guid.NewGuid():N}@example.com";

        var invite = await a.Client.PostAsJsonAsync("/api/v1/org/members", new { email, role = "Member" });
        invite.EnsureSuccessStatusCode();
        var dto = (await invite.Content.ReadFromJsonAsync<InviteDto>())!;

        // The invited email must match the accepting user's own email (InvitationEndpoints enforces this).
        var invitee = await _f.RegisterAsync(email);

        var accept = await invitee.Client.PostAsync($"/api/v1/invitations/{dto.InvitationToken}:accept", null);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var acceptDto = (await accept.Content.ReadFromJsonAsync<AcceptDto>())!;
        Assert.Equal(a.OrgId, acceptDto.OrgId);
        Assert.Null(acceptDto.DocumentId); // org-only invitation, no document attached

        AdoptSession(invitee.Client, accept);
        var members = await ListMembersAsync(invitee.Client);
        Assert.Contains(members, m => m.Email == email && m.Role == "Member");
    }

    [Fact]
    public async Task Owner_changes_a_members_role()
    {
        var a = await _f.RegisterAsync();
        var member = await _f.SeedOrgUserAsync(a.OrgId, OrgRole.Member);

        var res = await a.Client.PatchAsJsonAsync($"/api/v1/org/members/{member.UserId}", new { role = "Admin" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var members = await ListMembersAsync(a.Client);
        Assert.Equal("Admin", members.Single(m => m.UserId == member.UserId).Role);
    }

    [Fact]
    public async Task A_member_cannot_change_roles()
    {
        var a = await _f.RegisterAsync();
        var member = await _f.SeedOrgUserAsync(a.OrgId, OrgRole.Member);
        var other = await _f.SeedOrgUserAsync(a.OrgId, OrgRole.Member);

        var res = await member.Client.PatchAsJsonAsync($"/api/v1/org/members/{other.UserId}", new { role = "Admin" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Invalid_role_string_is_400()
    {
        var a = await _f.RegisterAsync();
        var member = await _f.SeedOrgUserAsync(a.OrgId, OrgRole.Member);

        var res = await a.Client.PatchAsJsonAsync($"/api/v1/org/members/{member.UserId}", new { role = "Sysadmin" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Owner_removes_a_member()
    {
        var a = await _f.RegisterAsync();
        var member = await _f.SeedOrgUserAsync(a.OrgId, OrgRole.Member);

        var res = await a.Client.DeleteAsync($"/api/v1/org/members/{member.UserId}");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        Assert.DoesNotContain(await ListMembersAsync(a.Client), m => m.UserId == member.UserId);
    }

    [Fact]
    public async Task Removing_the_last_owner_is_409()
    {
        var a = await _f.RegisterAsync();

        var res = await a.Client.DeleteAsync($"/api/v1/org/members/{a.UserId}");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Demoting_the_last_owner_is_409()
    {
        var a = await _f.RegisterAsync();

        var res = await a.Client.PatchAsJsonAsync($"/api/v1/org/members/{a.UserId}", new { role = "Member" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Owner_renames_the_org_and_the_slug_is_unchanged()
    {
        var a = await _f.RegisterAsync();
        var before = await (await a.Client.GetAsync("/api/v1/org")).Content.ReadFromJsonAsync<OrgDto>();

        var res = await a.Client.PatchAsJsonAsync("/api/v1/org", new { name = "New Org Name" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var after = await (await a.Client.GetAsync("/api/v1/org")).Content.ReadFromJsonAsync<OrgDto>();
        Assert.Equal("New Org Name", after!.Name);
        // R8 download filenames are baked with the slug (Numbering.DownloadFileName) - renaming the org
        // must never re-slug, or every future download filename would silently change.
        Assert.Equal(before!.Slug, after.Slug);
    }

    [Fact]
    public async Task A_member_cannot_rename_the_org()
    {
        var a = await _f.RegisterAsync();
        var member = await _f.SeedOrgUserAsync(a.OrgId, OrgRole.Member);

        var res = await member.Client.PatchAsJsonAsync("/api/v1/org", new { name = "Hostile Rename" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Org_B_member_never_appears_in_org_A_member_list()
    {
        var a = await _f.RegisterAsync();
        var b = await _f.RegisterAsync();

        Assert.DoesNotContain(await ListMembersAsync(a.Client), m => m.UserId == b.UserId);
        Assert.DoesNotContain(await ListMembersAsync(b.Client), m => m.UserId == a.UserId);
    }

    [Fact]
    public async Task Org_A_owner_cannot_patch_or_delete_a_member_of_org_B()
    {
        var a = await _f.RegisterAsync();
        var b = await _f.RegisterAsync(); // owner of org B, member of org B

        var patch = await a.Client.PatchAsJsonAsync($"/api/v1/org/members/{b.UserId}", new { role = "Admin" });
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);

        var del = await a.Client.DeleteAsync($"/api/v1/org/members/{b.UserId}");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }
}
