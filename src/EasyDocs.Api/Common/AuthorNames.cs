using EasyDocs.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Common;

// Resolves user ids to display names for read paths (console history, audit trail). One query for a
// whole page — never one per row. A name that cannot be resolved degrades to a placeholder rather
// than failing the read; unlike a missing branch row, an unresolvable name is not a data-integrity
// violation worth a 500.
public static class AuthorNames
{
    public const string Unknown = "(unknown)";

    public static async Task<Dictionary<Guid, string>> ForAsync(
        EasyDocsDbContext db, IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToArray();
        if (ids.Length == 0) return [];

        return await db.Users
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }
}
