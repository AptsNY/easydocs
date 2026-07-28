using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;

// Document membership management (spec §10.1 "Members", §11 per-document authorization).
public class MembersTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public MembersTests(ApiFactory f) => _f = f;

    private record MemberDto(Guid UserId, string Email, string DisplayName, string Role);
    private record AddDto(Guid? UserId, string? Role, string? InvitationToken, string? Email);

    private static async Task<MemberDto[]> ListAsync(HttpClient c, Guid docId)
    {
        var res = await c.GetAsync($"/api/v1/documents/{docId}/members");
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<MemberDto[]>())!;
    }

    [Fact]
    public async Task Creator_is_the_sole_owner()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();

        var members = await ListAsync(a.Client, docId);
        var only = Assert.Single(members);
        Assert.Equal(a.UserId, only.UserId);
        Assert.Equal("Owner", only.Role);
        Assert.Equal(a.Email, only.Email);
    }

    [Fact]
    public async Task Owner_adds_an_existing_org_user_by_email()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var b = await _f.SeedOrgUserAsync(a.OrgId);

        // Before: same org, but not a document member -> 403, not 404 (spec §11).
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Client.GetAsync($"/api/v1/documents/{docId}")).StatusCode);

        var add = await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email = b.Email, role = "Editor" });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var dto = (await add.Content.ReadFromJsonAsync<AddDto>())!;
        Assert.Equal(b.UserId, dto.UserId);
        Assert.Equal("Editor", dto.Role);
        Assert.Null(dto.InvitationToken);

        Assert.Equal(2, (await ListAsync(a.Client, docId)).Length);
        // After: the new Editor can read and write.
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync($"/api/v1/documents/{docId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await b.Client.PostAsync($"/api/v1/documents/{docId}/versions", TestAuth.DocxForm())).StatusCode);
    }

    [Fact]
    public async Task Adding_an_unknown_email_returns_an_invitation_token()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();

        var add = await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email = $"nobody-{Guid.NewGuid():N}@example.com", role = "Viewer" });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        var dto = (await add.Content.ReadFromJsonAsync<AddDto>())!;
        Assert.NotNull(dto.InvitationToken);
        Assert.Null(dto.UserId);
        // No member yet — membership begins at accept.
        Assert.Single(await ListAsync(a.Client, docId));
    }

    [Fact]
    public async Task Duplicate_member_is_409()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var b = await _f.SeedOrgUserAsync(a.OrgId);

        await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members", new { email = b.Email, role = "Viewer" });
        var again = await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email = b.Email, role = "Editor" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Invalid_role_is_400()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var res = await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email = "x@example.com", role = "Sysadmin" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Editor_cannot_manage_members()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var b = await _f.SeedOrgUserAsync(a.OrgId);
        await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members", new { email = b.Email, role = "Editor" });

        // An Editor may read the roster but not change it — membership is an Owner concern.
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync($"/api/v1/documents/{docId}/members")).StatusCode);

        var c = await _f.SeedOrgUserAsync(a.OrgId);
        var add = await b.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email = c.Email, role = "Viewer" });
        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);

        var patch = await b.Client.PatchAsJsonAsync($"/api/v1/documents/{docId}/members/{a.UserId}", new { role = "Viewer" });
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);

        var del = await b.Client.DeleteAsync($"/api/v1/documents/{docId}/members/{a.UserId}");
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
    }

    [Fact]
    public async Task Patch_changes_role()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var b = await _f.SeedOrgUserAsync(a.OrgId);
        await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members", new { email = b.Email, role = "Editor" });

        var patch = await a.Client.PatchAsJsonAsync($"/api/v1/documents/{docId}/members/{b.UserId}", new { role = "Viewer" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var members = await ListAsync(a.Client, docId);
        Assert.Equal("Viewer", members.Single(m => m.UserId == b.UserId).Role);
        // Demoted: writes now rejected.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await b.Client.PostAsync($"/api/v1/documents/{docId}/versions", TestAuth.DocxForm())).StatusCode);
    }

    [Fact]
    public async Task Cannot_demote_or_remove_the_last_owner()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();

        var demote = await a.Client.PatchAsJsonAsync($"/api/v1/documents/{docId}/members/{a.UserId}", new { role = "Editor" });
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);

        var remove = await a.Client.DeleteAsync($"/api/v1/documents/{docId}/members/{a.UserId}");
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);

        // Still an Owner, still functional.
        Assert.Equal("Owner", (await ListAsync(a.Client, docId)).Single().Role);
    }

    [Fact]
    public async Task Delete_removes_a_member_and_revokes_access()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var b = await _f.SeedOrgUserAsync(a.OrgId);
        await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members", new { email = b.Email, role = "Editor" });

        var del = await a.Client.DeleteAsync($"/api/v1/documents/{docId}/members/{b.UserId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        Assert.Single(await ListAsync(a.Client, docId));
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Client.GetAsync($"/api/v1/documents/{docId}")).StatusCode);
    }

    [Fact]
    public async Task A_second_owner_allows_demoting_the_first()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var b = await _f.SeedOrgUserAsync(a.OrgId);
        await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members", new { email = b.Email, role = "Owner" });

        var demote = await a.Client.PatchAsJsonAsync($"/api/v1/documents/{docId}/members/{a.UserId}", new { role = "Viewer" });
        Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
        Assert.Equal("Viewer", (await ListAsync(b.Client, docId)).Single(m => m.UserId == a.UserId).Role);
    }

    [Fact]
    public async Task Non_member_and_cross_org_callers_are_rejected()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();

        var sameOrgStranger = await _f.SeedOrgUserAsync(a.OrgId);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await sameOrgStranger.Client.GetAsync($"/api/v1/documents/{docId}/members")).StatusCode);

        var otherOrg = await _f.RegisterAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherOrg.Client.GetAsync($"/api/v1/documents/{docId}/members")).StatusCode);
    }

    [Fact]
    public async Task Cannot_add_a_user_from_another_org_directly()
    {
        // A user who exists but is not in the caller's org must go through an invitation, never a
        // silent cross-org membership grant.
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var outsider = await _f.RegisterAsync();

        var add = await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email = outsider.Email, role = "Editor" });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        var dto = (await add.Content.ReadFromJsonAsync<AddDto>())!;
        Assert.NotNull(dto.InvitationToken); // invited, not added
        Assert.Null(dto.UserId);
        Assert.Single(await ListAsync(a.Client, docId));
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Client.GetAsync($"/api/v1/documents/{docId}")).StatusCode);
    }
}
