using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Diffing;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Versioning;

namespace EasyDocs.Api.Merging;

// The read half of merging (spec: 2026-08-24-three-way-merge-review-design.md). Commits nothing,
// writes no audit row, and every comparison is guarded — the same discipline WmlComparerDiffService
// enforces, for the same reason: a merge preview must never be the thing that 500s the console.
public sealed class MergePreviewService(
    EasyDocsDbContext db, IBlobStore blobs, WmlComparerDiffService diff, ILogger<MergePreviewService> log)
{
    public record BaseSide(Guid Id, string Number);
    public record VersionSide(Guid Id, string Number, string AuthorName, ChangeSummary? Summary);

    // Available == WILL the merge work (it runs the merge dry). Overlaps == the ancestor blocks both
    // sides changed, as the merge settled them; null when the merge would refuse or there is no fork
    // point. The leg summaries degrade on their own: a failed base leg costs a panel, not the merge.
    public record Preview(
        bool Available, BaseSide? Base, VersionSide Main, VersionSide Incoming,
        IReadOnlyList<ThreeWayMerge.Overlap>? Overlaps);

    // null => the merge cannot be attempted at all; the endpoint turns that into the same 409 the POST
    // would have returned.
    public async Task<Preview?> BuildAsync(Guid documentId, Guid leftId, Guid rightId, CancellationToken ct)
    {
        // The INCOMING side is pinned: CommitSaveAsync refuses (409 "Branch moved") a merge whose right
        // is no longer its branch's head, so a save landing on the branch mid-review is never stranded.
        //
        // ponytail: the reviewed MAIN base is not pinned. MergeSides re-resolves main's head from the database
        // on every call, and POST /merges does the same at click time with no expected-head — so if
        // somebody lands a version on main while this screen is open, the merge runs against a head the
        // reader never saw, and answers 201 without mentioning it. Nothing is lost (ADR-1: the merge is
        // itself a revertible version), which is why this ships, but a review screen that can review the
        // wrong thing is a weaker promise than the feature makes. It is the plausible case, not an exotic
        // one: this whole feature exists because people edit these documents concurrently.
        //
        // Upgrade path, cheapest first: have the screen re-fetch this preview immediately before posting
        // and refuse if main.id moved (client-only, keeps POST /merges byte-identical). Failing that,
        // give the POST an expected-head parameter — a deliberate change to a frozen endpoint, so it
        // needs its own decision.
        var sides = await MergeSides.ResolveAsync(db, documentId, leftId, rightId, ct);
        if (sides is null) return null;
        var (incoming, _, mainHead, _, baseVersion) = sides;

        var names = await AuthorNames.ForAsync(db, [mainHead.CreatedBy, incoming.CreatedBy], ct);
        string Who(Guid id) => names.GetValueOrDefault(id, AuthorNames.Unknown);
        static string Num(Domain.DocumentVersion v) => $"{v.Major}.{v.Minor}.{v.Revision}";

        if (baseVersion is null)
        {
            // No fork point: the merge is the two-way Compare(main, incoming), so that is what to predict.
            var mergeable = await diff.SummaryAsync(mainHead.BlobSha256, incoming.BlobSha256, ct);
            return new Preview(
                mergeable.Available, null,
                new VersionSide(mainHead.Id, Num(mainHead), Who(mainHead.CreatedBy), null),
                new VersionSide(incoming.Id, Num(incoming), Who(incoming.CreatedBy), null),
                null);
        }

        var mainLeg = await diff.SummaryAsync(baseVersion.BlobSha256, mainHead.BlobSha256, ct);
        var incomingLeg = await diff.SummaryAsync(baseVersion.BlobSha256, incoming.BlobSha256, ct);
        // The merge itself, dry: the same fold and the same Compare MergeAsync runs, on the same bytes, so
        // `available` predicts the POST exactly and the overlaps ARE the blocks the merge settles.
        var merge = await MergeAsync(baseVersion.BlobSha256, mainHead.BlobSha256, incoming.BlobSha256, ct);

        return new Preview(
            merge is not null,
            new BaseSide(baseVersion.Id, Num(baseVersion)),
            new VersionSide(mainHead.Id, Num(mainHead), Who(mainHead.CreatedBy), Summary(mainLeg)),
            new VersionSide(incoming.Id, Num(incoming), Who(incoming.CreatedBy), Summary(incomingLeg)),
            merge?.Overlaps);
    }

    private static ChangeSummary? Summary(WmlComparerDiffService.DiffSummary s) =>
        s.Available ? new ChangeSummary(s.Insertions, s.Deletions, s.Moves, s.FormatChanges) : null;

    // null when the merge would refuse (409 "Merge unavailable").
    private async Task<ThreeWayMerge.Result?> MergeAsync(string baseSha, string mainSha, string incomingSha, CancellationToken ct)
    {
        try
        {
            return WmlComparerMergeService.Merge(await ReadAsync(baseSha, ct), await ReadAsync(mainSha, ct),
                await ReadAsync(incomingSha, ct), "preview");
        }
        catch (Exception ex)
        {
            log.LogInformation(ex, "Merge unavailable for {Base}/{Main}/{Incoming}", baseSha, mainSha, incomingSha);
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
