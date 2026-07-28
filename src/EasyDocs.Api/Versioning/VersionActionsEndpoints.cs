using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Events;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Versioning;

// Name-this-version + revert-to-version (E11). Both are Editor+ on the version's document.
public static class VersionActionsEndpoints
{
    public record NameRequest(string? Name);

    public static void MapVersionActionEndpoints(this WebApplication app)
    {
        app.MapPatch("/api/v1/versions/{vid:guid}", Name).RequireAuthorization();
        app.MapPost("/api/v1/versions/{vid:guid}/revert", Revert).RequireAuthorization();
    }

    // Label a version (metadata only). E11.
    private static async Task<IResult> Name(Guid vid, NameRequest req, HttpContext ctx, EasyDocsDbContext db, EventBus bus)
    {
        var version = await db.Versions.FirstOrDefaultAsync(v => v.Id == vid);
        if (version is null) return Problem.Of(404, "Not found", "Version not found.");

        var failure = await AuthorizeEditAsync(db, ctx, version.DocumentId);
        if (failure is not null) return failure;

        version.Name = req.Name?.Trim();
        await db.SaveChangesAsync(ctx.RequestAborted);

        bus.Publish(version.DocumentId, "version.named", new { versionId = vid, name = version.Name });
        return Results.Ok(new { versionId = vid, name = version.Name });
    }

    // Revert = commit the target version's existing blob as a new main head (content-addressed, zero
    // re-upload) via the single write path. History is untouched — all prior versions remain (E11).
    private static async Task<IResult> Revert(Guid vid, HttpContext ctx, EasyDocsDbContext db, VersioningService versioning, EventBus bus)
    {
        var target = await db.Versions.FirstOrDefaultAsync(v => v.Id == vid);
        if (target is null) return Problem.Of(404, "Not found", "Version not found.");

        var actorId = CurrentUser.UserId(ctx.User);
        var failure = await AuthorizeEditAsync(db, ctx, target.DocumentId);
        if (failure is not null) return failure;

        var size = await db.Blobs.Where(b => b.Sha256 == target.BlobSha256).Select(b => b.SizeBytes).FirstAsync(ctx.RequestAborted);

        var result = await versioning.CommitSaveAsync(
            new CommitInput(target.DocumentId, target.BlobSha256, size, VersionSource.Revert, actorId), ctx.RequestAborted);

        bus.Publish(target.DocumentId, "version.reverted", new { fromVersionId = vid, newVersionId = result.VersionId });
        return Results.Created($"/api/v1/documents/{target.DocumentId}/versions/{result.VersionId}",
            new { versionId = result.VersionId, major = result.Major, minor = result.Minor, revision = result.Revision });
    }

    // Mirrors DocumentEndpoints.AuthorizeAsync: no org-role fallback; 404/403 mapping; Editor required.
    private static async Task<IResult?> AuthorizeEditAsync(EasyDocsDbContext db, HttpContext ctx, Guid documentId)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);
        var (result, role) = await DocumentAuthorization.ResolveAsync(db, orgId, userId, documentId);
        return result switch
        {
            AccessResult.NotFound => Problem.Of(404, "Not found", "Document not found."),
            AccessResult.Forbidden => Problem.Of(403, "Forbidden", "You do not have access to this document."),
            _ when !DocumentAuthorization.CanEdit(role!.Value) => Problem.Of(403, "Forbidden", "Editor role required."),
            _ => null,
        };
    }
}
