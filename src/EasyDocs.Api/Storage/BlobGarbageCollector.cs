using EasyDocs.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Storage;

// Blob garbage collection (issue #15). Versions are immutable and never hard-deleted, so a blob
// referenced by any Versions or VersionDiffs column is permanent; what CAN become garbage is a blob
// whose commit failed after the bytes were stored, or rows orphaned by manual surgery. This sweeps
// blobs referenced by NOTHING, once per interval (default daily), skipping anything younger than
// the grace window — an upload writes its blob before the version row commits, and grace is what
// keeps the sweep from eating a commit in flight.
//
// Deletion order is object-then-row on purpose: a crash between the two leaves a row the next
// sweep retries (DeleteAsync is idempotent), whereas row-first would leave an object no sweep can
// ever find again.
//
// ponytail: there is a residual race — identical bytes re-uploaded during the sweep of that exact
// sha can dedupe against an object the sweep is deleting. Window is sub-second, requires re-upload
// of content that has been unreferenced for a full grace window, and the failure is a version whose
// download 404s until re-upload. Upgrade path if it ever matters: two-phase tombstone (mark
// PendingDelete, sweep later, dedupe clears the mark).
public sealed class BlobGarbageCollector(
    IServiceScopeFactory scopes, IBlobStore blobs, IConfiguration cfg, ILogger<BlobGarbageCollector> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!cfg.GetValue("BlobGc:Enabled", true))
        {
            log.LogInformation("blob gc disabled by configuration");
            return;
        }
        var interval = TimeSpan.FromSeconds(cfg.GetValue("BlobGc:IntervalSeconds", 86_400));
        var grace = TimeSpan.FromSeconds(cfg.GetValue("BlobGc:GraceSeconds", 86_400));

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try { await SweepAsync(grace, stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "blob gc sweep failed; next attempt in {Interval}", interval);
            }
        }
    }

    private async Task SweepAsync(TimeSpan grace, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var cutoff = DateTimeOffset.UtcNow - grace;

        var candidates = await db.Blobs
            .Where(b => b.CreatedAt < cutoff
                && !db.Versions.Any(v => v.BlobSha256 == b.Sha256 || v.PdfBlobSha256 == b.Sha256)
                && !db.VersionDiffs.Any(d => d.FromSha256 == b.Sha256 || d.ToSha256 == b.Sha256
                    || d.RedlineBlobSha256 == b.Sha256 || d.HtmlBlobSha256 == b.Sha256))
            .Select(b => new { b.Sha256, b.SizeBytes })
            .ToListAsync(ct);
        if (candidates.Count == 0) return;

        long reclaimed = 0;
        var removed = 0;
        foreach (var c in candidates)
        {
            await blobs.DeleteAsync(c.Sha256, ct);
            // Row second, one at a time: the FK Restrict on every referencing column makes this
            // delete fail for a blob that got referenced since the query — that blob is kept
            // (and if the object delete won the race, the uploader's dedupe re-writes the bytes).
            try
            {
                await db.Blobs.Where(b => b.Sha256 == c.Sha256).ExecuteDeleteAsync(ct);
                removed++;
                reclaimed += c.SizeBytes;
            }
            catch (Exception ex) when (ex is DbUpdateException or Npgsql.PostgresException)
            {
                log.LogWarning(ex, "blob {Sha} got referenced mid-sweep; kept", c.Sha256);
            }
        }
        log.LogInformation("blob gc: removed {Count} unreferenced blobs, {Bytes} bytes", removed, reclaimed);
    }
}
