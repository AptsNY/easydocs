using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Versioning;

// One console row (spec §9). Built with a FIXED number of queries no matter the page size: the
// dashboard and console are the two hottest reads in the product, and a per-row query here would be
// an N+1 the moment a document has real history.
public sealed record ChangeSummary(int Insertions, int Deletions, int Moves, int FormatChanges);

public sealed record VersionListRow(
    Guid Id, int Major, int Minor, int Revision, string Number, string? Name, string Source,
    string? PublishedKind, DateTimeOffset? PublishedAt, string? PublishName, bool HasPdf,
    Guid? ParentVersionId, Guid BranchId, string BranchKind, int BranchOrdinal,
    Guid? BranchMergedIntoVersionId, Guid CreatedBy, string CreatedByName,
    DateTimeOffset CreatedAt, ChangeSummary? Summary);

public static class VersionListProjection
{
    public static async Task<List<VersionListRow>> BuildAsync(
        EasyDocsDbContext db, IReadOnlyList<DocumentVersion> versions, CancellationToken ct)
    {
        if (versions.Count == 0) return [];

        var branchIds = versions.Select(v => v.BranchId).Distinct().ToArray();
        var branches = await db.Branches
            .Where(b => branchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, ct);

        var authors = await AuthorNames.ForAsync(db, versions.Select(v => v.CreatedBy), ct);

        var summaries = await SummariesAsync(db, versions, ct);

        return versions.Select(v =>
        {
            var b = branches[v.BranchId];
            return new VersionListRow(
                v.Id, v.Major, v.Minor, v.Revision, $"{v.Major}.{v.Minor}.{v.Revision}",
                v.Name, v.Source.ToString(), v.PublishedKind, v.PublishedAt, v.PublishName,
                v.PdfBlobSha256 is not null, v.ParentVersionId,
                b.Id, b.Kind.ToString(), b.Ordinal, b.MergedIntoVersionId,
                // Defensive fallback on a read path: an unresolvable display name should degrade
                // gracefully, not 500 a history read, unlike a missing branch row above which is a bug.
                v.CreatedBy, authors.GetValueOrDefault(v.CreatedBy, AuthorNames.Unknown),
                v.CreatedAt, summaries.GetValueOrDefault(v.Id));
        }).ToList();
    }

    // Per-row summary = the cached parent->child diff. Null when DiffSummaryWorker has not drained the
    // job yet; the console shows a dash and refreshes on the `diff.ready` SSE event.
    private static async Task<Dictionary<Guid, ChangeSummary>> SummariesAsync(
        EasyDocsDbContext db, IReadOnlyList<DocumentVersion> versions, CancellationToken ct)
    {
        var parentIds = versions
            .Where(v => v.ParentVersionId is not null)
            .Select(v => v.ParentVersionId!.Value)
            .Distinct()
            .ToArray();
        if (parentIds.Length == 0) return [];

        var parentSha = await db.Versions
            .Where(v => parentIds.Contains(v.Id))
            .Select(v => new { v.Id, v.BlobSha256 })
            .ToDictionaryAsync(v => v.Id, v => v.BlobSha256, ct);

        var wanted = versions
            .Where(v => v.ParentVersionId is { } p && parentSha.ContainsKey(p))
            .Select(v => (VersionId: v.Id, From: parentSha[v.ParentVersionId!.Value], To: v.BlobSha256))
            .ToList();
        if (wanted.Count == 0) return [];

        // Fetches a superset (the two IN lists cross-multiply) and is then indexed by the exact tuple.
        var froms = wanted.Select(w => w.From).Distinct().ToArray();
        var tos = wanted.Select(w => w.To).Distinct().ToArray();
        var rows = await db.VersionDiffs
            .Where(d => froms.Contains(d.FromSha256) && tos.Contains(d.ToSha256) && d.Insertions != null)
            .Select(d => new { d.FromSha256, d.ToSha256, d.Insertions, d.Deletions, d.Moves, d.FormatChanges })
            .ToListAsync(ct);
        var byPair = rows.ToDictionary(d => (d.FromSha256, d.ToSha256));

        var result = new Dictionary<Guid, ChangeSummary>();
        foreach (var w in wanted)
            if (byPair.TryGetValue((w.From, w.To), out var d))
                result[w.VersionId] = new ChangeSummary(
                    d.Insertions!.Value, d.Deletions ?? 0, d.Moves ?? 0, d.FormatChanges ?? 0);
        return result;
    }
}
