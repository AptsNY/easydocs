using EasyDocs.Api.Data;
using EasyDocs.Api.Events;
using EasyDocs.Api.Versioning;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Publishing;

public sealed record PublishResult(Guid VersionId, int Major, int Minor, int Revision, string Kind);

/// <summary>
/// Publish a selected draft as Minor (R3) or Major (R4): renumber THAT version from the document's
/// authoritative counter, advance the counter so future drafts continue from it (R6), stamp publish
/// metadata. All under the same per-document FOR UPDATE lock as the write path (spec §5.1).
/// </summary>
public sealed class PublishService(EasyDocsDbContext db, EventBus bus)
{
    public async Task<PublishResult> PublishAsync(
        Guid documentId, Guid versionId, string kind, string? name, Guid actorUserId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Documents\" WHERE \"Id\" = {documentId} FOR UPDATE", ct);

        var doc = await db.Documents.FirstAsync(d => d.Id == documentId, ct);
        var version = await db.Versions.FirstAsync(v => v.Id == versionId && v.DocumentId == documentId, ct);

        var counter = (doc.VersionCounterMajor, doc.VersionCounterMinor, doc.VersionCounterRev);
        var (major, minor, rev) = kind == "major" ? Numbering.PublishMajor(counter) : Numbering.PublishMinor(counter);

        version.Major = major;
        version.Minor = minor;
        version.Revision = rev;
        version.PublishedKind = kind;
        version.PublishedBy = actorUserId;
        version.PublishedAt = DateTimeOffset.UtcNow;
        version.PublishName = name;

        // R6: advance the document counter so the next NextDraft continues from the published number.
        doc.VersionCounterMajor = major;
        doc.VersionCounterMinor = minor;
        doc.VersionCounterRev = rev;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        bus.Publish(documentId, "version.published", new { versionId, major, minor, revision = rev, kind });

        // TODO(M2-T2): enqueue PDF render for this published version (write PdfBlobSha256 when done).

        return new PublishResult(versionId, major, minor, rev, kind);
    }
}
