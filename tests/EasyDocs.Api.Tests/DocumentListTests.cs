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

    // Names are compared lower-cased. Under the default Postgres collation a raw ORDER BY name puts
    // every capital ahead of every lowercase letter, so "Zebra" would sort before "apple".
    [Fact]
    public async Task Sorting_by_name_is_case_insensitive()
    {
        var acct = await _f.RegisterAsync();
        var folderId = await acct.Client.CreateFolderAsync("Alphabet");
        await acct.Client.CreateDocAsync("Zebra", folderId);
        await acct.Client.CreateDocAsync("apple", folderId);
        await acct.Client.CreateDocAsync("Mango", folderId);

        var asc = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=name&order=asc&limit=100");
        Assert.Equal(new[] { "apple", "Mango", "Zebra" }, asc!.Items.Select(t => t.Name));

        var desc = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=name&order=desc&limit=100");
        Assert.Equal(new[] { "Zebra", "Mango", "apple" }, desc!.Items.Select(t => t.Name));
    }

    [Fact]
    public async Task Sorting_by_creation_time_runs_both_ways()
    {
        var acct = await _f.RegisterAsync();
        var folderId = await acct.Client.CreateFolderAsync("Chronological");
        await acct.Client.CreateDocAsync("First", folderId);
        await acct.Client.CreateDocAsync("Second", folderId);
        await acct.Client.CreateDocAsync("Third", folderId);

        var asc = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=created&order=asc&limit=100");
        Assert.Equal(new[] { "First", "Second", "Third" }, asc!.Items.Select(t => t.Name));

        var desc = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=created&order=desc&limit=100");
        Assert.Equal(new[] { "Third", "Second", "First" }, desc!.Items.Select(t => t.Name));
    }

    // The point of the feature: the document touched last comes first, regardless of when it was
    // created.
    [Fact]
    public async Task Sorting_by_last_updated_follows_the_newest_version_not_the_creation_time()
    {
        var acct = await _f.RegisterAsync();
        var folderId = await acct.Client.CreateFolderAsync("Recency");
        // The two orders are deliberately opposed: "Older" is created FIRST but uploaded to LAST. If
        // `updated` silently fell through to the creation key, both assertions below could not hold at
        // once — which is the whole point of the feature, and what a same-direction fixture cannot show.
        var older = await acct.Client.CreateDocAsync("Older", folderId);
        var newer = await acct.Client.CreateDocAsync("Newer", folderId);
        await acct.Client.UploadAsync(newer, DocxFixtures.Build("newer", "doc", "first upload"));
        await acct.Client.UploadAsync(older, DocxFixtures.Build("older", "doc", "last upload"));

        var byUpdate = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=updated&order=desc&limit=100");
        Assert.Equal(new[] { "Older", "Newer" }, byUpdate!.Items.Select(t => t.Name));

        var byCreation = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=created&order=desc&limit=100");
        Assert.Equal(new[] { "Newer", "Older" }, byCreation!.Items.Select(t => t.Name));
    }

    // A document with no versions has no version time to sort by. It must fall back to its own
    // creation time, not vanish: a NULL cannot take part in a keyset row-value comparison, so an
    // uncoalesced key would silently drop the row from every page.
    [Fact]
    public async Task A_document_with_no_versions_still_appears_when_sorting_by_last_updated()
    {
        var acct = await _f.RegisterAsync();
        var folderId = await acct.Client.CreateFolderAsync("Empties");
        var withVersion = await acct.Client.CreateDocAsync("Has one", folderId);
        await acct.Client.UploadAsync(withVersion, DocxFixtures.Build("a", "version", Guid.NewGuid().ToString("N")));
        var empty = await acct.Client.CreateDocAsync("Has none", folderId);

        var page = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort=updated&order=desc&limit=100");

        Assert.Equal(2, page!.Items.Length);
        // Created after the upload, so its creation time is the later of the two.
        Assert.Equal(empty, page.Items[0].Id);
        Assert.Null(page.Items[0].UpdatedAt); // and the tile still says "no versions yet"
    }

    // A sort that only holds within one page is not a sort. Five names read two at a time must come
    // back in one alphabetical sequence, with no row repeated and none dropped.
    [Fact]
    public async Task A_sorted_list_stays_ordered_across_cursor_pages()
    {
        var acct = await _f.RegisterAsync();
        var folderId = await acct.Client.CreateFolderAsync("Paged");
        foreach (var name in new[] { "delta", "Alpha", "echo", "bravo", "Charlie" })
            await acct.Client.CreateDocAsync(name, folderId);

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var url = $"/api/v1/documents?folderId={folderId}&sort=name&order=asc&limit=2"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await acct.Client.GetFromJsonAsync<Page>(url);
            seen.AddRange(page!.Items.Select(t => t.Name));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(new[] { "Alpha", "bravo", "Charlie", "delta", "echo" }, seen);
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
