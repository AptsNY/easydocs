using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Merging;

// Which of the two version ids is "somebody else's work to bring onto main", and what main's head is
// right now. Extracted from WmlComparerMergeService so the merge PREVIEW resolves the identical sides:
// a preview that described a different merge than the button performs would be worse than no preview.
internal static class MergeSides
{
    internal record Sides(
        DocumentVersion Incoming, Branch IncomingBranch,
        DocumentVersion MainHead, Branch MainBranch);

    // Two branch kinds qualify as incoming: a Concurrent branch from a stale-base save (M1, E4) and an
    // IncomingPush branch materialised from a copy (M4, E9). Both are treated identically.
    internal static bool IsIncoming(Branch b) => b.Kind is BranchKind.Concurrent or BranchKind.IncomingPush;

    // null when the merge cannot be attempted at all: a version id that is not this document's, neither
    // side on an incoming branch, or a document with no main head yet.
    internal static async Task<Sides?> ResolveAsync(
        EasyDocsDbContext db, Guid documentId, Guid leftVersionId, Guid rightVersionId, CancellationToken ct)
    {
        var left = await db.Versions.FirstOrDefaultAsync(v => v.Id == leftVersionId && v.DocumentId == documentId, ct);
        var right = await db.Versions.FirstOrDefaultAsync(v => v.Id == rightVersionId && v.DocumentId == documentId, ct);
        if (left is null || right is null) return null;

        var leftBranch = await db.Branches.FirstAsync(b => b.Id == left.BranchId, ct);
        var rightBranch = await db.Branches.FirstAsync(b => b.Id == right.BranchId, ct);

        var (incoming, incomingBranch) = IsIncoming(rightBranch) ? (right, rightBranch)
            : IsIncoming(leftBranch) ? (left, leftBranch)
            : (null, null!);
        if (incoming is null) return null;

        // Main's head is the merge BASE: it is the accepted content at merge time, which is what makes
        // the incoming side's edits the tracked redline rather than the other way round.
        var mainBranch = await db.Branches.FirstAsync(b => b.DocumentId == documentId && b.Ordinal == 0, ct);
        var mainHead = await db.Versions.Where(v => v.BranchId == mainBranch.Id)
            .OrderByDescending(v => v.SeqInBranch).FirstOrDefaultAsync(ct);
        if (mainHead is null) return null;

        return new Sides(incoming, incomingBranch, mainHead, mainBranch);
    }
}
