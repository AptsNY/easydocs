using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Documents;

// Content indexing (issue #12): every commit enqueues the document id; this recomputes the
// document's main-branch head at processing time, extracts its text, and upserts DocumentTexts.
// Recomputing at processing time (instead of trusting the payload) makes a burst of commits
// coalesce to the right answer no matter the order the jobs run in.
//
// Poll-only — no nudge channel. Search indexing tolerates the poll interval of latency, and the
// second Channel<Guid> registration a nudge would need is a DI collision with the PDF queue not
// worth solving for this.
public sealed class TextIndexWorker(
    IServiceScopeFactory scopes, IConfiguration cfg, ILogger<TextIndexWorker> log)
    : DurableJobWorker<Guid>(BackgroundJobs.Extract, scopes, cfg, log)
{
    protected override async Task AwaitNudgeAsync(CancellationToken ct)
        => await Task.Delay(Timeout.InfiniteTimeSpan, ct); // cancelled by the poll timer

    protected override async Task HandleAsync(IServiceProvider services, Guid documentId, CancellationToken ct)
    {
        var db = services.GetRequiredService<EasyDocsDbContext>();
        var head = await (
            from b in db.Branches
            where b.DocumentId == documentId && b.Kind == BranchKind.Main
            from v in db.Versions
            where v.BranchId == b.Id
            orderby v.SeqInBranch descending
            select v.BlobSha256).FirstOrDefaultAsync(ct);
        if (head is null) return; // document (or its history) is gone — nothing to index

        var blobs = services.GetRequiredService<IBlobStore>();
        string text;
        await using (var stream = await blobs.OpenReadAsync(head, ct))
        {
            // ZipArchive needs a seekable stream; the S3 backend's isn't. Spool — heads are documents,
            // not archives, and the extractor stops reading at MaxChars anyway.
            using var seekable = new MemoryStream();
            await stream.CopyToAsync(seekable, ct);
            seekable.Position = 0;
            text = DocxText.Extract(seekable);
        }

        var row = await db.DocumentTexts.FirstOrDefaultAsync(t => t.DocumentId == documentId, ct);
        if (row is null)
            db.Add(new DocumentText { DocumentId = documentId, Text = text, UpdatedAt = DateTimeOffset.UtcNow });
        else
        {
            row.Text = text;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
