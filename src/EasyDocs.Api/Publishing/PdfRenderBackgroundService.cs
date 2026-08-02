using System.Threading.Channels;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Events;
using EasyDocs.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Publishing;

// PDF rendering (spec §7): publish enqueues a version id into the durable BackgroundJobs queue
// (issue #16, inside the publish transaction); this drains it, renders the docx to PDF
// out-of-process, links PdfBlobSha256. The Channel<Guid> is only the wake-up nudge. Guarded
// end-to-end — a render failure logs and retries via the queue, and never crashes the host.
public sealed class PdfRenderBackgroundService(
    ChannelReader<Guid> nudges, IServiceScopeFactory scopes, IConfiguration cfg,
    EventBus bus, ILogger<PdfRenderBackgroundService> log)
    : DurableJobWorker<Guid>(BackgroundJobs.Pdf, scopes, cfg, log)
{
    protected override async Task AwaitNudgeAsync(CancellationToken ct) => await nudges.ReadAsync(ct);

    protected override async Task HandleAsync(IServiceProvider services, Guid versionId, CancellationToken ct)
    {
        var db = services.GetRequiredService<EasyDocsDbContext>();
        var version = await db.Versions.FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is null) return; // consumed: the version is gone, there is nothing to render

        var blobs = services.GetRequiredService<IBlobStore>();
        var renderer = services.GetRequiredService<LibreOfficePdfRenderer>();

        // A version whose bytes are ALREADY a PDF must not be converted. soffice does not pass a
        // PDF through — it imports it into Draw and re-lays it out, so publishing a scanned lease
        // handed the user back a different document: different size, different producer, text
        // reflowed or rasterised. The published PDF has to BE the file they uploaded.
        //
        // Sniffed from the bytes for the same reason downloads are (spec §10.3): the client's
        // multipart Content-Type is untrusted, and Blobs.Mime can predate the sniffing fix.
        // The blobs row already exists for this sha, so pointing the FK at it is safe.
        var (mime, _) = await BlobMime.SniffAsync(blobs, version.BlobSha256, ct);
        if (mime == BlobMime.Pdf)
        {
            version.PdfBlobSha256 = version.BlobSha256;
            await db.SaveChangesAsync(ct);
            bus.Publish(version.DocumentId, "pdf.ready",
                new { versionId, pdfSha = version.BlobSha256 });
            return;
        }

        await using var docx = await blobs.OpenReadAsync(version.BlobSha256, ct);
        var pdf = await renderer.RenderToBlobAsync(docx, ct);
        if (pdf is null) return; // guard: soffice absent/failed — leave PdfBlobSha256 null

        // Versions.PdfBlobSha256 is a foreign key onto `blobs`, so the row has to exist before we
        // point at it. The renderer only writes the content-addressed file; registering the blob is
        // the caller's job here exactly as it is in VersioningService.CommitSaveAsync.
        var pdfSha = pdf.Value.Sha256;
        if (!await db.Blobs.AnyAsync(b => b.Sha256 == pdfSha, ct))
            db.Add(new Blob
            {
                Sha256 = pdfSha,
                SizeBytes = pdf.Value.SizeBytes,
                Mime = "application/pdf",
                StorageKey = pdfSha,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        version.PdfBlobSha256 = pdfSha;
        await db.SaveChangesAsync(ct);
        bus.Publish(version.DocumentId, "pdf.ready", new { versionId, pdfSha });
    }
}
