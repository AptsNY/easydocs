using System.Threading.Channels;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Events;
using EasyDocs.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Publishing;

// In-process scheduler (spec §7): publish enqueues a version id; this drains the queue, renders the docx
// to PDF out-of-process, links PdfBlobSha256. Mirrors DiffSummaryWorker. Guarded end-to-end — a render
// failure logs and moves on, never crashes the host.
public sealed class PdfRenderBackgroundService(
    ChannelReader<Guid> jobs, IServiceScopeFactory scopes, EventBus bus, ILogger<PdfRenderBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var versionId in jobs.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
                var version = await db.Versions.FirstOrDefaultAsync(v => v.Id == versionId, stoppingToken);
                if (version is null) continue;

                var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
                var renderer = scope.ServiceProvider.GetRequiredService<LibreOfficePdfRenderer>();

                await using var docx = await blobs.OpenReadAsync(version.BlobSha256, stoppingToken);
                var pdf = await renderer.RenderToBlobAsync(docx, stoppingToken);
                if (pdf is null) continue; // guard: soffice absent/failed — leave PdfBlobSha256 null

                // Versions.PdfBlobSha256 is a foreign key onto `blobs`, so the row has to exist before we
                // point at it. The renderer only writes the content-addressed file; registering the blob is
                // the caller's job here exactly as it is in VersioningService.CommitSaveAsync.
                var pdfSha = pdf.Value.Sha256;
                if (!await db.Blobs.AnyAsync(b => b.Sha256 == pdfSha, stoppingToken))
                    db.Add(new Blob
                    {
                        Sha256 = pdfSha,
                        SizeBytes = pdf.Value.SizeBytes,
                        Mime = "application/pdf",
                        StorageKey = pdfSha,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });

                version.PdfBlobSha256 = pdfSha;
                await db.SaveChangesAsync(stoppingToken);
                bus.Publish(version.DocumentId, "pdf.ready", new { versionId, pdfSha });
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "pdf render job for version {VersionId} failed", versionId);
            }
        }
    }
}
