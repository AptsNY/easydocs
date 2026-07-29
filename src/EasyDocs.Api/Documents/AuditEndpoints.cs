using EasyDocs.Api.Api;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Documents;

// GET /api/v1/documents/{id}/audit (spec §10.1 Audit, §11). The per-document slice of the append-only
// trail, newest first, cursor-paginated like the other list endpoints. Any member may read it — an
// audit trail only the owner can see does not answer "what happened to my document".
public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/documents").RequireAuthorization().WithTags("Audit");
        g.MapGet("/{id:guid}/audit", List);
    }

    private static async Task<IResult> List(Guid id, HttpContext ctx, EasyDocsDbContext db, string? cursor, int? limit)
    {
        var (_, _, failure) = await DocumentAuthorization.AuthorizeAsync(db, ctx, id, Need.Read, ct: ctx.RequestAborted);
        if (failure is not null) return failure;

        var page = await Pagination.PageAsync(
            db.AuditEvents.Where(e => e.DocumentId == id), cursor, limit, descending: true, ctx.RequestAborted);

        // A null actor (anonymous public share-link read, spec §11) must stay null — it is not
        // "unknown", it is genuinely nobody, so it never enters the lookup and never gets a placeholder.
        var actorIds = page.Items.Where(e => e.ActorUserId is not null).Select(e => e.ActorUserId!.Value);
        var authors = await AuthorNames.ForAsync(db, actorIds, ctx.RequestAborted);

        return Results.Ok(new
        {
            items = page.Items.Select(e => new
            {
                id = e.Id,
                action = e.Action,
                actorUserId = e.ActorUserId,
                actorName = e.ActorUserId is { } uid ? authors.GetValueOrDefault(uid, AuthorNames.Unknown) : null,
                targetType = e.TargetType,
                targetId = e.TargetId,
                metadata = e.Metadata,
                createdAt = e.CreatedAt,
            }),
            nextCursor = page.NextCursor,
        });
    }
}
