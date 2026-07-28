using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests.Conformance;

// E3 Edit/version (spec §12.1): a Collabora save produces a new version; an unchanged re-save creates
// none; the list shows author/time/summary.
//
// The editing round trip is exercised at the WOPI protocol level — the suite plays Collabora's side of
// the conversation (LOCK + PutFile). See ConformanceFixture for why this is preferred over a browser.
[Collection(ConformanceCollection.Name)]
public class E03_EditVersion
{
    private readonly ApiFactory _f;
    public E03_EditVersion(ApiFactory f) => _f = f;

    [Fact]
    public async Task A_collabora_save_produces_a_new_version()
    {
        var api = await EdApi.NewAsync(_f);
        var (docId, baseVid) = await api.NewDocumentWithBaseAsync("Edited in Collabora");

        var session = await api.MintSessionAsync(baseVid);
        Assert.Contains("WOPISrc=", session.EditorUrl); // the host handed Collabora a real WOPI src
        Assert.True(session.AccessTokenTtlSeconds > 0);

        var save = await EdApi.WopiSaveAsync(_f.CreateClient(), session, DocxFixtures.Edited());
        save.EnsureSuccessStatusCode();

        var versions = (await api.ListVersionsAsync(docId)).Items;
        Assert.Equal(2, versions.Length);
        var head = versions.OrderBy(v => v.Revision).Last();
        Assert.Equal((0, 0, 2), (head.Major, head.Minor, head.Revision));
        Assert.Equal("EditWopi", (await api.GetVersionAsync(head.Id)).Source);
    }

    [Fact]
    public async Task An_unchanged_re_save_creates_no_version()
    {
        var api = await EdApi.NewAsync(_f);
        var (docId, baseVid) = await api.NewDocumentWithBaseAsync("Idempotent");

        var session = await api.MintSessionAsync(baseVid);
        var edited = DocxFixtures.Edited();

        (await EdApi.WopiSaveAsync(_f.CreateClient(), session, edited)).EnsureSuccessStatusCode();
        var afterFirst = (await api.ListVersionsAsync(docId)).Items.Length;

        // Same bytes again on the same lock (Collabora holds one lock per editing session and PutFiles
        // repeatedly): dedupe by sha, no new version (spec §5.2 step 2).
        (await EdApi.WopiSaveAsync(_f.CreateClient(), session, edited)).EnsureSuccessStatusCode();
        var afterSecond = (await api.ListVersionsAsync(docId)).Items.Length;

        Assert.Equal(2, afterFirst);
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public async Task The_version_list_shows_author_time_and_a_change_summary()
    {
        var api = await EdApi.NewAsync(_f);
        var (docId, baseVid) = await api.NewDocumentWithBaseAsync("Attributed");

        var session = await api.MintSessionAsync(baseVid);
        (await EdApi.WopiSaveAsync(_f.CreateClient(), session, DocxFixtures.Edited())).EnsureSuccessStatusCode();

        var items = (await api.ListVersionsAsync(docId)).Items.OrderBy(v => v.Revision).ToArray();
        var head = items.Last();

        // Author + time come straight off the list entry.
        Assert.Equal(api.UserId, head.CreatedBy);
        Assert.True(head.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.True(head.CreatedAt <= DateTimeOffset.UtcNow.AddMinutes(1));

        // Summary: the numeric diff between the base and the saved head is non-empty.
        var summary = await api.CompareAsync(docId, baseVid, head.Id);
        Assert.True(summary.Insertions > 0,
            $"expected insertions from the Collabora save, got ins={summary.Insertions} del={summary.Deletions}");
    }

    [Fact]
    public async Task Closing_a_session_is_owner_only_and_ends_the_session()
    {
        var api = await EdApi.NewAsync(_f);
        var (_, baseVid) = await api.NewDocumentWithBaseAsync("Closable");
        var session = await api.MintSessionAsync(baseVid);

        // Someone else's session is 404, not 403 — no existence leak (spec §11).
        var stranger = await EdApi.NewAsync(_f);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await stranger.CloseSessionRawAsync(session.SessionId)).StatusCode);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await api.CloseSessionRawAsync(session.SessionId)).StatusCode);
    }
}
