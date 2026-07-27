using Clippit;
using Clippit.Word;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Events;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Versioning;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Merging;

// Concrete (no interface): merge-into-main (spec §5.3, E4). The main-branch head is the accepted content,
// so it becomes the BASE (not tracked changes). A single guarded WmlComparer.Compare(mainHead, incoming)
// renders the incoming concurrent branch's edits as a clean single-author redline (stamped with the
// incoming author's DisplayName) on top of current main — ready to accept/reject. The compare is guarded:
// any failure (malformed blob, no incoming branch) degrades to Available=false, NEVER throws / partial-commits.
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

        // The incoming side is the one on a concurrent branch; its edits become the tracked redline.
        var (incoming, incomingBranch) = rightBranch.Kind == BranchKind.Concurrent ? (right, rightBranch)
            : leftBranch.Kind == BranchKind.Concurrent ? (left, leftBranch)
            : (null, null!);
        if (incoming is null) return new MergeResult(false, null);

        // base = the TARGET (main) branch's current head at merge time — the accepted content.
        var mainBranch = await db.Branches.FirstAsync(b => b.DocumentId == documentId && b.Ordinal == 0, ct);
        var mainHead = await db.Versions.Where(v => v.BranchId == mainBranch.Id)
            .OrderByDescending(v => v.SeqInBranch).FirstOrDefaultAsync(ct);
        if (mainHead is null) return new MergeResult(false, null);

        var incomingAuthor = await AuthorNameAsync(incoming.CreatedBy, ct);

        byte[] mergedBytes;
        try
        {
            var mainDoc = new WmlDocument("main.docx", await ReadBytesAsync(mainHead.BlobSha256, ct));
            var incomingDoc = new WmlDocument("incoming.docx", await ReadBytesAsync(incoming.BlobSha256, ct));
            var merged = WmlComparer.Compare(mainDoc, incomingDoc, SettingsFor(incomingAuthor));
            mergedBytes = merged.DocumentByteArray;
        }
        catch
        {
            // Uncomparable (malformed docx, …): degrade — nothing committed, branches untouched.
            return new MergeResult(false, null);
        }

        var stored = await blobs.PutAsync(new MemoryStream(mergedBytes), ct);
        var commit = await versioning.CommitSaveAsync(
            new CommitInput(documentId, stored.Sha256, stored.SizeBytes, VersionSource.Merge, actorUserId,
                ExplicitBranchId: mainBranch.Id, BaseVersionId: mainHead.Id, MergeParentVersionId: incoming.Id),
            ct);

        incomingBranch.MergedIntoVersionId = commit.VersionId; // close the merged concurrent branch
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
