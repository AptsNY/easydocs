using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class DownloadTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public DownloadTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private async Task<(HttpClient client, string orgSlug)> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"dl-{Guid.NewGuid():N}@example.com";
        var orgName = $"Org-{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "D", password = "pw-at-least-12", orgName });
        reg.EnsureSuccessStatusCode();
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var org = await db.Organizations.OrderByDescending(o => o.CreatedAt).FirstAsync(o => o.Name == orgName);
        return (client, org.Slug);
    }

    private static MultipartFormDataContent Docx(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        return new MultipartFormDataContent { { part, "file", "d.docx" } };
    }

    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId, int Major, int Minor, int Revision);

    private static async Task<Guid> CreateDocAsync(HttpClient c, string name)
    {
        var create = await c.PostAsJsonAsync("/api/v1/documents", new { name });
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<DocDto>())!.Id;
    }

    private static async Task<UploadDto> UploadAsync(HttpClient c, Guid docId, byte[] bytes)
    {
        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(bytes));
        up.EnsureSuccessStatusCode();
        return (await up.Content.ReadFromJsonAsync<UploadDto>())!;
    }

    [Fact]
    public async Task Download_docx_has_R8_filename()
    {
        var (c, slug) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Master Lease");
        var bytes = new byte[] { 10, 20, 30, 40 };
        var v = await UploadAsync(c, docId, bytes);
        Assert.Equal((0, 0, 1), (v.Major, v.Minor, v.Revision));

        var resp = await c.GetAsync($"/api/v1/versions/{v.VersionId}/download?format=docx");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal($"{slug}__Master_Lease-v0.0.1.docx", resp.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes, body);
    }

    // The real corpus contains 9 PDFs and some legacy .doc files ingested through this endpoint. The bytes
    // were always stored faithfully; only the LABEL was wrong — Blobs.Mime and the download headers were
    // hardcoded docx, so Word refused the file and R8 named it "X.pdf-v0.0.1.docx".
    //
    // The multipart part built by Docx() deliberately LIES here: docx content type, "d.docx" filename.
    // Both are untrusted input (spec §10.3) and neither may reach a response header; the type is sniffed
    // from the stored bytes and mapped through a server-side allowlist (BlobMime).
    [Fact]
    public async Task Download_serves_pdf_bytes_as_pdf_with_an_R8_pdf_name()
    {
        var (c, slug) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Laundry Agreement.pdf");
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\nlease body\n%%EOF");
        var v = await UploadAsync(c, docId, pdf);

        var resp = await c.GetAsync($"/api/v1/versions/{v.VersionId}/download");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/pdf", resp.Content.Headers.ContentType!.MediaType);
        // R8: the document name already ends in .pdf, so the extension is not repeated.
        Assert.Equal($"{slug}__Laundry_Agreement-v0.0.1.pdf", resp.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Equal(pdf, await resp.Content.ReadAsByteArrayAsync());

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var ver = await db.Versions.FirstAsync(x => x.Id == v.VersionId);
        Assert.Equal("application/pdf", (await db.Blobs.FirstAsync(b => b.Sha256 == ver.BlobSha256)).Mime);
    }

    // Legacy .doc (OLE2 compound file) is the other real non-docx in the corpus.
    [Fact]
    public async Task Download_serves_legacy_doc_bytes_as_msword()
    {
        var (c, slug) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Old Lease.doc");
        var v = await UploadAsync(c, docId, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 1, 2, 3]);

        var resp = await c.GetAsync($"/api/v1/versions/{v.VersionId}/download");
        Assert.Equal("application/msword", resp.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"{slug}__Old_Lease-v0.0.1.doc", resp.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
    }

    [Fact]
    public async Task Download_pdf_unpublished_returns_409()
    {
        var (c, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Master Lease");
        var v = await UploadAsync(c, docId, new byte[] { 1, 2, 3 });

        var resp = await c.GetAsync($"/api/v1/versions/{v.VersionId}/download?format=pdf");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Download_pdf_streams_when_pdf_present()
    {
        var (c, slug) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Master Lease");
        var v = await UploadAsync(c, docId, new byte[] { 10, 20, 30, 40 });

        // Host has no soffice; seed the rendered PDF blob directly (download reads it via IBlobStore only).
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\nfake pdf body\n%%EOF");
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var blobs = scope.ServiceProvider.GetRequiredService<EasyDocs.Api.Storage.IBlobStore>();
            var res = await blobs.PutAsync(new MemoryStream(pdf));
            db.Add(new Blob { Sha256 = res.Sha256, SizeBytes = res.SizeBytes, Mime = "application/pdf", StorageKey = res.Sha256, CreatedAt = DateTimeOffset.UtcNow });
            var ver = await db.Versions.FirstAsync(x => x.Id == v.VersionId);
            ver.PdfBlobSha256 = res.Sha256;
            await db.SaveChangesAsync();
        }

        var resp = await c.GetAsync($"/api/v1/versions/{v.VersionId}/download?format=pdf");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/pdf", resp.Content.Headers.ContentType!.MediaType);
        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(pdf, body);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(body, 0, 4));
        var filename = resp.Content.Headers.ContentDisposition!.FileName!.Trim('"');
        Assert.EndsWith(".pdf", filename);
        Assert.Equal($"{slug}__Master_Lease-v0.0.1.pdf", filename);
    }

    [Fact]
    public async Task Manual_counter_override_then_next_draft_follows()
    {
        var (c, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Counter Doc");

        var set0 = await c.PutAsJsonAsync($"/api/v1/documents/{docId}/version-counter", new { major = 0, minor = 0, rev = 0 });
        Assert.Equal(HttpStatusCode.OK, set0.StatusCode);
        var v1 = await UploadAsync(c, docId, new byte[] { 1 });
        Assert.Equal((0, 0, 1), (v1.Major, v1.Minor, v1.Revision));

        var set2 = await c.PutAsJsonAsync($"/api/v1/documents/{docId}/version-counter", new { major = 2, minor = 5, rev = 9 });
        Assert.Equal(HttpStatusCode.OK, set2.StatusCode);
        var v2 = await UploadAsync(c, docId, new byte[] { 2 }); // distinct bytes -> new version
        Assert.Equal((2, 5, 10), (v2.Major, v2.Minor, v2.Revision));
    }

    [Fact]
    public async Task Manual_counter_negative_400()
    {
        var (c, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Counter Doc");

        var resp = await c.PutAsJsonAsync($"/api/v1/documents/{docId}/version-counter", new { major = 0, minor = 0, rev = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
