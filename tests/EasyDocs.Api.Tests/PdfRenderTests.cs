using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Publishing;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Tests;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

public class PdfRenderTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public PdfRenderTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private record RegisterDto(Guid Id);
    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId);

    // Mirror LibreOfficePdfRenderer.ResolveSoffice: SOFFICE_PATH, then PATH, then the macOS bundle path.
    private static bool SofficeAvailable() => LibreOfficePdfRenderer.ResolveSoffice() is not null;

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"pdf-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "P", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    private static MultipartFormDataContent Docx(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        return new MultipartFormDataContent { { part, "file", "f.docx" } };
    }

    [SkippableFact]
    public async Task Publish_renders_pdf_when_soffice_available()
    {
        Skip.IfNot(SofficeAvailable(), "soffice not installed on this host");

        var c = await AuthedClientAsync();
        var docId = (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Doc" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;

        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(DocxFixtures.Base()));
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var vid = (await up.Content.ReadFromJsonAsync<UploadDto>())!.VersionId;

        (await c.PostAsJsonAsync($"/api/v1/versions/{vid}/publish", new { kind = "minor" })).EnsureSuccessStatusCode();

        string? pdfSha = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            pdfSha = (await db.Versions.AsNoTracking().FirstAsync(v => v.Id == vid)).PdfBlobSha256;
            if (pdfSha is not null) break;
            await Task.Delay(500);
        }

        Assert.NotNull(pdfSha);

        // The PDF must also be a registered blob — Versions.PdfBlobSha256 is an FK onto `blobs`.
        using (var s1 = _f.Services.CreateScope())
        {
            var db = s1.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            Assert.True(await db.Blobs.AsNoTracking().AnyAsync(b => b.Sha256 == pdfSha),
                "the rendered PDF was never registered in `blobs`, so linking it violates the foreign key");
        }

        using var s2 = _f.Services.CreateScope();
        var blobs = s2.ServiceProvider.GetRequiredService<IBlobStore>();
        await using var pdf = await blobs.OpenReadAsync(pdfSha!);
        var header = new byte[4];
        await pdf.ReadExactlyAsync(header);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(header));
    }

    // Spec §12.2 robustness: garbage in must not take the renderer down. The contract is the method
    // NAME — it returns rather than throwing or hanging — and that whatever comes back is honest.
    //
    // This used to assert the result was null, which was a guess about LibreOffice rather than an
    // observation of it, and the guess was wrong: soffice treats `01 02 03` as a plain text file and
    // renders a perfectly valid one-page PDF of it. Nothing caught that, because the test only runs
    // where soffice exists — it skips on a dev machine, and ci.yml's build-test job does not install
    // LibreOffice. The first environment ever to execute it was the release gate.
    //
    // Producing a PDF from junk is harmless here: the ingest path sniffs magic bytes, so a non-.docx
    // blob is never treated as a document in the first place. What would NOT be harmless is registering
    // something that is not a PDF as one, so that is what is asserted.
    [Fact]
    public async Task Malformed_docx_does_not_crash_renderer()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var store = new FileSystemBlobStore(root);
        var renderer = new LibreOfficePdfRenderer(store, NullLogger<LibreOfficePdfRenderer>.Instance);

        // No exception, no hang. Either outcome is acceptable; crashing is not.
        var result = await renderer.RenderToBlobAsync(new MemoryStream([1, 2, 3]), CancellationToken.None);

        if (result is null) return; // refused outright — also fine

        await using var pdf = await store.OpenReadAsync(result.Value.Sha256);
        var header = new byte[4];
        await pdf.ReadExactlyAsync(header);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(header));
    }
}
