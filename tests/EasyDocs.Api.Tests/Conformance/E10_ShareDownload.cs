using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EasyDocs.Api.Tests.Conformance;

// E10 Share/download (spec §12.1): a share link is scoped to ONE version, revocable, and audited;
// DOCX + PDF download work, with no cloud export.
[Collection(ConformanceCollection.Name)]
public class E10_ShareDownload
{
    private readonly ApiFactory _f;
    public E10_ShareDownload(ApiFactory f) => _f = f;

    private record PublicViewDto(string DocumentName, string Version, string DownloadUrl);

    private async Task<Guid> ShareLinkIdAsync(Guid versionId)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        return await db.ShareLinks.Where(s => s.VersionId == versionId).Select(s => s.Id).SingleAsync();
    }

    [Fact]
    public async Task A_share_link_is_scoped_to_a_single_version()
    {
        var api = await EdApi.NewAsync(_f);
        var doc = await api.CreateDocumentAsync("Scoped share");

        // Capture the bytes: DocxFixtures rebuilds the zip per call and zip entries carry timestamps,
        // so two calls are not byte-identical.
        var baseBytes = DocxFixtures.Base();
        var editedBytes = DocxFixtures.Edited();
        var v1 = await api.UploadAsync(doc.Id, baseBytes);
        var v2 = await api.UploadAsync(doc.Id, editedBytes);

        var link = await api.CreateShareLinkAsync(v1.VersionId);

        var anon = _f.CreateClient();
        var view = await anon.GetFromJsonAsync<PublicViewDto>(link.Url);

        // It serves exactly v1 — never the document's later head.
        Assert.Equal("0.0.1", view!.Version);
        Assert.Equal("Scoped share", view.DocumentName);

        var bytes = await (await anon.GetAsync(view.DownloadUrl)).Content.ReadAsByteArrayAsync();
        Assert.Equal(baseBytes, bytes);
        Assert.NotEqual(editedBytes, bytes);

        // And a second version's link is a different capability.
        var link2 = await api.CreateShareLinkAsync(v2.VersionId);
        Assert.NotEqual(link.Token, link2.Token);
        Assert.Equal("0.0.2", (await anon.GetFromJsonAsync<PublicViewDto>(link2.Url))!.Version);
    }

    [Fact]
    public async Task The_public_link_needs_no_credentials_but_a_bad_token_is_404()
    {
        var api = await EdApi.NewAsync(_f);
        var (_, vid) = await api.NewDocumentWithBaseAsync("Anonymous view");
        var link = await api.CreateShareLinkAsync(vid);

        var anon = _f.CreateClient(); // no Authorization header at all
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(link.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/s/not-a-real-token")).StatusCode);
    }

    [Fact]
    public async Task A_share_link_is_revocable()
    {
        var api = await EdApi.NewAsync(_f);
        var (_, vid) = await api.NewDocumentWithBaseAsync("Revocable");
        var link = await api.CreateShareLinkAsync(vid);
        var anon = _f.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(link.Url)).StatusCode);

        var linkId = await ShareLinkIdAsync(vid);
        Assert.Equal(HttpStatusCode.NoContent, (await api.RevokeShareLinkRawAsync(linkId)).StatusCode);

        // Dead for both view and download.
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(link.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"{link.Url}/download")).StatusCode);
    }

    [Fact]
    public async Task An_expired_link_stops_working()
    {
        var api = await EdApi.NewAsync(_f);
        var (_, vid) = await api.NewDocumentWithBaseAsync("Expiring");

        var link = await api.CreateShareLinkAsync(vid, DateTimeOffset.UtcNow.AddMilliseconds(-1));

        Assert.Equal(HttpStatusCode.NotFound, (await _f.CreateClient().GetAsync(link.Url)).StatusCode);
    }

    [Fact]
    public async Task Share_activity_including_the_anonymous_read_is_audited()
    {
        var api = await EdApi.NewAsync(_f);
        var (docId, vid) = await api.NewDocumentWithBaseAsync("Audited share");
        var link = await api.CreateShareLinkAsync(vid);

        (await _f.CreateClient().GetAsync(link.Url)).EnsureSuccessStatusCode();
        var linkId = await ShareLinkIdAsync(vid);
        (await api.RevokeShareLinkRawAsync(linkId)).EnsureSuccessStatusCode();

        var trail = (await api.AuditAsync(docId)).Items;
        var actions = trail.Select(t => t.Action).ToArray();
        Assert.Contains("share_link.created", actions);
        Assert.Contains("share_link.viewed", actions); // §11: public share reads ARE audited
        Assert.Contains("share_link.revoked", actions);

        // The anonymous read has no actor — that is the point of auditing it.
        Assert.Null(trail.First(t => t.Action == "share_link.viewed").ActorUserId);
    }

    [Fact]
    public async Task Docx_download_works_for_members_and_pdf_requires_a_publish()
    {
        var api = await EdApi.NewAsync(_f);
        var (_, vid) = await api.NewDocumentWithBaseAsync("Both formats");

        var docx = await api.DownloadRawAsync(vid, "docx");
        docx.EnsureSuccessStatusCode();
        Assert.Equal(TestAuth.DocxMime, docx.Content.Headers.ContentType?.MediaType);

        // PDF exists only after a publish renders one — a clean 409, not a 500 or an empty file.
        var pdf = await api.DownloadRawAsync(vid, "pdf");
        Assert.Equal(HttpStatusCode.Conflict, pdf.StatusCode);
    }

    [Fact]
    public async Task Sharing_requires_document_membership()
    {
        var api = await EdApi.NewAsync(_f);
        var (_, vid) = await api.NewDocumentWithBaseAsync("Members only");

        var outsider = await EdApi.NewAsync(_f);
        var res = await outsider.Http.PostAsJsonAsync($"/api/v1/versions/{vid}/share-links", new { expiresAt = (DateTimeOffset?)null });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode); // cross-org: no existence leak
    }

    [Fact]
    public async Task There_is_no_cloud_export_path()
    {
        var api = await EdApi.NewAsync(_f);
        var (_, vid) = await api.NewDocumentWithBaseAsync("No export");

        // Download is the only egress; export/cloud-connection routes must not exist (spec §2 out-of-v1).
        Assert.Equal(HttpStatusCode.NotFound, (await api.Http.PostAsync($"/api/v1/versions/{vid}/exports", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Http.GetAsync("/api/v1/cloud-connections")).StatusCode);

        var openapi = await api.Http.GetStringAsync("/openapi/v1.json");
        Assert.DoesNotContain("export", openapi, StringComparison.OrdinalIgnoreCase);
    }
}
