using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Auth;

public enum AccessResult { Ok, NotFound, Forbidden }

// What an endpoint needs from the caller's document role. Named rather than ordered so no caller
// has to know that DocRole's int order (Owner, Editor, Viewer) runs *descending* in privilege.
public enum Need { Read, Edit, Own }

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

    public static bool Satisfies(DocRole role, Need need) => need switch
    {
        Need.Read => true,
        Need.Edit => CanEdit(role),
        Need.Own => role is DocRole.Owner,
        _ => false,
    };

    // The chokepoint as an endpoint filter: resolve the caller's role and map failure to RFC-7807.
    // Cross-org/missing -> 404 (no existence leak), same-org non-member -> 403, under-privileged -> 403.
    // `includeDeleted` is for the restore path, which by definition targets a trashed document.
    public static async Task<(Document? Doc, DocRole? Role, IResult? Failure)> AuthorizeAsync(
        EasyDocsDbContext db, HttpContext ctx, Guid documentId, Need need,
        bool includeDeleted = false, CancellationToken ct = default)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);

        var doc = await db.Documents.FirstOrDefaultAsync(
            d => d.Id == documentId && (includeDeleted || d.DeletedAt == null), ct);
        if (doc is null || doc.OrgId != orgId)
            return (null, null, Problem.Of(404, "Not found", "Document not found."));

        var m = await db.DocumentMembers.FirstOrDefaultAsync(x => x.DocumentId == documentId && x.UserId == userId, ct);
        if (m is null)
            return (null, null, Problem.Of(403, "Forbidden", "You do not have access to this document."));

        if (!Satisfies(m.Role, need))
            return (null, null, Problem.Of(403, "Forbidden",
                need == Need.Own ? "Owner role required." : "Editor role required."));

        return (doc, m.Role, null);
    }
}
