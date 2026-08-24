using Clippit;
using Clippit.Word;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Events;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Versioning;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Merging;

// Concrete (no interface): merge-into-main (spec §5.3, E4, and cross-document pushes in E9). The
// main-branch head is the accepted content, so it becomes the BASE (not tracked changes). A single guarded
// WmlComparer.Compare(mainHead, incoming) renders the incoming branch's edits as a clean single-author
// redline (stamped with the incoming author's DisplayName) on top of current main — ready to accept/reject.
// The compare is guarded: any failure (malformed blob, no incoming branch) degrades to Available=false,
// NEVER throws / partial-commits.
public sealed class WmlComparerMergeService(IBlobStore blobs, EasyDocsDbContext db, VersioningService versioning, EventBus bus)
{
    public record MergeResult(bool Available, Guid? MergeVersionId);

    public async Task<MergeResult> MergeAsync(Guid documentId, Guid leftVersionId, Guid rightVersionId, Guid actorUserId, CancellationToken ct)
    {
        // Sides live in MergeSides so the preview endpoint resolves them identically (spec:
        // 2026-08-24-three-way-merge-review-design.md).
        //
        // ponytail: an incoming_push branch carries the fork point in RootVersionId (spec §8), and
        // merge-into-main (§5.3 [D]) still does not read it — it compares the current main head against
        // the incoming head, so the common ancestor is provenance, not a merge input. The three-way
        // REVIEW surfaces that ancestor to the user without changing this. Ceiling unchanged: a true
        // three-way fuse of both authors over the ancestor is the deferred v1.1 enhancement in §5.3.
        var sides = await MergeSides.ResolveAsync(db, documentId, leftVersionId, rightVersionId, ct);
        if (sides is null) return new MergeResult(false, null);
        var (incoming, incomingBranch, mainHead, mainBranch) = sides;

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

    // Same fallback spelling as every other name-resolving read path (Common/AuthorNames.cs); this one
    // just resolves a single id instead of a page, since a merge has exactly one incoming author.
    private async Task<string> AuthorNameAsync(Guid userId, CancellationToken ct) =>
        (await AuthorNames.ForAsync(db, [userId], ct)).GetValueOrDefault(userId, AuthorNames.Unknown);

    private async Task<byte[]> ReadBytesAsync(string sha, CancellationToken ct)
    {
        await using var s = await blobs.OpenReadAsync(sha, ct);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
