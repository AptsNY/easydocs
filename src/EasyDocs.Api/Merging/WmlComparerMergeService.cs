using Clippit;
using Clippit.Word;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Events;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Versioning;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Merging;

// Concrete (no interface): concurrent-branch 3-way merge (spec §5.3, E4). WmlComparer.Compare is pairwise,
// so we get a 3-way merge by SEQUENTIAL APPLICATION: Compare(base, left) stamps left's author, then
// Compare(that, right) regenerates one clean revision layer stamped with right's author — both authors'
// edits end up as tracked changes in a single internally-consistent docx. The whole compare is guarded:
// any failure (malformed blob, no ancestor) degrades to Available=false and NEVER throws / partial-commits.
public sealed class WmlComparerMergeService(IBlobStore blobs, EasyDocsDbContext db, VersioningService versioning, EventBus bus)
{
    public record MergeResult(bool Available, Guid? MergeVersionId);

    public async Task<MergeResult> MergeAsync(Guid documentId, Guid leftVersionId, Guid rightVersionId, Guid actorUserId, CancellationToken ct)
    {
        var left = await db.Versions.FirstOrDefaultAsync(v => v.Id == leftVersionId && v.DocumentId == documentId, ct);
        var right = await db.Versions.FirstOrDefaultAsync(v => v.Id == rightVersionId && v.DocumentId == documentId, ct);
        if (left is null || right is null) return new MergeResult(false, null);

        var leftBranch = await db.Branches.FirstAsync(b => b.Id == left.BranchId, ct);
        var rightBranch = await db.Branches.FirstAsync(b => b.Id == right.BranchId, ct);

        // The merge's common ancestor is the concurrent branch's fork point. Prefer the right side when both
        // are concurrent (M1's typical case: left on main, right on a concurrent branch).
        var concurrent = rightBranch.Kind == BranchKind.Concurrent ? rightBranch
            : leftBranch.Kind == BranchKind.Concurrent ? leftBranch : null;
        if (concurrent?.RootVersionId is not { } baseVersionId) return new MergeResult(false, null);

        var baseVersion = await db.Versions.FirstOrDefaultAsync(v => v.Id == baseVersionId, ct);
        if (baseVersion is null) return new MergeResult(false, null);

        var leftAuthor = await AuthorNameAsync(left.CreatedBy, ct);
        var rightAuthor = await AuthorNameAsync(right.CreatedBy, ct);

        byte[] mergedBytes;
        try
        {
            var baseDoc = new WmlDocument("base.docx", await ReadBytesAsync(baseVersion.BlobSha256, ct));
            var leftDoc = new WmlDocument("left.docx", await ReadBytesAsync(left.BlobSha256, ct));
            var rightDoc = new WmlDocument("right.docx", await ReadBytesAsync(right.BlobSha256, ct));

            var mergedLeft = WmlComparer.Compare(baseDoc, leftDoc, SettingsFor(leftAuthor));
            var merged = WmlComparer.Compare(mergedLeft, rightDoc, SettingsFor(rightAuthor));
            mergedBytes = merged.DocumentByteArray;
        }
        catch
        {
            // Uncomparable (malformed docx, unresolved revisions, …): degrade — nothing committed, branches untouched.
            return new MergeResult(false, null);
        }

        var mainBranch = await db.Branches.FirstAsync(b => b.DocumentId == documentId && b.Ordinal == 0, ct);
        var stored = await blobs.PutAsync(new MemoryStream(mergedBytes), ct);
        var commit = await versioning.CommitSaveAsync(
            new CommitInput(documentId, stored.Sha256, stored.SizeBytes, VersionSource.Merge, actorUserId,
                ExplicitBranchId: mainBranch.Id, BaseVersionId: leftVersionId, MergeParentVersionId: rightVersionId),
            ct);

        concurrent.MergedIntoVersionId = commit.VersionId; // close the merged concurrent branch
        await db.SaveChangesAsync(ct);

        bus.Publish(documentId, "merge.completed", new { mergeVersionId = commit.VersionId });
        return new MergeResult(true, commit.VersionId);
    }

    private static WmlComparerSettings SettingsFor(string author) =>
        new() { AuthorForRevisions = author };

    private async Task<string> AuthorNameAsync(Guid userId, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "Unknown";

    private async Task<byte[]> ReadBytesAsync(string sha, CancellationToken ct)
    {
        await using var s = await blobs.OpenReadAsync(sha, ct);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
