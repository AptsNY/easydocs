using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Tests;

public class FolderTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public FolderTests(ApiFactory f) => _f = f;

    // Fresh authenticated client with a unique org/user. Containerized DB persists across tests.
    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"folder-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "F", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    private static async Task<Guid> CreateAsync(HttpClient c, string name, Guid? parentId = null)
    {
        var res = await c.PostAsJsonAsync("/api/v1/folders", new { name, parentId });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<FolderDto>();
        return body!.Id;
    }

    private record FolderDto(Guid Id, string Name, Guid? ParentId);

    [Fact]
    public async Task Can_nest_folders_at_least_three_levels()
    {
        var c = await AuthedClientAsync();
        var a = await CreateAsync(c, "Leases");
        var b = await CreateAsync(c, "Templates", a);
        var cc = await CreateAsync(c, "2026", b);

        var underB = await c.GetFromJsonAsync<List<FolderDto>>($"/api/v1/folders?parentId={b}");
        Assert.Contains(underB!, f => f.Id == cc);

        var root = await c.GetFromJsonAsync<List<FolderDto>>("/api/v1/folders");
        Assert.Contains(root!, f => f.Id == a);
        Assert.DoesNotContain(root!, f => f.Id == b); // b is nested, not at root
    }

    [Fact]
    public async Task Delete_nonempty_without_mode_returns_400()
    {
        var c = await AuthedClientAsync();
        var a = await CreateAsync(c, "A");
        await CreateAsync(c, "B", a);
        var res = await c.DeleteAsync($"/api/v1/folders/{a}");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Delete_with_promote_children_reparents_to_grandparent()
    {
        var c = await AuthedClientAsync();
        var a = await CreateAsync(c, "A");
        var b = await CreateAsync(c, "B", a);
        var cc = await CreateAsync(c, "C", b);

        var res = await c.DeleteAsync($"/api/v1/folders/{b}?mode=promote_children");
        Assert.True(res.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK);

        var underA = await c.GetFromJsonAsync<List<FolderDto>>($"/api/v1/folders?parentId={a}");
        Assert.Contains(underA!, f => f.Id == cc);          // C promoted to A
        Assert.DoesNotContain(underA!, f => f.Id == b);     // B gone
    }

    [Fact]
    public async Task Delete_mode_trash_soft_deletes()
    {
        var c = await AuthedClientAsync();
        var a = await CreateAsync(c, "A");
        var res = await c.DeleteAsync($"/api/v1/folders/{a}?mode=trash");
        Assert.True(res.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK);

        var root = await c.GetFromJsonAsync<List<FolderDto>>("/api/v1/folders");
        Assert.DoesNotContain(root!, f => f.Id == a);
    }

    [Fact]
    public async Task Duplicate_name_same_parent_returns_409()
    {
        var c = await AuthedClientAsync();
        await CreateAsync(c, "Dup");
        var res = await c.PostAsJsonAsync("/api/v1/folders", new { name = "Dup" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Folders_endpoints_require_auth()
        => Assert.Equal(HttpStatusCode.Unauthorized,
            (await _f.CreateClient().GetAsync("/api/v1/folders")).StatusCode);
}
