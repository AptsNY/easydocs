using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Editing;

public static class EditingEndpoints
{
    public static void MapEditingEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1").RequireAuthorization().WithTags("Editing");
        g.MapPost("/versions/{vid:guid}/sessions", MintSession);
        g.MapDelete("/sessions/{sid:guid}", CloseSession);
    }

    // Mint an edit session: hands Collabora file_id = session_id + a short-TTL WOPI token (spec §6, §6.1).
    private static async Task<IResult> MintSession(Guid vid, HttpContext ctx, EasyDocsDbContext db,
        WopiAccessToken wopiToken, CollaboraDiscovery discovery, IConfiguration cfg)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);

        var version = await db.Versions.FirstOrDefaultAsync(v => v.Id == vid, ctx.RequestAborted);
        if (version is null) return Problem.Of(404, "Not found", "Version not found.");

        var (result, role) = await DocumentAuthorization.ResolveAsync(db, orgId, userId, version.DocumentId, ctx.RequestAborted);
        if (result != AccessResult.Ok) // NotFound (cross-org/missing) collapses to 404 to avoid an existence leak.
            return result == AccessResult.NotFound
                ? Problem.Of(404, "Not found", "Version not found.")
                : Problem.Of(403, "Forbidden", "You do not have access to this document.");
        if (!DocumentAuthorization.CanEdit(role!.Value))
            return Problem.Of(403, "Forbidden", "Editor role required.");

        var session = new EditSession
        {
            Id = Guid.NewGuid(),
            DocumentId = version.DocumentId,
            BaseVersionId = vid,
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            LastCommittedSha = null,
        };
        db.Add(session);
        db.Add(Audit.Event(orgId, version.DocumentId, userId, "edit_session.opened",
            "session", session.Id.ToString(), new { baseVersionId = vid }));
        await db.SaveChangesAsync(ctx.RequestAborted);

        var actionUrl = await discovery.ActionUrlForDocxAsync(ctx.RequestAborted);
        var token = wopiToken.Issue(session.Id, userId, "w");
        var wopiHost = cfg["WOPI_HOST_URL"] ?? throw new InvalidOperationException("WOPI_HOST_URL not configured");
        // Unescaped WOPISrc per WOPI convention (Collabora appends it as a query arg it re-parses).
        var editorUrl = $"{actionUrl}WOPISrc={wopiHost}/wopi/files/{session.Id}&access_token={token}";

        return Results.Created($"/api/v1/sessions/{session.Id}", new
        {
            sessionId = session.Id,
            editorUrl,
            accessToken = token,
            accessTokenTtlSeconds = WopiAccessToken.TtlSeconds,
        });
    }

    // Close a session (owner only). 404 (not 403) for a non-owner avoids leaking session existence.
    private static async Task<IResult> CloseSession(Guid sid, HttpContext ctx, EasyDocsDbContext db)
    {
        var userId = CurrentUser.UserId(ctx.User);
        var session = await db.EditSessions.FirstOrDefaultAsync(s => s.Id == sid, ctx.RequestAborted);
        if (session is null || session.UserId != userId)
            return Problem.Of(404, "Not found", "Session not found.");
        session.ClosedAt = DateTimeOffset.UtcNow;
        db.Add(Audit.Event(CurrentUser.OrgId(ctx.User), session.DocumentId, userId, "edit_session.closed",
            "session", sid.ToString(), null));
        await db.SaveChangesAsync(ctx.RequestAborted);
        return Results.NoContent();
    }
}
