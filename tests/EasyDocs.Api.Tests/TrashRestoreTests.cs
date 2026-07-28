using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Tests;
using EasyDocs.Api.Tests.Fixtures;

// DELETE /api/v1/documents/{id} and POST /api/v1/documents/{id}:restore (spec §10.1 Documents).
// Trashing is soft — history survives, so restore is lossless.
public class TrashRestoreTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public TrashRestoreTests(ApiFactory f) => _f = f;

    private record ListDto(ListItem[] Items, string? NextCursor);
    private record ListItem(Guid Id, string Name);
    private record VersionsDto(VersionItem[] Items);
    private record VersionItem(Guid Id, int Major, int Minor, int Revision);

    private static async Task<Guid[]> ListIdsAsync(HttpClient c)
    {
        var res = await c.GetAsync("/api/v1/documents");
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<ListDto>())!.Items.Select(i => i.Id).ToArray();
    }

    [Fact]
    public async Task Trash_hides_the_document_then_restore_brings_it_back_with_history()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        await a.Client.UploadAsync(docId);
        await a.Client.UploadAsync(docId, DocxFixtures.Edited());

        Assert.Contains(docId, await ListIdsAsync(a.Client));

        var del = await a.Client.DeleteAsync($"/api/v1/documents/{docId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // Gone from the dashboard and unreachable, but not destroyed.
        Assert.DoesNotContain(docId, await ListIdsAsync(a.Client));
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.GetAsync($"/api/v1/documents/{docId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await a.Client.PostAsync($"/api/v1/documents/{docId}/versions", TestAuth.DocxForm())).StatusCode);

        var restore = await a.Client.PostAsync($"/api/v1/documents/{docId}:restore", null);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        Assert.Contains(docId, await ListIdsAsync(a.Client));
        var versions = await (await a.Client.GetAsync($"/api/v1/documents/{docId}/versions"))
            .Content.ReadFromJsonAsync<VersionsDto>();
        Assert.Equal(2, versions!.Items.Length); // history untouched by the round trip
    }

    [Fact]
    public async Task Only_an_owner_may_trash()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var editor = await _f.SeedOrgUserAsync(a.OrgId);
        await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email = editor.Email, role = "Editor" });

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.Client.DeleteAsync($"/api/v1/documents/{docId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.DeleteAsync($"/api/v1/documents/{docId}")).StatusCode);
    }

    [Fact]
    public async Task Only_an_owner_may_restore()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var editor = await _f.SeedOrgUserAsync(a.OrgId);
        await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email = editor.Email, role = "Editor" });
        await a.Client.DeleteAsync($"/api/v1/documents/{docId}");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await editor.Client.PostAsync($"/api/v1/documents/{docId}:restore", null)).StatusCode);
    }

    [Fact]
    public async Task Restoring_a_live_document_is_a_no_op()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();

        var res = await a.Client.PostAsync($"/api/v1/documents/{docId}:restore", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // postcondition already holds
        Assert.Contains(docId, await ListIdsAsync(a.Client));
    }

    [Fact]
    public async Task Deleting_twice_is_404()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await a.Client.DeleteAsync($"/api/v1/documents/{docId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.DeleteAsync($"/api/v1/documents/{docId}")).StatusCode);
    }

    [Fact]
    public async Task Cross_org_caller_cannot_trash_or_restore()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var other = await _f.RegisterAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.DeleteAsync($"/api/v1/documents/{docId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.Client.PostAsync($"/api/v1/documents/{docId}:restore", null)).StatusCode);
    }
}
