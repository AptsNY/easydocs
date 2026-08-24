using Clippit;
using Clippit.Word;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Diffing;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Versioning;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Merging;

// The read half of merging (spec: 2026-08-24-three-way-merge-review-design.md). Commits nothing,
// writes no audit row, and every comparison is guarded — the same discipline WmlComparerDiffService
// enforces, for the same reason: a merge preview must never be the thing that 500s the console.
public sealed class MergePreviewService(
    EasyDocsDbContext db, IBlobStore blobs, WmlComparerDiffService diff, ILogger<MergePreviewService> log)
{
    public record BaseSide(Guid Id, string Number);
    public record VersionSide(Guid Id, string Number, string AuthorName, ChangeSummary? Summary);

    // Available == can main <-> incoming be compared, i.e. WILL the merge work. Every other field
    // degrades on its own: a failed base leg costs a panel, not the merge.
    public record Preview(
        bool Available, BaseSide? Base, VersionSide Main, VersionSide Incoming,
        IReadOnlyList<ThreeWayOverlap.Paragraph>? Overlaps);

    // null => the merge cannot be attempted at all; the endpoint turns that into the same 409 the POST
    // would have returned.
    public async Task<Preview?> BuildAsync(Guid documentId, Guid leftId, Guid rightId, CancellationToken ct)
    {
        var sides = await MergeSides.ResolveAsync(db, documentId, leftId, rightId, ct);
        if (sides is null) return null;
        var (incoming, incomingBranch, mainHead, _) = sides;

        var names = await AuthorNames.ForAsync(db, [mainHead.CreatedBy, incoming.CreatedBy], ct);
        string Who(Guid id) => names.GetValueOrDefault(id, AuthorNames.Unknown);
        static string Num(Domain.DocumentVersion v) => $"{v.Major}.{v.Minor}.{v.Revision}";

        // Does the merge itself have a chance? Same pair, same engine, same bytes as MergeAsync — so
        // this predicts the merge exactly rather than guessing at it.
        var mergeable = await diff.SummaryAsync(mainHead.BlobSha256, incoming.BlobSha256, ct);

        // The fork point. Always present for a Concurrent branch (CommitSaveAsync only creates one when
        // BaseVersionId is set); null is the guard for IncomingPush and legacy rows.
        var baseVersion = incomingBranch.RootVersionId is { } rootId
            ? await db.Versions.FirstOrDefaultAsync(v => v.Id == rootId, ct)
            : null;

        if (baseVersion is null)
            return new Preview(
                mergeable.Available, null,
                new VersionSide(mainHead.Id, Num(mainHead), Who(mainHead.CreatedBy), null),
                new VersionSide(incoming.Id, Num(incoming), Who(incoming.CreatedBy), null),
                null);

        var mainLeg = await diff.SummaryAsync(baseVersion.BlobSha256, mainHead.BlobSha256, ct);
        var incomingLeg = await diff.SummaryAsync(baseVersion.BlobSha256, incoming.BlobSha256, ct);

        return new Preview(
            mergeable.Available,
            new BaseSide(baseVersion.Id, Num(baseVersion)),
            new VersionSide(mainHead.Id, Num(mainHead), Who(mainHead.CreatedBy), Summary(mainLeg)),
            new VersionSide(incoming.Id, Num(incoming), Who(incoming.CreatedBy), Summary(incomingLeg)),
            // Overlap needs BOTH base legs to have compared; without both there is nothing to intersect.
            mainLeg.Available && incomingLeg.Available
                ? await OverlapsAsync(baseVersion.BlobSha256, mainHead.BlobSha256, incoming.BlobSha256, ct)
                : null);
    }

    private static ChangeSummary? Summary(WmlComparerDiffService.DiffSummary s) =>
        s.Available ? new ChangeSummary(s.Insertions, s.Deletions, s.Moves, s.FormatChanges) : null;

    // Re-runs both base comparisons to get the compared PACKAGES — SummaryAsync only returns counts, and
    // the walker needs the XML. Not cached: the redline cache stores rendered html/docx, not the
    // in-memory WmlDocument, and adding a third cached artefact for a screen this size is not worth it.
    //
    // ponytail: two extra Compare calls per preview open. Ceiling: on a very large document that is the
    // slowest thing the screen does. Upgrade path if it bites — cache the overlap list in version_diffs
    // keyed by the (base, main, incoming) sha triple.
    private async Task<IReadOnlyList<ThreeWayOverlap.Paragraph>?> OverlapsAsync(
        string baseSha, string mainSha, string incomingSha, CancellationToken ct)
    {
        try
        {
            var b = await ReadAsync(baseSha, ct);
            var main = WmlComparer.Compare(new WmlDocument("base.docx", b),
                new WmlDocument("main.docx", await ReadAsync(mainSha, ct)), new WmlComparerSettings());
            var incoming = WmlComparer.Compare(new WmlDocument("base.docx", b),
                new WmlDocument("incoming.docx", await ReadAsync(incomingSha, ct)), new WmlComparerSettings());
            return ThreeWayOverlap.Find(main, incoming);
        }
        catch (Exception ex)
        {
            // Degrade, never throw: the redlines and counts are still worth showing without the hint.
            log.LogWarning(ex, "Three-way overlap failed for {Base}/{Main}/{Incoming}", baseSha, mainSha, incomingSha);
            return null;
        }
    }

    private async Task<byte[]> ReadAsync(string sha, CancellationToken ct)
    {
        await using var s = await blobs.OpenReadAsync(sha, ct);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
