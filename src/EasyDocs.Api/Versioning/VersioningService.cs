using System.Threading.Channels;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Diffing;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Events;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
public sealed class VersioningService(EasyDocsDbContext db, EventBus bus, ChannelWriter<DiffJob> diffQueue)
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public async Task<CommitResult> CommitSaveAsync(CommitInput input, CancellationToken ct)
    {
        // Blobs are content-addressed and immutable — insert only if this sha is new
        // (caller already ran IBlobStore.PutAsync). Check-then-insert is racy: two concurrent commits
        // of the same new content both pass this AnyAsync check. That's fine per spec §5.2 — Blobs is
        // keyed by Sha256, so the loser's row would be byte-identical to the winner's and the file was
        // already written to disk by IBlobStore.PutAsync before either commit got here — so the loser
        // swallows the unique-violation and carries on rather than 500ing an ordinary concurrent upload.
        if (!await db.Blobs.AnyAsync(bl => bl.Sha256 == input.BlobSha256, ct))
        {
            var blob = new Blob { Sha256 = input.BlobSha256, SizeBytes = input.SizeBytes, Mime = DocxMime, StorageKey = input.BlobSha256, CreatedAt = DateTimeOffset.UtcNow };
            db.Add(blob);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Lost the race. Detach rather than just catching: SaveChangesAsync is called again
                // later in this method (for the version + audit rows), and a tracked Added entity that
                // failed to insert once would be retried — and fail — every time after.
                db.Entry(blob).State = EntityState.Detached;
            }
        }

        // Per-document row lock so the authoritative counter increment (spec §5.1) is race-safe.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Documents\" WHERE \"Id\" = {input.DocumentId} FOR UPDATE", ct);

        var doc = await db.Documents.FirstAsync(d => d.Id == input.DocumentId, ct);
        var mainBranch = await db.Branches.FirstAsync(b => b.DocumentId == input.DocumentId && b.Ordinal == 0, ct);
        var mainHead = await db.Versions.Where(v => v.BranchId == mainBranch.Id)
            .OrderByDescending(v => v.SeqInBranch).FirstOrDefaultAsync(ct);

        // Load the session up front (spec §5.2): its BranchId decides pinning and its LastCommittedSha is the dedupe key.
        var session = input.SessionId is { } sessionId
            ? await db.EditSessions.FirstAsync(s => s.Id == sessionId, ct)
            : null;

        // Dedupe (spec §5.2 step 2): a session re-PUT of unchanged content is a no-op on any branch;
        // a sessionless upload dedupes against the main head sha.
        var deduped = session is not null
            ? input.BlobSha256 == session.LastCommittedSha
            : mainHead is not null && input.BlobSha256 == mainHead.BlobSha256;
        if (deduped)
        {
            var existing = await db.Versions
                .Where(v => v.DocumentId == input.DocumentId && v.BlobSha256 == input.BlobSha256)
                .OrderByDescending(v => v.CreatedAt).FirstAsync(ct);
            await tx.CommitAsync(ct);
            return new CommitResult(existing.Id, existing.Major, existing.Minor, existing.Revision, existing.BranchId, Deduped: true);
        }

        // Branch decision (spec §5.2 step 4).
        Branch targetBranch;
        if (input.ExplicitBranchId is { } explicitId)
            targetBranch = await db.Branches.FirstAsync(b => b.Id == explicitId, ct);
        else if (session?.BranchId is { } pinnedId)
            targetBranch = await db.Branches.FirstAsync(b => b.Id == pinnedId, ct); // already diverged — fast-forward on it
        else if (input.BaseVersionId is null || input.BaseVersionId == mainHead?.Id)
            targetBranch = mainBranch; // fast-forward on main
        else
        {
            // Stale base: the main head moved on. Branch instead of overwriting (E4 "zero lost edits").
            var maxOrdinal = await db.Branches.Where(b => b.DocumentId == input.DocumentId).MaxAsync(b => b.Ordinal, ct);
            targetBranch = new Branch
            {
                Id = Guid.NewGuid(), DocumentId = input.DocumentId, Ordinal = maxOrdinal + 1,
                Kind = BranchKind.Concurrent, RootVersionId = input.BaseVersionId, CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Add(targetBranch);
            if (session is not null) session.BranchId = targetBranch.Id; // pin so later saves fast-forward here
        }

        // Head of the TARGET branch drives SeqInBranch/ParentVersionId (not always main).
        var targetHead = await db.Versions.Where(v => v.BranchId == targetBranch.Id)
            .OrderByDescending(v => v.SeqInBranch).FirstOrDefaultAsync(ct);

        var (major, minor, rev) = Numbering.NextDraft((doc.VersionCounterMajor, doc.VersionCounterMinor, doc.VersionCounterRev));
        doc.VersionCounterMajor = major;
        doc.VersionCounterMinor = minor;
        doc.VersionCounterRev = rev;

        var version = new DocumentVersion
        {
            Id = Guid.NewGuid(), DocumentId = input.DocumentId, BranchId = targetBranch.Id, SeqInBranch = (targetHead?.SeqInBranch ?? 0) + 1,
            ParentVersionId = targetHead?.Id ?? input.BaseVersionId, MergeParentVersionId = input.MergeParentVersionId,
            Major = major, Minor = minor, Revision = rev,
            Source = input.Source, BlobSha256 = input.BlobSha256, CreatedBy = input.ActorUserId, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(version);

        if (session is not null) session.LastCommittedSha = input.BlobSha256;

        // Audited here rather than at each caller: this is the single write path (spec §5.2), so one row
        // covers upload, import, WOPI PutFile, merge and revert. Inside the transaction, so the trail
        // cannot disagree with the version it records. The dedupe path returns above — nothing changed.
        db.Add(Audit.Event(doc.OrgId, input.DocumentId, input.ActorUserId, "version.created",
            "version", version.Id.ToString(),
            new { number = $"{major}.{minor}.{rev}", source = input.Source.ToString(), branchId = targetBranch.Id }));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        bus.Publish(input.DocumentId, "version.created",
            new { versionId = version.Id, major, minor, revision = rev, branchId = targetBranch.Id });

        // Enqueue the parent->child diff for eager numeric-summary computation (spec §7). Only when this
        // commit has a parent — a brand-new document's first version has nothing to compare against.
        var parentSha = targetHead?.BlobSha256;
        if (parentSha is null && version.ParentVersionId is { } parentId)
            parentSha = await db.Versions.Where(v => v.Id == parentId).Select(v => v.BlobSha256).FirstOrDefaultAsync(ct);
        if (parentSha is not null)
            diffQueue.TryWrite(new DiffJob(parentSha, version.BlobSha256, input.DocumentId));

        return new CommitResult(version.Id, major, minor, rev, targetBranch.Id, Deduped: false);
    }
}
