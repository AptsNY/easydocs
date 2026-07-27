using System.Threading.Channels;
using EasyDocs.Api.Data;
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
                var pdfSha = await renderer.RenderToBlobAsync(docx, stoppingToken);
                if (pdfSha is null) continue; // guard: soffice absent/failed — leave PdfBlobSha256 null

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
