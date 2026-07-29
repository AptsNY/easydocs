using EasyDocs.Api.Data;
using EasyDocs.Api.Publishing;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EasyDocs.Api.Tests.Conformance;

// E6 Publish (spec §12.1): a published version renumbers, gets a PDF, and appears in Major Versions —
// and it applies to the SELECTED version, not blindly to the head.
[Collection(ConformanceCollection.Name)]
public class E06_Publish
{
    private readonly ApiFactory _f;
    public E06_Publish(ApiFactory f) => _f = f;

    // Mirrors LibreOfficePdfRenderer.ResolveSoffice — the PDF leg needs a real LibreOffice.
    private static bool SofficeAvailable() => LibreOfficePdfRenderer.ResolveSoffice() is not null;

    [Fact]
    public async Task Publishing_renumbers_the_selected_version_and_lists_it_under_major_versions()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Selective");

        var first = await api.UploadAsync(doc.Id, DocxFixtures.Base());          // 0.0.1
        var second = await api.UploadAsync(doc.Id, DocxFixtures.Edited());       // 0.0.2
        var third = await api.UploadAsync(doc.Id, DocxFixtures.EditedPlusEcho()); // 0.0.3

        // Publish the MIDDLE version, not the head.
        var published = await api.PublishAsync(second.VersionId, "minor", "Board copy");

        Assert.Equal(second.VersionId, published.VersionId);
        Assert.Equal((0, 1, 0), (published.Major, published.Minor, published.Revision));

        var target = await api.GetVersionAsync(second.VersionId);
        Assert.Equal((0, 1, 0), (target.Major, target.Minor, target.Revision));
        Assert.Equal("minor", target.PublishedKind);
        Assert.Equal("Board copy", target.PublishName);
        Assert.NotNull(target.PublishedAt);

        // The head and the first draft are untouched.
        Assert.Null((await api.GetVersionAsync(third.VersionId)).PublishedKind);
        Assert.Null((await api.GetVersionAsync(first.VersionId)).PublishedKind);

        // Major Versions lists exactly the published one.
        var publications = await api.ListPublicationsAsync(doc.Id);
        var only = Assert.Single(publications.Items);
        Assert.Equal(second.VersionId, only.VersionId);
        Assert.Equal("minor", only.Kind);
        Assert.Equal("Board copy", only.Name);
        Assert.Equal(api.UserId, only.PublishedBy);
    }

    [Fact]
    public async Task An_invalid_publish_kind_is_rejected()
    {
        var api = await EdApi.NewAsync(_f);
        var (_, vid) = await api.NewDocumentWithBaseAsync("Bad kind");

        var res = await api.PublishRawAsync(vid, "enormous");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Major_versions_list_holds_every_publication_newest_first()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Many publications");

        var a = await api.UploadAsync(doc.Id, DocxFixtures.Base());
        await api.PublishAsync(a.VersionId, "minor");
        var b = await api.UploadAsync(doc.Id, DocxFixtures.Edited());
        await api.PublishAsync(b.VersionId, "major");

        var items = (await api.ListPublicationsAsync(doc.Id)).Items;
        Assert.Equal(2, items.Length);
        Assert.All(items, i => Assert.NotNull(i.Kind));
        // Newest-created first.
        Assert.Equal(b.VersionId, items[0].VersionId);
        Assert.Equal(a.VersionId, items[1].VersionId);
    }

    [SkippableFact]
    public async Task Publishing_renders_a_pdf()
    {
        Skip.IfNot(SofficeAvailable(), "soffice not installed on this host — the compose stack bundles LibreOffice (spec §12.3)");

        var api = await EdApi.NewAsync(_f);
        var (_, vid) = await api.NewDocumentWithBaseAsync("Rendered");
        await api.PublishAsync(vid, "minor");

        // The render is queued out-of-process; poll for the linked blob.
        string? pdfSha = null;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            pdfSha = (await db.Versions.AsNoTracking().FirstAsync(v => v.Id == vid)).PdfBlobSha256;
            if (pdfSha is not null) break;
            await Task.Delay(500);
        }
        Assert.NotNull(pdfSha);

        // The rendered PDF must be a registered blob, not just a file on disk: PdfBlobSha256 is a
        // foreign key, so a missing `blobs` row makes the link fail and the PDF silently never appear.
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var blob = await db.Blobs.AsNoTracking().FirstOrDefaultAsync(b => b.Sha256 == pdfSha);
            Assert.NotNull(blob);
            Assert.Equal("application/pdf", blob!.Mime);
            Assert.True(blob.SizeBytes > 0);
        }

        // The API reports it and serves it.
        Assert.True((await api.GetVersionAsync(vid)).HasPdf);

        var download = await api.DownloadRawAsync(vid, "pdf");
        download.EnsureSuccessStatusCode();
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);

        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes[..4]));
    }

    [Fact]
    public async Task Pdf_download_before_a_render_is_a_conflict_not_a_500()
    {
        var api = await EdApi.NewAsync(_f);
        var (_, vid) = await api.NewDocumentWithBaseAsync("No pdf yet");

        var res = await api.DownloadRawAsync(vid, "pdf");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }
}
