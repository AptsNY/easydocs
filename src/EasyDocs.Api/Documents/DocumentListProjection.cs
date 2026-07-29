using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Documents;

// One dashboard tile (spec §9): current version number, when it last changed, who changed it. Built
// with a FIXED number of queries no matter the page size, same discipline as VersionListProjection —
// the dashboard is the single hottest read in the product.
public sealed record DocumentListRow(
    Guid Id, string Name, Guid? FolderId, string? CurrentNumber, int VersionCount,
    DateTimeOffset? UpdatedAt, string? LastAuthorName, DateTimeOffset? DeletedAt);

public static class DocumentListProjection
{
    public static async Task<List<DocumentListRow>> BuildAsync(
        EasyDocsDbContext db, IReadOnlyList<Document> docs, CancellationToken ct)
    {
        if (docs.Count == 0) return [];

        var docIds = docs.Select(d => d.Id).ToArray();

        // Per-document aggregate in one grouped query: count, latest CreatedAt (-> UpdatedAt), and the
        // head version's own Id (latest by CreatedAt) to fetch its Major/Minor/Revision/CreatedBy without
        // a second per-document round trip. Latest-by-CreatedAt, not max(SeqInBranch): SeqInBranch is
        // scoped to a single branch, so a document with a push/fork branch would need a branch-aware
        // "head" to use it; CreatedAt is document-wide and matches what "last changed" means on a tile.
        var heads = await db.Versions
            .Where(v => docIds.Contains(v.DocumentId))
            .GroupBy(v => v.DocumentId)
            .Select(g => new
            {
                DocumentId = g.Key,
                VersionCount = g.Count(),
                Head = g.OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id).First(),
            })
            .ToDictionaryAsync(x => x.DocumentId, ct);

        var authors = await AuthorNames.ForAsync(db, heads.Values.Select(h => h.Head.CreatedBy), ct);

        return docs.Select(d =>
        {
            heads.TryGetValue(d.Id, out var h);
            var number = h is null ? null : $"{h.Head.Major}.{h.Head.Minor}.{h.Head.Revision}";
            var authorName = h is null ? null : authors.GetValueOrDefault(h.Head.CreatedBy, AuthorNames.Unknown);
            return new DocumentListRow(
                d.Id, d.Name, d.FolderId, number, h?.VersionCount ?? 0,
                h?.Head.CreatedAt, authorName, d.DeletedAt);
        }).ToList();
    }
}
