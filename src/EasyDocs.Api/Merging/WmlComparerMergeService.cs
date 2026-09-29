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
// main-branch head is the accepted content, so it becomes the BASE (not tracked changes). The incoming
// branch's OWN changes since the fork point are folded onto main (ThreeWayMerge), and one guarded
// WmlComparer.Compare(mainHead, thatResult) renders them as a clean single-author redline (stamped with
// the incoming author's DisplayName) on top of current main — ready to accept/reject. Everything is
// guarded: any failure (malformed blob, no incoming branch, a change that cannot carry over) degrades to
// Available=false, NEVER throws / partial-commits.
public sealed class WmlComparerMergeService(IBlobStore blobs, EasyDocsDbContext db, VersioningService versioning, EventBus bus)
{
    public record MergeResult(bool Available, Guid? MergeVersionId);

    public async Task<MergeResult> MergeAsync(Guid documentId, Guid leftVersionId, Guid rightVersionId, Guid actorUserId, CancellationToken ct)
    {
        // Sides live in MergeSides so the preview endpoint resolves them identically (spec:
        // 2026-08-24-three-way-merge-review-design.md).
        //
        // Why the fork point is a merge INPUT: a plain Compare(mainHead, incoming) shows every way main
        // differs from incoming as the incoming author's change — including main's own edits the branch
        // never had, proposed as deletions that Accept All silently applies. Folding only
        // ancestor -> incoming onto main (ThreeWayMerge) is what makes the redline "their changes".
        //
        // ponytail: the fold is block-level and refuses (409) whatever it cannot guarantee — see the
        // list in ThreeWayMerge. What it does settle: where both sides changed the same PARAGRAPH
        // differently, incoming's paragraph is proposed over main's, so main's edit there shows as a
        // tracked reversion; the preview lists exactly those blocks (it runs this same Merge dry).
        // Not carried from the incoming side: section/page setup, new styles or list definitions, and
        // comments (WmlComparer drops those on every path). With no fork point at all (a legacy branch
        // row with a null RootVersionId) there is nothing to fold against and this falls back to the
        // two-way compare, reversions included; the review screen says so. Upgrade path: recurse into
        // tables and content controls, word-level three-way inside conflicting paragraphs.
        var sides = await MergeSides.ResolveAsync(db, documentId, leftVersionId, rightVersionId, ct);
        if (sides is null) return new MergeResult(false, null);
        var (incoming, _, mainHead, mainBranch, baseVersion) = sides;

        var incomingAuthor = await AuthorNameAsync(incoming.CreatedBy, ct);

        byte[] mergedBytes;
        try
        {
            mergedBytes = Merge(
                baseVersion is null ? null : await ReadBytesAsync(baseVersion.BlobSha256, ct),
                await ReadBytesAsync(mainHead.BlobSha256, ct), await ReadBytesAsync(incoming.BlobSha256, ct),
                incomingAuthor).Docx;
        }
        catch
        {
            // Uncomparable (malformed docx, a change ThreeWayMerge cannot carry over, …): degrade — nothing committed, branches untouched.
            return new MergeResult(false, null);
        }

        var stored = await blobs.PutAsync(new MemoryStream(mergedBytes), ct);
        var commit = await versioning.CommitSaveAsync(
            new CommitInput(documentId, stored.Sha256, stored.SizeBytes, VersionSource.Merge, actorUserId,
                ExplicitBranchId: mainBranch.Id, BaseVersionId: mainHead.Id, MergeParentVersionId: incoming.Id),
            ct);

        // CommitSaveAsync closed the incoming branch (MergedIntoVersionId) inside its transaction, and
        // refused with a 409 if main moved or the branch was merged while this comparison ran.
        bus.Publish(documentId, "merge.completed", new { mergeVersionId = commit.VersionId });
        return new MergeResult(true, commit.VersionId);
    }

    // The merge proper, shared with the preview so `available` and the overlaps describe exactly what
    // the button does. Throws when the merge must refuse. With no ancestor it is the two-way compare.
    public static ThreeWayMerge.Result Merge(byte[]? ancestor, byte[] main, byte[] incoming, string author)
    {
        var fold = ancestor is null ? new ThreeWayMerge.Result(incoming, []) : ThreeWayMerge.Apply(ancestor, main, incoming);
        var merged = WmlComparer.Compare(new WmlDocument("main.docx", main), new WmlDocument("incoming.docx", fold.Docx),
            new WmlComparerSettings { AuthorForRevisions = author });
        return fold with { Docx = merged.DocumentByteArray };
    }

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
