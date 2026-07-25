using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Versioning;

public sealed record CommitInput(
    Guid DocumentId, string BlobSha256, long SizeBytes, VersionSource Source, Guid ActorUserId,
    Guid? SessionId = null, Guid? BaseVersionId = null, Guid? ExplicitBranchId = null,
    Guid? MergeParentVersionId = null);

public sealed record CommitResult(Guid VersionId, int Major, int Minor, int Revision, Guid BranchId, bool Deduped);

/// <summary>
/// The single write path (spec §5.2): HTTP upload/import and (later) WOPI PutFile all route through
/// CommitSaveAsync. This task delivers the fast-forward (main-branch head) path + sha dedupe.
/// Branch-on-stale-base is Task 6.
/// </summary>
public sealed class VersioningService(EasyDocsDbContext db)
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public async Task<CommitResult> CommitSaveAsync(CommitInput input, CancellationToken ct)
    {
        // Blobs are content-addressed and immutable — insert only if this sha is new
        // (caller already ran IBlobStore.PutAsync).
        if (!await db.Blobs.AnyAsync(bl => bl.Sha256 == input.BlobSha256, ct))
        {
            db.Add(new Blob { Sha256 = input.BlobSha256, SizeBytes = input.SizeBytes, Mime = DocxMime, StorageKey = input.BlobSha256, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
        }

        // Per-document row lock so the authoritative counter increment (spec §5.1) is race-safe.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Documents\" WHERE \"Id\" = {input.DocumentId} FOR UPDATE", ct);

        var doc = await db.Documents.FirstAsync(d => d.Id == input.DocumentId, ct);
        var mainBranch = await db.Branches.FirstAsync(b => b.DocumentId == input.DocumentId && b.Ordinal == 0, ct);
        var head = await db.Versions.Where(v => v.BranchId == mainBranch.Id)
            .OrderByDescending(v => v.SeqInBranch).FirstOrDefaultAsync(ct);

        // Dedupe: an identical head sha is a no-op (the sessionless dedupe key, spec §5.2).
        if (head is not null && input.BlobSha256 == head.BlobSha256)
        {
            await tx.CommitAsync(ct);
            return new CommitResult(head.Id, head.Major, head.Minor, head.Revision, mainBranch.Id, Deduped: true);
        }

        var target = input.ExplicitBranchId ?? mainBranch.Id; // fast-forward only in this task
        var (major, minor, rev) = Numbering.NextDraft((doc.VersionCounterMajor, doc.VersionCounterMinor, doc.VersionCounterRev));
        doc.VersionCounterMajor = major;
        doc.VersionCounterMinor = minor;
        doc.VersionCounterRev = rev;

        var version = new DocumentVersion
        {
            Id = Guid.NewGuid(), DocumentId = input.DocumentId, BranchId = target, SeqInBranch = (head?.SeqInBranch ?? 0) + 1,
            ParentVersionId = head?.Id, MergeParentVersionId = input.MergeParentVersionId,
            Major = major, Minor = minor, Revision = rev,
            Source = input.Source, BlobSha256 = input.BlobSha256, CreatedBy = input.ActorUserId, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(version);

        if (input.SessionId is { } sessionId)
        {
            var session = await db.EditSessions.FirstAsync(s => s.Id == sessionId, ct);
            session.LastCommittedSha = input.BlobSha256;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // TODO(Task 7/8): emit SSE version.created + enqueue diff.
        return new CommitResult(version.Id, major, minor, rev, target, Deduped: false);
    }
}
