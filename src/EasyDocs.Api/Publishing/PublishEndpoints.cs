using EasyDocs.Api.Api;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Publishing;

public static class PublishEndpoints
{
    public record PublishRequest(string? Kind, string? Name);

    public static void MapPublishEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").WithTags("Publishing");
        g.MapPost("/api/v1/versions/{vid:guid}/publish", Publish).RequireAuthorization();
        g.MapGet("/api/v1/documents/{id:guid}/publications", ListPublications).RequireAuthorization();
    }

    // Publish a selected version as minor/major (R3/R4, E6). Editor+ on the version's document.
    private static async Task<IResult> Publish(Guid vid, PublishRequest req, HttpContext ctx, EasyDocsDbContext db, PublishService svc)
    {
        var kind = req.Kind?.ToLowerInvariant();
        if (kind is not ("minor" or "major"))
            return Problem.Of(400, "Invalid request", "kind must be 'minor' or 'major'.");

        var version = await db.Versions.FirstOrDefaultAsync(v => v.Id == vid);
        if (version is null) return Problem.Of(404, "Not found", "Version not found.");

        var (failure, actor) = await AuthorizeAsync(db, ctx, version.DocumentId, requireEdit: true);
        if (failure is not null) return failure;

        var result = await svc.PublishAsync(version.DocumentId, vid, kind, req.Name?.Trim(), actor, ctx.RequestAborted);
        return Results.Ok(result);
    }

    // The "Major Versions" list (E6): published versions only, newest first. Viewer+.
    // Cursor-paginated on (CreatedAt, Id) descending — newest-created first (matches publish order in practice).
    private static async Task<IResult> ListPublications(Guid id, HttpContext ctx, EasyDocsDbContext db, string? cursor, int? limit)
    {
        var (failure, _) = await AuthorizeAsync(db, ctx, id, requireEdit: false);
        if (failure is not null) return failure;

        var page = await Pagination.PageAsync(
            db.Versions.Where(v => v.DocumentId == id && v.PublishedKind != null),
            cursor, limit, descending: true, ctx.RequestAborted);
        return Results.Ok(new
        {
            items = page.Items.Select(v => new
            {
                versionId = v.Id, major = v.Major, minor = v.Minor, revision = v.Revision,
                name = v.PublishName, publishedBy = v.PublishedBy, publishedAt = v.PublishedAt, kind = v.PublishedKind,
            }),
            nextCursor = page.NextCursor,
        });
    }

    // Mirrors DocumentEndpoints.AuthorizeAsync: no org-role fallback; 404/403 mapping; returns the actor id.
    private static async Task<(IResult? Failure, Guid Actor)> AuthorizeAsync(
        EasyDocsDbContext db, HttpContext ctx, Guid documentId, bool requireEdit)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);
        var (result, role) = await DocumentAuthorization.ResolveAsync(db, orgId, userId, documentId);
        switch (result)
        {
            case AccessResult.NotFound:
                return (Problem.Of(404, "Not found", "Document not found."), userId);
            case AccessResult.Forbidden:
                return (Problem.Of(403, "Forbidden", "You do not have access to this document."), userId);
        }
        if (requireEdit && !DocumentAuthorization.CanEdit(role!.Value))
            return (Problem.Of(403, "Forbidden", "Editor role required."), userId);
        return (null, userId);
    }
}
