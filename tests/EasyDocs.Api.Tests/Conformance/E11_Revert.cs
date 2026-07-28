using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests.Conformance;

// E11 Revert (spec §12.1): the new head equals the target's content; history is untouched.
[Collection(ConformanceCollection.Name)]
public class E11_Revert
{
    private readonly ApiFactory _f;
    public E11_Revert(ApiFactory f) => _f = f;

    [Fact]
    public async Task Revert_creates_a_new_head_with_the_targets_content_and_keeps_history()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Revertible");

        // Capture the bytes: DocxFixtures rebuilds the zip per call (timestamps), so two calls differ.
        var baseBytes = DocxFixtures.Base();
        var v1 = await api.UploadAsync(doc.Id, baseBytes);                     // 0.0.1
        var v2 = await api.UploadAsync(doc.Id, DocxFixtures.Edited());         // 0.0.2
        var v3 = await api.UploadAsync(doc.Id, DocxFixtures.EditedPlusEcho()); // 0.0.3

        var before = (await api.ListVersionsAsync(doc.Id)).Items.Select(v => v.Id).ToArray();
        Assert.Equal(3, before.Length);

        var reverted = await api.RevertAsync(v1.VersionId);

        // A NEW head, numbered onward — not a rewind of the counter.
        Assert.Equal((0, 0, 4), (reverted.Major, reverted.Minor, reverted.Revision));
        Assert.NotEqual(v1.VersionId, reverted.VersionId);
        Assert.Equal("Revert", (await api.GetVersionAsync(reverted.VersionId)).Source);

        // Byte-identical to the target: content-addressed, so reverting re-points at the same blob.
        var targetBytes = await (await api.DownloadRawAsync(v1.VersionId)).Content.ReadAsByteArrayAsync();
        var headBytes = await (await api.DownloadRawAsync(reverted.VersionId)).Content.ReadAsByteArrayAsync();
        Assert.Equal(targetBytes, headBytes);
        Assert.Equal(baseBytes, headBytes);

        // History untouched: every prior version still there, plus the new head.
        var after = (await api.ListVersionsAsync(doc.Id)).Items.Select(v => v.Id).ToArray();
        Assert.Equal(4, after.Length);
        foreach (var id in before) Assert.Contains(id, after);
        Assert.Contains(reverted.VersionId, after);
        // Explicitly: the versions we reverted *away* from survive.
        Assert.Contains(v2.VersionId, after);
        Assert.Contains(v3.VersionId, after);
    }

    [Fact]
    public async Task Reverting_to_the_current_head_is_a_no_op_by_dedupe()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Revert head");
        var v1 = await api.UploadAsync(doc.Id, DocxFixtures.Base());

        // Same sha as the head -> deduped, so no pointless version is created (spec §5.2 step 2).
        var reverted = await api.RevertAsync(v1.VersionId);

        Assert.Equal(v1.VersionId, reverted.VersionId);
        Assert.Single((await api.ListVersionsAsync(doc.Id)).Items);
    }

    [Fact]
    public async Task Revert_is_audited_and_requires_editor_role()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Revert perms");
        var v1 = await api.UploadAsync(doc.Id, DocxFixtures.Base());
        await api.UploadAsync(doc.Id, DocxFixtures.Edited());

        var viewer = await EdApi.ForSeededMemberAsync(_f, api.OrgId);
        await api.AddMemberAsync(doc.Id, viewer.Email, "Viewer");
        var denied = await viewer.Http.PostAsync($"/api/v1/versions/{v1.VersionId}/revert", null);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);

        await api.RevertAsync(v1.VersionId);
        Assert.Contains("version.reverted", await api.AuditActionsAsync(doc.Id));
    }
}
