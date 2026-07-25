using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Auth;

public enum AccessResult { Ok, NotFound, Forbidden }

// Single resolve_role chokepoint over document_members (spec §10/§11).
// No org-role fallback: org membership grants NO implicit document access.
//   cross-org or missing doc -> NotFound (no existence leak)
//   same org, not a member   -> Forbidden
//   member                   -> Ok + role
public static class DocumentAuthorization
{
    public static async Task<(AccessResult Result, DocRole? Role)> ResolveAsync(
        EasyDocsDbContext db, Guid orgId, Guid userId, Guid documentId, CancellationToken ct = default)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.DeletedAt == null, ct);
        if (doc is null || doc.OrgId != orgId) return (AccessResult.NotFound, null);
        var m = await db.DocumentMembers.FirstOrDefaultAsync(x => x.DocumentId == documentId && x.UserId == userId, ct);
        if (m is null) return (AccessResult.Forbidden, null);
        return (AccessResult.Ok, m.Role);
    }

    // Explicit privilege checks — do NOT rely on DocRole int ordering (Owner,Editor,Viewer is descending privilege).
    public static bool CanEdit(DocRole role) => role is DocRole.Owner or DocRole.Editor;
}
