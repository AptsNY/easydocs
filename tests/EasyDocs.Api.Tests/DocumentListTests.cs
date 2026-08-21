using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Api;
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

    // A sort that only holds within one page is not a sort. The WHERE and the ORDER BY are two
    // hand-written six-arm switches, so every (sort, order) pair needs paging of its own — one
    // copy-paste slip in one arm otherwise ships silently.
    //
    // The paged sequence is compared against the same query read whole rather than against a hardcoded
    // list. That is the point: the test never has to know which order is correct, so it holds whatever
    // the collation and the coalesced update time decide, and fails exactly when the WHERE and the
    // ORDER BY of an arm disagree with each other.
    [Theory]
    [InlineData("created", "asc")]
    [InlineData("created", "desc")]
    [InlineData("updated", "asc")]
    [InlineData("updated", "desc")]
    [InlineData("name", "asc")]
    [InlineData("name", "desc")]
    public async Task A_sorted_list_stays_ordered_across_cursor_pages(string sort, string order)
    {
        var acct = await _f.RegisterAsync();
        // Its own folder: the suite shares one database and runs in parallel, so anything org-wide would
        // be paging other tests' documents too.
        var folderId = await acct.Client.CreateFolderAsync($"Paged {sort} {order}");
        // "résumé" and "Ångström" put the name arms permanently on a locale-aware collation, and "a-b"
        // on punctuation the C locale would order differently — Postgres does that comparison, not C#,
        // and both the WHERE and the ORDER BY have to agree with it.
        var names = new[] { "delta", "Alpha", "echo", "bravo", "Charlie", "résumé", "a-b", "Ångström" };
        var ids = new Dictionary<string, Guid>();
        foreach (var name in names)
            ids[name] = await acct.Client.CreateDocAsync(name, folderId);

        // Uploaded in an order unrelated to creation, so the `updated` arms cannot agree with the
        // `created` arms by accident and a swapped arm shows as a mismatch. Every upload carries a unique
        // paragraph: blobs are content-addressed, and two tests first-writing the same sha at the same
        // time can race into a 500.
        foreach (var name in new[] { "echo", "Alpha", "Ångström", "bravo" })
            await acct.Client.UploadAsync(
                ids[name], DocxFixtures.Build(name, sort, order, Guid.NewGuid().ToString("N")));

        var unpaged = await acct.Client.GetFromJsonAsync<Page>(
            $"/api/v1/documents?folderId={folderId}&sort={sort}&order={order}&limit=100");
        Assert.Equal(names.Length, unpaged!.Items.Length);

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var url = $"/api/v1/documents?folderId={folderId}&sort={sort}&order={order}&limit=2"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await acct.Client.GetFromJsonAsync<Page>(url);
            seen.AddRange(page!.Items.Select(t => t.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(seen.Count, seen.Distinct().Count()); // no row twice
        Assert.Equal(unpaged.Items.Select(t => t.Id), seen);
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

    // The other half of that check: only a tag this endpoint MINTS means "you changed sort". A cursor
    // from the release before the tag existed is 8 bytes of ticks followed by a Guid, so what the new
    // format reads as its tag is the LOW BYTE of a tick count — and rejecting that would mean an old
    // browser tab pressing "Load more", having never sent ?sort= at all, is told to drop a cursor
    // because it changed a sort it never asked for. An unusable cursor is page one; the spec leans on it.
    [Fact]
    public async Task A_cursor_from_before_the_tag_existed_falls_back_to_page_one()
    {
        var acct = await _f.RegisterAsync();
        var folderId = await acct.Client.CreateFolderAsync("Legacy cursor");
        for (var i = 0; i < 3; i++)
            await acct.Client.CreateDocAsync($"L{i}", folderId);

        // The legacy payload, built by hand because no code mints it any more: 8 bytes of ticks then a
        // Guid, 24 bytes, no tag. The low byte is what decides which branch this exercises, and it must
        // NOT be 0 (SortCreated) — Postgres keeps timestamptz to the microsecond, so a real tick count is
        // always a multiple of 10 and that byte is one of {0, 10, ... 250}, meaning 25 legacy cursors in
        // 26 land here rather than on the tag-matches path. Simplify this to a plain UtcNow and the test
        // covers the interesting branch one time in 26 and says nothing the other 25.
        var legacy = new byte[24];
        BitConverter.TryWriteBytes(legacy.AsSpan(0, 8), new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero).UtcTicks);
        legacy[0] = 0x50;
        Guid.NewGuid().TryWriteBytes(legacy.AsSpan(8));
        Assert.NotEqual(0, legacy[0]);

        // No ?sort=, exactly as the old client sent it.
        var res = await acct.Client.GetAsync(
            $"/api/v1/documents?folderId={folderId}&limit=100&cursor={Uri.EscapeDataString(Base64Url.EncodeToString(legacy))}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var page = await res.Content.ReadFromJsonAsync<Page>();
        Assert.Equal(3, page!.Items.Length);
    }

    // A name cursor's key is client-controlled bytes that end up in a `text` parameter, and Postgres
    // rejects NUL in text outright — so this used to be a 500 on input anyone can send. lower(name)
    // cannot contain NUL either, so no such cursor can ever match a row: it belongs in the same
    // unusable-so-page-one bucket. Bytes that are merely not valid UTF-8 need no such rescue — they
    // round-trip to replacement characters, which Postgres compares happily — so they stay a plain 200
    // and the last assertion holds that line.
    [Fact]
    public async Task A_name_cursor_whose_key_contains_NUL_is_page_one_not_a_500()
    {
        var acct = await _f.RegisterAsync();
        var folderId = await acct.Client.CreateFolderAsync("NUL key");
        for (var i = 0; i < 3; i++)
            await acct.Client.CreateDocAsync($"B{i}", folderId);

        // Tag 2 is what ?sort=name mints, so this reaches the name keyset rather than being discarded as
        // a foreign tag.
        var withNul = Pagination.Encode(2, new byte[] { 0x61, 0x00, 0x62 }, Guid.NewGuid());
        var res = await acct.Client.GetAsync(
            $"/api/v1/documents?folderId={folderId}&sort=name&limit=100&cursor={Uri.EscapeDataString(withNul)}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var page = await res.Content.ReadFromJsonAsync<Page>();
        Assert.Equal(3, page!.Items.Length);

        var invalidUtf8 = Pagination.Encode(2, new byte[] { 0xFF, 0xFE }, Guid.NewGuid());
        var alsoOk = await acct.Client.GetAsync(
            $"/api/v1/documents?folderId={folderId}&sort=name&limit=100&cursor={Uri.EscapeDataString(invalidUtf8)}");
        Assert.Equal(HttpStatusCode.OK, alsoOk.StatusCode);
    }
}
