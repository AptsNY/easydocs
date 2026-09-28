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
    Guid? MergeParentVersionId = null,
    // Open a new branch of this kind rooted at BaseVersionId and commit onto it — under the document
    // lock, so its Ordinal cannot collide with a concurrent branch (a push accept's incoming branch).
    BranchKind? NewBranchKind = null,
    // What the stored bytes actually are (Storage.BlobMime.Sniff). Null = docx, which is what every
    // in-process caller commits: WOPI PutFile, revert, merge and copy all write OOXML by construction.
    string? Mime = null);

// A commit the write path refuses under its lock because the world moved while the caller prepared it.
// Mapped to 409 problem+json once, in Program.cs, for every caller.
public sealed class CommitConflictException(string title, string detail) : Exception(detail)
{
    public string Title { get; } = title;
}

public sealed record CommitResult(Guid VersionId, int Major, int Minor, int Revision, Guid BranchId, bool Deduped);

/// <summary>
/// The single write path (spec §5.2): HTTP upload/import and (later) WOPI PutFile all route through
/// CommitSaveAsync. This task delivers the fast-forward (main-branch head) path + sha dedupe.
/// Branch-on-stale-base is Task 6.
/// </summary>
public sealed class VersioningService(EasyDocsDbContext db, EventBus bus, ChannelWriter<DiffJob> diffQueue)
{
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
            var blob = new Blob { Sha256 = input.BlobSha256, SizeBytes = input.SizeBytes, Mime = input.Mime ?? Storage.BlobMime.Docx, StorageKey = input.BlobSha256, CreatedAt = DateTimeOffset.UtcNow };
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
        var doc = await db.LockDocumentAsync(input.DocumentId, ct);
        var mainBranch = await db.Branches.FirstAsync(b => b.DocumentId == input.DocumentId && b.Ordinal == 0, ct);
        var mainHead = await db.Versions.Where(v => v.BranchId == mainBranch.Id)
            .OrderByDescending(v => v.SeqInBranch).FirstOrDefaultAsync(ct);

        // Load the session up front (spec §5.2): its BranchId decides pinning and its LastCommittedSha is the dedupe key.
        var session = input.SessionId is { } sessionId
            ? await db.EditSessions.FirstAsync(s => s.Id == sessionId, ct)
            : null;
        // WOPI/WebDAV authorize by loading the session before the lock; read what a concurrent save
        // of the same session committed, not that stale copy.
        if (session is not null) await db.Entry(session).ReloadAsync(ct);
        // The callers copied the session's base into the input BEFORE the lock; the reloaded session is
        // the truth. Otherwise two overlapping saves of one session (a WebDAV retry, Collabora autosave
        // over an explicit save) see each other as concurrent editors and fork a branch.
        var baseVersionId = session?.BaseVersionId ?? input.BaseVersionId;

        // A merge was computed against a main head and one incoming branch, both read before this lock
        // and seconds of comparison ago. If main moved, committing would silently drop the newer save
        // from the head; if the branch was merged meanwhile, it would be merged twice.
        Branch? mergedBranch = null;
        if (input.MergeParentVersionId is { } mergeParentId)
        {
            var parent = await db.Versions.FirstAsync(v => v.Id == mergeParentId, ct);
            mergedBranch = await db.Branches.FirstAsync(b => b.Id == parent.BranchId, ct);
            await db.Entry(mergedBranch).ReloadAsync(ct);
            // Checked first: of two racing merges, the loser is told the truth — not "review again".
            if (mergedBranch.MergedIntoVersionId is not null)
                throw new CommitConflictException("Already merged", "This branch has already been merged.");
            if (baseVersionId != mainHead?.Id)
                throw new CommitConflictException("Main moved", "Main changed while the merge was prepared; review the merge again.");
            // The incoming side too: a save landing on the branch mid-review would otherwise be stranded on
            // a branch marked merged, which can never be merged again.
            var branchHeadSeq = await db.Versions.Where(v => v.BranchId == parent.BranchId).MaxAsync(v => v.SeqInBranch, ct);
            if (parent.SeqInBranch != branchHeadSeq)
                throw new CommitConflictException("Branch moved", "The branch changed while the merge was prepared; review the merge again.");
        }

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
            if (mergedBranch is not null)
            {
                mergedBranch.MergedIntoVersionId = existing.Id; // merged content equals main: still closed
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return new CommitResult(existing.Id, existing.Major, existing.Minor, existing.Revision, existing.BranchId, Deduped: true);
        }

        // Branch decision (spec §5.2 step 4).
        Branch targetBranch;
        if (input.ExplicitBranchId is { } explicitId)
            targetBranch = await db.Branches.FirstAsync(b => b.Id == explicitId, ct);
        else if (input.NewBranchKind is { } newKind)
            targetBranch = await OpenBranchAsync(newKind);
        else if (session?.BranchId is { } pinnedId
                 && !await db.Branches.AnyAsync(b => b.Id == pinnedId && b.MergedIntoVersionId != null, ct))
            targetBranch = await db.Branches.FirstAsync(b => b.Id == pinnedId, ct); // already diverged — fast-forward on it
        // A session pinned to a branch that has since been merged falls through: its next save opens a
        // fresh branch (its base is not main's head) instead of piling onto one that can never merge again.
        else if (baseVersionId is null || baseVersionId == mainHead?.Id)
            targetBranch = mainBranch; // fast-forward on main
        else
        {
            // Stale base: the main head moved on. Branch instead of overwriting (E4 "zero lost edits").
            targetBranch = await OpenBranchAsync(BranchKind.Concurrent);
            if (session is not null) session.BranchId = targetBranch.Id; // pin so later saves fast-forward here
        }

        // Under the lock, so MAX(Ordinal)+1 cannot collide with a concurrent commit's new branch.
        async Task<Branch> OpenBranchAsync(BranchKind kind)
        {
            var maxOrdinal = await db.Branches.Where(b => b.DocumentId == input.DocumentId).MaxAsync(b => b.Ordinal, ct);
            var branch = new Branch
            {
                Id = Guid.NewGuid(), DocumentId = input.DocumentId, Ordinal = maxOrdinal + 1,
                Kind = kind, RootVersionId = baseVersionId, CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Add(branch);
            return branch;
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
            ParentVersionId = targetHead?.Id ?? baseVersionId, MergeParentVersionId = input.MergeParentVersionId,
            Major = major, Minor = minor, Revision = rev,
            Source = input.Source, BlobSha256 = input.BlobSha256, CreatedBy = input.ActorUserId, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(version);
        if (mergedBranch is not null) mergedBranch.MergedIntoVersionId = version.Id; // closed in the same transaction

        // The session's base advances with its own commits: otherwise its next save sees main "moved"
        // (by this very commit) and forks a spurious branch, and WOPI GetFile keeps serving the bytes the
        // session opened with. On a pinned branch the pinned path above never reads it, so this is safe.
        if (session is not null)
        {
            session.LastCommittedSha = input.BlobSha256;
            session.BaseVersionId = version.Id;
        }

        // Audited here rather than at each caller: this is the single write path (spec §5.2), so one row
        // covers upload, import, WOPI PutFile, merge and revert. Inside the transaction, so the trail
        // cannot disagree with the version it records. The dedupe path returns above — nothing changed.
        db.Add(Audit.Event(doc.OrgId, input.DocumentId, input.ActorUserId, "version.created",
            "version", version.Id.ToString(),
            new { number = $"{major}.{minor}.{rev}", source = input.Source.ToString(), branchId = targetBranch.Id }));

        // Enqueue the parent->child diff for eager numeric-summary computation (spec §7) — a durable
        // BackgroundJobs row in THIS transaction, so the job commits iff the version does (issue #16).
        // Only when this commit has a parent — a brand-new document's first version has nothing to
        // compare against.
        var parentSha = targetHead?.BlobSha256;
        if (parentSha is null && version.ParentVersionId is { } parentId)
            parentSha = await db.Versions.Where(v => v.Id == parentId).Select(v => v.BlobSha256).FirstOrDefaultAsync(ct);
        var diffJob = parentSha is not null ? new DiffJob(parentSha, version.BlobSha256, input.DocumentId) : null;
        if (diffJob is not null)
            db.Add(BackgroundJobs.For(BackgroundJobs.Diff, diffJob));

        // Reindex the document's content for search (issue #12) — the worker recomputes the main
        // head itself, so enqueueing on every commit (branch or not) is always safe.
        db.Add(BackgroundJobs.For(BackgroundJobs.Extract, input.DocumentId));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        bus.Publish(input.DocumentId, "version.created",
            new { versionId = version.Id, major, minor, revision = rev, branchId = targetBranch.Id });

        if (diffJob is not null)
            diffQueue.TryWrite(diffJob); // nudge only — the committed row is the job

        return new CommitResult(version.Id, major, minor, rev, targetBranch.Id, Deduped: false);
    }
}
