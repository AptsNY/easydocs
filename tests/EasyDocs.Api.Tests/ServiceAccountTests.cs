using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EasyDocs.Api.Domain;

namespace EasyDocs.Api.Tests;

public class ServiceAccountTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ServiceAccountTests(ApiFactory f) => _f = f;

    private record SvcDto(Guid UserId, string Name, string Email);
    private record TokenDto(Guid Id, string Token);
    private record IdDto(Guid Id);
    private record ManagerDto(Guid UserId, string DisplayName);
    private record SvcRowDto(Guid UserId, string Name, string Email, ManagerDto ManagedBy, int LiveTokens);
    private record MemberRowDto(Guid UserId, string Role);

    private static async Task<SvcDto> CreateAsync(HttpClient c, string name = "docassemble")
    {
        var res = await c.PostAsJsonAsync("/api/v1/org/service-accounts", new { name });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<SvcDto>())!;
    }

    private static Task<HttpResponseMessage> MintAsync(HttpClient c, Guid svc, string name = "runtime") =>
        c.PostAsJsonAsync($"/api/v1/org/service-accounts/{svc}/tokens", new { name });

    private HttpClient Bearer(string token)
    {
        var client = _f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<HttpClient> SvcClientAsync(HttpClient manager, Guid svc)
    {
        var res = await MintAsync(manager, svc);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return Bearer((await res.Content.ReadFromJsonAsync<TokenDto>())!.Token);
    }

    private static async Task<Guid> CreateDocAsync(HttpClient c, string name = "Lease")
    {
        var res = await c.PostAsJsonAsync("/api/v1/documents", new { name });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private static Task<HttpResponseMessage> AddMemberAsync(HttpClient c, Guid doc, string email, string role) =>
        c.PostAsJsonAsync($"/api/v1/documents/{doc}/members", new { email, role });

    [Fact]
    public async Task Owner_creates_a_service_account_that_joins_documents_and_authenticates()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        Assert.EndsWith("@service.invalid", svc.Email);

        var doc = await CreateDocAsync(owner.Client);
        var other = await CreateDocAsync(owner.Client, "Unshared");
        Assert.Equal(HttpStatusCode.Created, (await AddMemberAsync(owner.Client, doc, svc.Email, "Viewer")).StatusCode);

        var bot = await SvcClientAsync(owner.Client, svc.UserId);
        Assert.Equal(HttpStatusCode.OK, (await bot.GetAsync($"/api/v1/documents/{doc}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.GetAsync($"/api/v1/documents/{other}/versions")).StatusCode);
    }

    [Fact]
    public async Task The_service_account_cannot_sign_in()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        var res = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = svc.Email, password = "anything-at-all" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task A_plain_member_cannot_create_and_sees_an_empty_list()
    {
        var owner = await _f.RegisterAsync();
        await CreateAsync(owner.Client);
        var member = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        var res = await member.Client.PostAsJsonAsync("/api/v1/org/service-accounts", new { name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var svcId = (await owner.Client.GetFromJsonAsync<SvcRowDto[]>("/api/v1/org/service-accounts"))!.Single().UserId;
        Assert.Equal(HttpStatusCode.Forbidden, (await MintAsync(member.Client, svcId)).StatusCode);
        var list = await member.Client.GetFromJsonAsync<SvcRowDto[]>("/api/v1/org/service-accounts");
        Assert.Empty(list!);
    }

    [Fact]
    public async Task List_shows_manager_and_live_token_count()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        await SvcClientAsync(owner.Client, svc.UserId);

        var row = Assert.Single((await owner.Client.GetFromJsonAsync<SvcRowDto[]>("/api/v1/org/service-accounts"))!);
        Assert.Equal(svc.UserId, row.UserId);
        Assert.Equal(owner.UserId, row.ManagedBy.UserId);
        Assert.Equal(1, row.LiveTokens);
    }

    [Fact]
    public async Task Only_the_manager_may_mint_even_an_org_owner_may_not()
    {
        var owner = await _f.RegisterAsync();
        var admin = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Admin);
        var otherOwner = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Owner);
        var svc = await CreateAsync(admin.Client); // an Admin creates it, so the Admin manages it

        Assert.Equal(HttpStatusCode.Forbidden, (await MintAsync(owner.Client, svc.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await MintAsync(otherOwner.Client, svc.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await MintAsync(admin.Client, svc.UserId)).StatusCode);
    }

    [Fact]
    public async Task A_cross_org_service_account_is_404()
    {
        var a = await _f.RegisterAsync();
        var b = await _f.RegisterAsync();
        var svc = await CreateAsync(a.Client);
        Assert.Equal(HttpStatusCode.NotFound, (await MintAsync(b.Client, svc.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.DeleteAsync($"/api/v1/org/service-accounts/{svc.UserId}")).StatusCode);
    }

    [Fact]
    public async Task Delete_revokes_its_tokens_and_hands_its_own_documents_to_the_manager()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        var bot = await SvcClientAsync(owner.Client, svc.UserId);
        var created = await CreateDocAsync(bot, "Ingested"); // the bot is this document's sole Owner

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/v1/org/service-accounts/{svc.UserId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await bot.GetAsync("/api/v1/me")).StatusCode);
        // The manager was not a member before; now they own it, so the document is not orphaned.
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync($"/api/v1/documents/{created}")).StatusCode);
        var members = await owner.Client.GetFromJsonAsync<MemberRowDto[]>($"/api/v1/documents/{created}/members");
        var ownerRow = Assert.Single(members!, m => m.UserId == owner.UserId);
        Assert.Equal("Owner", ownerRow.Role);

        var remaining = await owner.Client.GetFromJsonAsync<SvcRowDto[]>("/api/v1/org/service-accounts");
        Assert.DoesNotContain(remaining!, r => r.UserId == svc.UserId);
    }

    [Fact]
    public async Task Delete_does_not_hand_over_a_document_the_service_account_co_owns_with_a_person()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        var bot = await SvcClientAsync(owner.Client, svc.UserId);
        var colleague = await _f.SeedOrgUserAsync(owner.OrgId);
        var doc = await CreateDocAsync(bot, "Shared"); // the bot is this document's sole Owner, for now

        // Direct-grant branch: the colleague is already an org member, so this grants membership immediately.
        Assert.Equal(HttpStatusCode.Created, (await AddMemberAsync(bot, doc, colleague.Email, "Editor")).StatusCode);
        await _f.SetRoleAsync(doc, colleague.UserId, DocRole.Owner);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/v1/org/service-accounts/{svc.UserId}")).StatusCode);

        // A co-Owner already existed, so the document was never solely owned by the service account —
        // the manager gets no new access, and the colleague keeps theirs.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.Client.GetAsync($"/api/v1/documents/{doc}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await colleague.Client.GetAsync($"/api/v1/documents/{doc}")).StatusCode);
    }

    [Fact]
    public async Task A_service_token_gets_the_filters_own_message_not_the_handlers()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        var bot = await SvcClientAsync(owner.Client, svc.UserId);

        var res = await bot.PostAsJsonAsync("/api/v1/org/service-accounts", new { name = "nested" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("A service account cannot do this.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task An_org_owner_who_is_not_the_manager_may_delete()
    {
        var owner = await _f.RegisterAsync();
        var admin = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Admin);
        var svc = await CreateAsync(admin.Client);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/v1/org/service-accounts/{svc.UserId}")).StatusCode);
    }

    // switch-org mints a 7-day ed_session: a service token must never be able to trade itself for one.
    [Fact]
    public async Task A_service_token_cannot_reach_person_only_routes()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        var bot = await SvcClientAsync(owner.Client, svc.UserId);
        var doc = await CreateDocAsync(bot);
        var vid = await UploadAsync(bot, doc);

        var switched = await bot.PostAsJsonAsync("/api/v1/auth/switch-org", new { orgId = owner.OrgId });
        Assert.Equal(HttpStatusCode.Forbidden, switched.StatusCode);
        Assert.False(switched.Headers.Contains("Set-Cookie"));

        Assert.Equal(HttpStatusCode.Forbidden, (await bot.PostAsJsonAsync("/api/v1/tokens", new { name = "self", scopes = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.GetAsync("/api/v1/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.PostAsync("/api/v1/invitations/anything:accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.GetAsync("/api/v1/account/mfa")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.PostAsync("/api/v1/account/mfa/setup", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.PostAsJsonAsync($"/api/v1/versions/{vid}/share-links", new { })).StatusCode);
    }

    private record VersionDto(Guid VersionId);

    private static async Task<Guid> UploadAsync(HttpClient c, Guid doc)
    {
        var res = await c.PostAsync($"/api/v1/documents/{doc}/versions", TestAuth.DocxForm());
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<VersionDto>())!.VersionId;
    }
}
