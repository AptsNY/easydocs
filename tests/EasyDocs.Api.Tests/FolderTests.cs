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
        var doc = await c.CreateDocAsync("In B", b);

        var res = await c.DeleteAsync($"/api/v1/folders/{b}?mode=promote_children");
        Assert.True(res.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK);

        var underA = await c.GetFromJsonAsync<List<FolderDto>>($"/api/v1/folders?parentId={a}");
        Assert.Contains(underA!, f => f.Id == cc);          // C promoted to A
        Assert.DoesNotContain(underA!, f => f.Id == b);     // B gone
        // B's own documents move up with its subfolders rather than staying in a deleted folder.
        Assert.Equal(a, (await c.GetFromJsonAsync<DocDto>($"/api/v1/documents/{doc}"))!.FolderId);
    }

    // mode=trash takes documents with it, so a folder holding only documents is not "empty" either.
    [Fact]
    public async Task Delete_of_a_folder_holding_documents_without_mode_returns_400()
    {
        var c = await AuthedClientAsync();
        var a = await CreateAsync(c, "A");
        await c.CreateDocAsync("In A", a);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.DeleteAsync($"/api/v1/folders/{a}")).StatusCode);
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

    // mode=trash used to stamp only the folder itself: its subfolders stayed live but unreachable from
    // the tree (GET /folders walks down from the root), still holding their documents.
    [Fact]
    public async Task Delete_mode_trash_takes_the_whole_subtree_and_its_documents_with_it()
    {
        var owner = await _f.RegisterAsync();
        var c = owner.Client;
        var leases = await CreateAsync(c, "Leases");
        var y2026 = await CreateAsync(c, "2026", leases);
        var mine = await c.CreateDocAsync("Lease A", y2026);
        // A colleague's document in the same subtree: the caller cannot trash what it does not own, so
        // it moves to the top level instead of being left inside a trashed folder.
        var colleague = await _f.SeedOrgUserAsync(owner.OrgId);
        var theirs = await colleague.Client.CreateDocAsync("Lease B", y2026);

        (await c.DeleteAsync($"/api/v1/folders/{leases}?mode=trash")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound,
            (await c.PatchAsJsonAsync($"/api/v1/folders/{y2026}", new { name = "x" })).StatusCode);

        var live = await c.GetFromJsonAsync<DocPage>("/api/v1/documents?limit=100");
        Assert.DoesNotContain(live!.Items, d => d.Id == mine);
        var trash = await c.GetFromJsonAsync<DocPage>("/api/v1/documents?trashed=true&limit=100");
        Assert.Contains(trash!.Items, d => d.Id == mine);

        var moved = await colleague.Client.GetFromJsonAsync<DocDto>($"/api/v1/documents/{theirs}");
        Assert.Null(moved!.FolderId);

        // Restoring a document whose folder is gone lands it at the top level, not in a ghost folder.
        var restored = await c.PostAsync($"/api/v1/documents/{mine}:restore", null);
        restored.EnsureSuccessStatusCode();
        Assert.Null((await restored.Content.ReadFromJsonAsync<DocDto>())!.FolderId);
    }

    private record DocDto(Guid Id, Guid? FolderId);
    private record DocPage(DocDto[] Items);

    [Fact]
    public async Task Duplicate_name_same_parent_returns_409()
    {
        var c = await AuthedClientAsync();
        await CreateAsync(c, "Dup");
        var res = await c.PostAsJsonAsync("/api/v1/folders", new { name = "Dup" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    // A folder made its own descendant is unrecoverable: GET /folders walks DOWN from the root, so the
    // whole cycle vanishes from every listing and no request can reach it to move it back (spec §4).
    [Fact]
    public async Task Cannot_move_a_folder_under_its_own_descendant()
    {
        var c = await AuthedClientAsync();
        var a = await CreateAsync(c, "A");
        var b = await CreateAsync(c, "B", a);
        var deep = await CreateAsync(c, "C", b);

        foreach (var newParent in new[] { b, deep })
        {
            var res = await c.PatchAsJsonAsync($"/api/v1/folders/{a}", new { parentId = newParent });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        }

        // Still reachable from the root, which is the point.
        var root = await c.GetFromJsonAsync<List<FolderDto>>("/api/v1/folders");
        Assert.Contains(root!, f => f.Id == a);
    }

    // `parentId: null` used to be indistinguishable from "parentId not supplied", so a folder could be
    // nested but never un-nested. Null now means the root; absent still means "leave the parent alone".
    [Fact]
    public async Task Explicit_null_parent_moves_to_root_and_an_absent_one_leaves_it_alone()
    {
        var c = await AuthedClientAsync();
        var a = await CreateAsync(c, "A");
        var b = await CreateAsync(c, "B", a);

        var renamed = await c.PatchAsJsonAsync($"/api/v1/folders/{b}", new { name = "B renamed" });
        renamed.EnsureSuccessStatusCode();
        Assert.Equal(a, (await renamed.Content.ReadFromJsonAsync<FolderDto>())!.ParentId);

        var moved = await c.PatchAsJsonAsync($"/api/v1/folders/{b}", new { parentId = (Guid?)null });
        moved.EnsureSuccessStatusCode();
        Assert.Null((await moved.Content.ReadFromJsonAsync<FolderDto>())!.ParentId);

        var root = await c.GetFromJsonAsync<List<FolderDto>>("/api/v1/folders");
        Assert.Contains(root!, f => f.Id == b);
    }

    // The unique index treats a NULL parent as distinct, so moving to the root has to pre-check names
    // the way Create does — otherwise it is the one route to two same-named folders side by side.
    [Fact]
    public async Task Moving_to_root_over_an_existing_name_returns_409()
    {
        var c = await AuthedClientAsync();
        var a = await CreateAsync(c, "Leases");
        var nested = await CreateAsync(c, "Leases", a);

        var res = await c.PatchAsJsonAsync($"/api/v1/folders/{nested}", new { parentId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Folders_endpoints_require_auth()
        => Assert.Equal(HttpStatusCode.Unauthorized,
            (await _f.CreateClient().GetAsync("/api/v1/folders")).StatusCode);
}
