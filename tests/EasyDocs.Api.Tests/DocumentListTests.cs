using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests;

// Dashboard tiles (spec §9) and the trash view E1's promote-vs-trash prompt implies.
public class DocumentListTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public DocumentListTests(ApiFactory f) => _f = f;

    private record Tile(
        Guid Id, string Name, Guid? FolderId, string? CurrentNumber, int VersionCount,
        DateTimeOffset? UpdatedAt, string? LastAuthorName, DateTimeOffset? DeletedAt);
    private record Page(Tile[] Items, string? NextCursor);

    [Fact]
    public async Task A_tile_shows_the_current_number_the_modified_time_and_the_last_author()
    {
        var acct = await _f.RegisterAsync();
        var docId = await acct.Client.CreateDocAsync("Tile");
        await acct.Client.UploadAsync(docId, DocxFixtures.Base());
        await acct.Client.UploadAsync(docId, DocxFixtures.Edited());

        var page = await acct.Client.GetFromJsonAsync<Page>("/api/v1/documents?limit=100");
        var tile = page!.Items.Single(t => t.Id == docId);

        Assert.Equal("0.0.2", tile.CurrentNumber);
        Assert.Equal(2, tile.VersionCount);
        Assert.Equal("U", tile.LastAuthorName);
        Assert.NotNull(tile.UpdatedAt);
    }

    [Fact]
    public async Task A_document_with_no_versions_yet_has_a_null_number_not_a_crash()
    {
        var acct = await _f.RegisterAsync();
        var docId = await acct.Client.CreateDocAsync("Empty");

        var page = await acct.Client.GetFromJsonAsync<Page>("/api/v1/documents?limit=100");
        var tile = page!.Items.Single(t => t.Id == docId);

        Assert.Null(tile.CurrentNumber);
        Assert.Equal(0, tile.VersionCount);
        Assert.Null(tile.LastAuthorName);
    }

    [Fact]
    public async Task Trashed_documents_are_listable_so_restore_is_reachable()
    {
        var acct = await _f.RegisterAsync();
        var docId = await acct.Client.CreateDocAsync("Bin me");
        await acct.Client.UploadAsync(docId, DocxFixtures.Base());
        (await acct.Client.DeleteAsync($"/api/v1/documents/{docId}")).EnsureSuccessStatusCode();

        var live = await acct.Client.GetFromJsonAsync<Page>("/api/v1/documents?limit=100");
        Assert.DoesNotContain(live!.Items, t => t.Id == docId);

        var trash = await acct.Client.GetFromJsonAsync<Page>("/api/v1/documents?trashed=true&limit=100");
        var tile = trash!.Items.Single(t => t.Id == docId);
        Assert.NotNull(tile.DeletedAt);

        // And the round trip works from the listing alone — no GUID kept on a napkin.
        (await acct.Client.PostAsync($"/api/v1/documents/{tile.Id}:restore", null)).EnsureSuccessStatusCode();
        var after = await acct.Client.GetFromJsonAsync<Page>("/api/v1/documents?limit=100");
        Assert.Contains(after!.Items, t => t.Id == docId);
    }

    [Fact]
    public async Task The_trash_view_is_still_membership_scoped()
    {
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync("Private bin");
        await owner.Client.UploadAsync(docId, DocxFixtures.Base());
        (await owner.Client.DeleteAsync($"/api/v1/documents/{docId}")).EnsureSuccessStatusCode();

        // A same-org user who was never a document member must not see it in the trash either.
        var other = await _f.SeedOrgUserAsync(owner.OrgId);
        var trash = await other.Client.GetFromJsonAsync<Page>("/api/v1/documents?trashed=true&limit=100");
        Assert.DoesNotContain(trash!.Items, t => t.Id == docId);
    }

    // Unlike ?order=, a bad ?sort= is not safely ignorable: it decides which column the cursor's key
    // means, so falling back would page a client against a column it did not ask for.
    [Fact]
    public async Task An_unknown_sort_is_rejected_rather_than_silently_ignored()
    {
        var acct = await _f.RegisterAsync();

        var res = await acct.Client.GetAsync("/api/v1/documents?sort=nonsense");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("updated")]
    [InlineData("name")]
    public async Task Every_documented_sort_is_accepted(string sort)
    {
        var acct = await _f.RegisterAsync();

        var res = await acct.Client.GetAsync($"/api/v1/documents?sort={sort}");

        res.EnsureSuccessStatusCode();
    }

    // The cursor carries the column it was built from, so replaying a name cursor under a time sort
    // is caught. Without this the WHERE would compare a name against a timestamp and quietly return
    // the wrong page.
    [Fact]
    public async Task A_cursor_from_one_sort_is_rejected_under_another()
    {
        var acct = await _f.RegisterAsync();
        var folderId = await acct.Client.CreateFolderAsync("Mismatch");
        for (var i = 0; i < 3; i++)
            await acct.Client.CreateDocAsync($"M{i}", folderId);

        var byName = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=name&limit=2");
        Assert.NotNull(byName!.NextCursor);

        var replayed = await acct.Client.GetAsync(
            $"/api/v1/documents?folderId={folderId}&sort=updated&limit=2&cursor={Uri.EscapeDataString(byName.NextCursor!)}");

        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
    }
}
