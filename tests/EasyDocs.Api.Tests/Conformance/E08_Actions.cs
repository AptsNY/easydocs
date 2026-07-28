using System.Net;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests.Conformance;

// E8 Actions menu (spec §12.1): the v1 action set is present AND functional — Open in Collabora,
// Import, Share, Download, Name, Publish, Revert, Push To Copy (8 actions). Desktop "Open in Word"
// and Export are explicitly v1.1 and must NOT exist.
//
// Seven of the eight are backed by shipped endpoints; Push To Copy ships in M4 (E9), so that one
// action is asserted as pending rather than functional.
[Collection(ConformanceCollection.Name)]
public class E08_Actions
{
    private readonly ApiFactory _f;
    public E08_Actions(ApiFactory f) => _f = f;

    [Fact]
    public async Task Open_in_collabora_is_functional()
    {
        var api = await EdApi.NewAsync(_f);
        var (_, vid) = await api.NewDocumentWithBaseAsync("Action: open");

        var session = await api.MintSessionAsync(vid);

        Assert.NotEqual(Guid.Empty, session.SessionId);
        Assert.Contains("WOPISrc=", session.EditorUrl);
        Assert.Contains("access_token=", session.EditorUrl);
        Assert.False(string.IsNullOrWhiteSpace(session.AccessToken));
    }

    [Fact]
    public async Task Import_is_functional()
    {
        var api = await EdApi.NewAsync(_f);
        var (docId, _) = await api.NewDocumentWithBaseAsync("Action: import");

        var imported = await api.ImportAsync(docId, DocxFixtures.Edited());

        Assert.Equal("Import", (await api.GetVersionAsync(imported.VersionId)).Source);
    }

    [Fact]
    public async Task Share_is_functional()
    {
        var api = await EdApi.NewAsync(_f);
        var (_, vid) = await api.NewDocumentWithBaseAsync("Action: share");

        var link = await api.CreateShareLinkAsync(vid);

        Assert.False(string.IsNullOrWhiteSpace(link.Token));
        var publicView = await _f.CreateClient().GetAsync(link.Url);
        Assert.Equal(HttpStatusCode.OK, publicView.StatusCode);
    }

    [Fact]
    public async Task Download_is_functional()
    {
        var api = await EdApi.NewAsync(_f);
        var (_, vid) = await api.NewDocumentWithBaseAsync("Action: download");

        var res = await api.DownloadRawAsync(vid);

        res.EnsureSuccessStatusCode();
        Assert.Equal(TestAuth.DocxMime, res.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await res.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Name_is_functional()
    {
        var api = await EdApi.NewAsync(_f);
        var (_, vid) = await api.NewDocumentWithBaseAsync("Action: name");

        var named = await api.NameVersionAsync(vid, "Signed original");

        Assert.Equal("Signed original", named.Name);
    }

    [Fact]
    public async Task Publish_is_functional()
    {
        var api = await EdApi.NewAsync(_f);
        var (docId, vid) = await api.NewDocumentWithBaseAsync("Action: publish");

        await api.PublishAsync(vid, "minor");

        Assert.Single((await api.ListPublicationsAsync(docId)).Items);
    }

    [Fact]
    public async Task Revert_is_functional()
    {
        var api = await EdApi.NewAsync(_f);
        var (docId, first) = await api.NewDocumentWithBaseAsync("Action: revert");
        await api.UploadAsync(docId, DocxFixtures.Edited());

        var reverted = await api.RevertAsync(first);

        Assert.Equal("Revert", (await api.GetVersionAsync(reverted.VersionId)).Source);
    }

    [SkippableFact]
    public void Push_to_copy_is_pending_until_m4()
    {
        // The 8th action (spec §12.1 E8 / §8) lands with copies & push in M4, tracked by E9.
        Skip.If(true, "Push To Copy ships in M4 (copies & push) — tracked by E9, per spec §13");
    }

    // The action set is closed: v1.1 features must not have leaked into the v1 surface.
    [Fact]
    public async Task Export_and_open_in_word_are_absent_from_the_v1_surface()
    {
        var api = await EdApi.NewAsync(_f);
        var openapi = await api.Http.GetStringAsync("/openapi/v1.json");

        foreach (var banned in new[] { "exports", "open-in-word", "openInWord", "ms-word:", "cloud-connections", "/tasks", "realtime" })
            Assert.DoesNotContain(banned, openapi, StringComparison.OrdinalIgnoreCase);

        // And the routes really are not served.
        var (_, vid) = await api.NewDocumentWithBaseAsync("Closed set");
        Assert.Equal(HttpStatusCode.NotFound, (await api.Http.PostAsync($"/api/v1/versions/{vid}/exports", null)).StatusCode);
    }
}
