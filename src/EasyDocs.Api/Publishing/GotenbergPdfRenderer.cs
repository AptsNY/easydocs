using System.Net.Http.Headers;
using EasyDocs.Api.Storage;

namespace EasyDocs.Api.Publishing;

// docx -> PDF through Gotenberg (LibreOffice behind an HTTP API; spec 2026-09-30 version page). One retry,
// null on any failure, never throws but for the caller's own cancellation. Unset GOTENBERG_URL = no renderer.
// No interface — a single concrete renderer.
public sealed class GotenbergPdfRenderer(
    HttpClient http, IConfiguration cfg, IBlobStore blobs, ILogger<GotenbergPdfRenderer> log)
{
    // Gotenberg's own default --api-timeout; waiting longer than the server will only times out on our side.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Returns the stored blob (sha + size) so the caller can insert the matching `blobs` row.
    public async Task<BlobResult?> RenderToBlobAsync(Stream docx, CancellationToken ct)
    {
        var pdf = await RenderAsync(docx, ct);
        return pdf is null ? null : await blobs.PutAsync(new MemoryStream(pdf), ct);
    }

    public async Task<byte[]?> RenderAsync(Stream docx, CancellationToken ct)
    {
        var baseUrl = cfg["GOTENBERG_URL"]?.TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            log.LogWarning("pdf render: GOTENBERG_URL is not set");
            return null;
        }

        byte[] bytes;
        using (var buf = new MemoryStream())
        {
            await docx.CopyToAsync(buf, ct);
            bytes = buf.ToArray();
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new MediaTypeHeaderValue(BlobMime.Docx);
                using var form = new MultipartFormDataContent { { file, "files", "in.docx" } };
                using var res = await http.PostAsync($"{baseUrl}/forms/libreoffice/convert", form, timeout.Token);
                if (res.IsSuccessStatusCode) return await res.Content.ReadAsByteArrayAsync(timeout.Token);
                log.LogWarning("pdf render attempt {Attempt}: gotenberg answered {Status}", attempt, (int)res.StatusCode);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "pdf render attempt {Attempt}: gotenberg call failed", attempt);
            }
        }
        return null;
    }
}
