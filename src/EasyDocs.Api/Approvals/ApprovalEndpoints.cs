using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Events;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Approvals;

// Approvals (E7, spec §12.1): requested only on a published version, one ApprovalRequest row per
// approver, decision + comment recorded immutably, cancel closes the request. Single decision — no
// threaded conversation, no tasks table.
public static class ApprovalEndpoints
{
    public record RequestBody(Guid[]? ApproverIds, DateTimeOffset? DueAt);
    public record RespondBody(string? Decision, string? Comment);

    public static void MapApprovalEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/versions/{vid:guid}/approvals", Request).RequireAuthorization();
        app.MapPost("/api/v1/approvals/{id:guid}:respond", Respond).RequireAuthorization();
        app.MapPost("/api/v1/approvals/{id:guid}:cancel", Cancel).RequireAuthorization();
    }

    private static async Task<IResult> Request(Guid vid, RequestBody req, HttpContext ctx, EasyDocsDbContext db)
    {
        var version = await db.Versions.FirstOrDefaultAsync(v => v.Id == vid);
        if (version is null) return Problem.Of(404, "Not found", "Version not found.");

        var (failure, actor) = await AuthorizeAsync(db, ctx, version.DocumentId, requireEdit: true);
        if (failure is not null) return failure;

        if (version.PublishedKind is null)
            return Problem.Of(400, "Not published", "Approvals can only be requested on a published version.");

        var ids = (req.ApproverIds ?? Array.Empty<Guid>()).Distinct().ToArray();
        if (ids.Length == 0) return Problem.Of(400, "Invalid request", "approverIds is required.");

        var now = DateTimeOffset.UtcNow;
        var rows = ids.Select(a => new ApprovalRequest
        {
            Id = Guid.NewGuid(), VersionId = vid, ApproverId = a,
            RequestedBy = actor, DueAt = req.DueAt, CreatedAt = now,
        }).ToList();
        db.ApprovalRequests.AddRange(rows);
        await db.SaveChangesAsync(ctx.RequestAborted);

        return Results.Created($"/api/v1/versions/{vid}/approvals",
            rows.Select(r => new { id = r.Id, versionId = r.VersionId, approverId = r.ApproverId, dueAt = r.DueAt }));
    }

    private static async Task<IResult> Respond(Guid id, RespondBody req, HttpContext ctx, EasyDocsDbContext db, EventBus bus)
    {
        var decision = req.Decision?.ToLowerInvariant();
        if (decision is not ("approved" or "rejected"))
            return Problem.Of(400, "Invalid request", "decision must be 'approved' or 'rejected'.");

        var ar = await db.ApprovalRequests.FirstOrDefaultAsync(x => x.Id == id);
        if (ar is null) return Problem.Of(404, "Not found", "Approval request not found.");

        if (ar.ApproverId != CurrentUser.UserId(ctx.User))
            return Problem.Of(403, "Forbidden", "Only the named approver may respond.");

        if (ar.DecidedAt is not null || ar.CancelledAt is not null)
            return Problem.Of(409, "Already closed", "This approval request has already been decided or cancelled.");

        ar.Decision = decision;
        ar.DecisionComment = req.Comment;
        ar.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ctx.RequestAborted);

        var documentId = await db.Versions.Where(v => v.Id == ar.VersionId).Select(v => v.DocumentId).FirstAsync(ctx.RequestAborted);
        bus.Publish(documentId, "approval.responded",
            new { id = ar.Id, versionId = ar.VersionId, decision = ar.Decision, decidedAt = ar.DecidedAt });

        return Results.Ok(new { id = ar.Id, decision = ar.Decision, comment = ar.DecisionComment, decidedAt = ar.DecidedAt });
    }

    private static async Task<IResult> Cancel(Guid id, HttpContext ctx, EasyDocsDbContext db)
    {
        var ar = await db.ApprovalRequests.FirstOrDefaultAsync(x => x.Id == id);
        if (ar is null) return Problem.Of(404, "Not found", "Approval request not found.");

        var documentId = await db.Versions.Where(v => v.Id == ar.VersionId).Select(v => v.DocumentId).FirstAsync(ctx.RequestAborted);
        // Requester or a document editor may cancel.
        if (ar.RequestedBy != CurrentUser.UserId(ctx.User))
        {
            var (failure, _) = await AuthorizeAsync(db, ctx, documentId, requireEdit: true);
            if (failure is not null) return failure;
        }

        if (ar.DecidedAt is not null || ar.CancelledAt is not null)
            return Problem.Of(409, "Already closed", "This approval request has already been decided or cancelled.");

        ar.CancelledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ctx.RequestAborted);
        return Results.Ok(new { id = ar.Id, cancelledAt = ar.CancelledAt });
    }

    // Mirrors PublishEndpoints.AuthorizeAsync: no org-role fallback; 404/403 mapping; returns the actor id.
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
