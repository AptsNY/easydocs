using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Versioning;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Copies;

// Materializing an accepted push (spec §8). Shared by both paths that let content in — the pusher who
// already holds an editing role on the target (auto-accepted) and an explicit :accept — so the branch
// wiring and the no-op guard exist once rather than per caller.
public sealed class PushService(EasyDocsDbContext db, VersioningService versioning)
{
    // A push whose content already equals the target's main head has nothing to contribute. It has to be
    // refused rather than materialized: CommitSaveAsync dedupes a sessionless commit against the main-head
    // sha (spec §5.2 step 2), so materializing it would return an existing main version and leave the
    // incoming branch behind with no versions on it.
    public async Task<bool> IsNoOpAsync(Guid targetDocumentId, string blobSha256, CancellationToken ct)
    {
        var main = await db.Branches.FirstAsync(b => b.DocumentId == targetDocumentId && b.Ordinal == 0, ct);
        var head = await db.Versions.Where(v => v.BranchId == main.Id)
            .OrderByDescending(v => v.SeqInBranch).FirstOrDefaultAsync(ct);
        return head is not null && head.BlobSha256 == blobSha256;
    }

    // Land the pushed version on a fresh incoming_push branch of the target. Returns null when there is
    // nothing to materialize (see IsNoOpAsync) — re-checked here because main may have moved between the
    // push and the review. Sets pr.MaterializedVersionId; the caller owns the status transition and save.
    public async Task<Guid?> MaterializeAsync(PushRequest pr, Guid actorUserId, CancellationToken ct)
    {
        var source = await db.Versions.FirstAsync(v => v.Id == pr.SourceVersionId, ct);
        if (await IsNoOpAsync(pr.TargetDocumentId, source.BlobSha256, ct)) return null;

        // The fork point is a version of the TARGET (that is what the copy was forked from), so the
        // incoming branch gets a root inside the target's own history and cross-document merge never has
        // to walk into the copy document (spec §8).
        var forkPoint = await db.Documents.Where(d => d.Id == pr.CopyDocumentId)
            .Select(d => d.ForkedFromVersionId).FirstAsync(ct);

        var maxOrdinal = await db.Branches.Where(b => b.DocumentId == pr.TargetDocumentId)
            .MaxAsync(b => b.Ordinal, ct);
        var branch = new Branch
        {
            Id = Guid.NewGuid(),
            DocumentId = pr.TargetDocumentId,
            Ordinal = maxOrdinal + 1,
            Kind = BranchKind.IncomingPush,
            RootVersionId = forkPoint,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(branch);
        await db.SaveChangesAsync(ct); // CommitSaveAsync resolves ExplicitBranchId with a DB query

        var size = await db.Blobs.Where(b => b.Sha256 == source.BlobSha256).Select(b => b.SizeBytes).FirstAsync(ct);

        // Through the single write path (spec §5.2), so the target's counter, the version.created audit row
        // and the SSE broadcast all behave as for any other write. BaseVersionId is the fork point: it
        // parents the incoming version inside the target's history and gives the version list a change
        // summary against the content the reviewer actually started from.
        var commit = await versioning.CommitSaveAsync(
            new CommitInput(pr.TargetDocumentId, source.BlobSha256, size, VersionSource.CopyPush, actorUserId,
                ExplicitBranchId: branch.Id, BaseVersionId: forkPoint), ct);

        pr.MaterializedVersionId = commit.VersionId;
        return commit.VersionId;
    }
}
