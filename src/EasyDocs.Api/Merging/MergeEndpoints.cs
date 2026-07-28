using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;

namespace EasyDocs.Api.Merging;

// POST /api/v1/documents/{id}/merges {left,right} — merge two concurrent branch heads into one
// tracked-changes docx on main (spec §5.3, E4). Editor+ only. A comparison failure is a 409
// "merge unavailable" (download-and-merge-manually), never a 500.
public static class MergeEndpoints
{
    public record MergeRequest(Guid Left, Guid Right);

    public static void MapMergeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/documents/{id:guid}/merges", Merge).RequireAuthorization().WithTags("Merging");
    }

    private static async Task<IResult> Merge(Guid id, MergeRequest req, HttpContext ctx, EasyDocsDbContext db, WmlComparerMergeService merge)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);
        var (result, role) = await DocumentAuthorization.ResolveAsync(db, orgId, userId, id, ctx.RequestAborted);
        if (result == AccessResult.NotFound) return Problem.Of(404, "Not found", "Document not found.");
        if (result == AccessResult.Forbidden) return Problem.Of(403, "Forbidden", "You do not have access to this document.");
        if (!DocumentAuthorization.CanEdit(role!.Value)) return Problem.Of(403, "Forbidden", "Editor role required.");

        var m = await merge.MergeAsync(id, req.Left, req.Right, userId, ctx.RequestAborted);
        if (!m.Available)
            return Problem.Of(409, "Merge unavailable", "Comparison failed — download both versions and merge manually.");

        db.Add(Audit.Event(orgId, id, userId, "merge.completed", "version", m.MergeVersionId.ToString(),
            new { left = req.Left, right = req.Right }));
        await db.SaveChangesAsync(ctx.RequestAborted);

        return Results.Created($"/api/v1/documents/{id}/versions/{m.MergeVersionId}",
            new { mergeVersionId = m.MergeVersionId });
    }
}
