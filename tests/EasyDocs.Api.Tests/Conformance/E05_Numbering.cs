using System.Net.Http.Json;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests.Conformance;

// E5 Numbering (spec §12.1, §5.1): R1-R6 exactly, including 0.0.7 -> 0.1.0, 0.0.7 -> 1.0.0 and manual
// 0.0.0; downloads named per R8. The counter on the document is authoritative.
[Collection(ConformanceCollection.Name)]
public class E05_Numbering
{
    private readonly ApiFactory _f;
    public E05_Numbering(ApiFactory f) => _f = f;

    private static string Number(VersionRefDto v) => $"{v.Major}.{v.Minor}.{v.Revision}";
    private static string Number(PublishedDto v) => $"{v.Major}.{v.Minor}.{v.Revision}";

    // R1/R2: the first draft is 0.0.1 and each subsequent draft bumps the revision.
    [Fact]
    public async Task Drafts_start_at_0_0_1_and_increment_the_revision()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Drafts");

        Assert.Equal("0.0.1", Number(await api.UploadAsync(doc.Id, DocxFixtures.Base())));
        Assert.Equal("0.0.2", Number(await api.UploadAsync(doc.Id, DocxFixtures.Edited())));
        Assert.Equal("0.0.3", Number(await api.UploadAsync(doc.Id, DocxFixtures.EditedPlusEcho())));
    }

    // R3: publishing minor from 0.0.7 yields 0.1.0.
    [Fact]
    public async Task Publishing_minor_from_0_0_7_yields_0_1_0()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Minor");
        var v = await api.UploadAsync(doc.Id, DocxFixtures.Base());
        await api.SetCounterAsync(doc.Id, 0, 0, 7);

        var published = await api.PublishAsync(v.VersionId, "minor");

        Assert.Equal("0.1.0", Number(published));
        var detail = await api.GetVersionAsync(v.VersionId);
        Assert.Equal((0, 1, 0), (detail.Major, detail.Minor, detail.Revision));
    }

    // R4: publishing major from 0.0.7 yields 1.0.0.
    [Fact]
    public async Task Publishing_major_from_0_0_7_yields_1_0_0()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Major");
        var v = await api.UploadAsync(doc.Id, DocxFixtures.Base());
        await api.SetCounterAsync(doc.Id, 0, 0, 7);

        var published = await api.PublishAsync(v.VersionId, "major");

        Assert.Equal("1.0.0", Number(published));
    }

    // R6: drafts after a publish continue from the published number.
    [Fact]
    public async Task Drafts_after_a_publish_continue_from_the_published_number()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Continues");
        var v = await api.UploadAsync(doc.Id, DocxFixtures.Base());
        await api.SetCounterAsync(doc.Id, 0, 0, 7);
        await api.PublishAsync(v.VersionId, "minor"); // -> 0.1.0

        var next = await api.UploadAsync(doc.Id, DocxFixtures.Edited());
        Assert.Equal("0.1.1", Number(next));

        var afterMajor = await api.PublishAsync(next.VersionId, "major"); // -> 1.0.0
        Assert.Equal("1.0.0", Number(afterMajor));
        Assert.Equal("1.0.1", Number(await api.UploadAsync(doc.Id, DocxFixtures.EditedPlusEcho())));
    }

    // R5: the manual override is authoritative, including resetting to 0.0.0.
    [Fact]
    public async Task Manual_counter_override_including_0_0_0_is_authoritative()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Manual");
        await api.UploadAsync(doc.Id, DocxFixtures.Base());   // 0.0.1
        await api.UploadAsync(doc.Id, DocxFixtures.Edited()); // 0.0.2

        await api.SetCounterAsync(doc.Id, 0, 0, 0);
        Assert.Equal("0.0.1", Number(await api.UploadAsync(doc.Id, DocxFixtures.EditedPlusEcho())));

        await api.SetCounterAsync(doc.Id, 3, 4, 5);
        Assert.Equal("3.4.6", Number(await api.UploadAsync(doc.Id, DocxFixtures.Base())));
    }

    [Fact]
    public async Task Negative_counter_values_are_rejected()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Negative");

        var res = await api.Http.PutAsJsonAsync($"/api/v1/documents/{doc.Id}/version-counter",
            new { major = -1, minor = 0, rev = 0 });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
    }

    // R8: downloads are named "{orgSlug}__{Sanitized_Name}-v{M}.{m}.{r}.{ext}".
    [Fact]
    public async Task Downloads_are_named_per_R8()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Master Lease Agreement");
        var v = await api.UploadAsync(doc.Id, DocxFixtures.Base());

        var res = await api.DownloadRawAsync(v.VersionId);
        res.EnsureSuccessStatusCode();

        var fileName = res.Content.Headers.ContentDisposition?.FileNameStar
            ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"');

        Assert.NotNull(fileName);
        Assert.EndsWith("-v0.0.1.docx", fileName);
        Assert.Contains("__", fileName);                    // {orgSlug}__{name}
        Assert.Contains("Master_Lease_Agreement", fileName); // spaces sanitized, name preserved
        Assert.DoesNotContain(' ', fileName);
    }
}
