using EasyDocs.Api.Api;
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

    // One row of the approvals screen (spec §9): enough to render a line without a follow-up request
    // per row — document name and version number included, both display names resolved.
    public sealed record ApprovalRow(
        Guid Id, Guid VersionId, Guid DocumentId, string DocumentName, string VersionNumber,
        Guid ApproverId, string ApproverName, Guid RequestedBy, string RequestedByName,
        string? Decision, string? DecisionComment, DateTimeOffset? DueAt,
        DateTimeOffset? DecidedAt, DateTimeOffset? CancelledAt, string Status,
        DateTimeOffset CreatedAt);

    public static void MapApprovalEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").WithTags("Approvals");
        g.MapPost("/api/v1/versions/{vid:guid}/approvals", Request).RequireAuthorization();
        g.MapPost("/api/v1/approvals/{id:guid}:respond", Respond).RequireAuthorization();
        g.MapPost("/api/v1/approvals/{id:guid}:cancel", Cancel).RequireAuthorization();
        g.MapGet("/api/v1/approvals", ListForCaller).RequireAuthorization();
        g.MapGet("/api/v1/versions/{vid:guid}/approvals", ListForVersion).RequireAuthorization();
    }

    // The approvals inbox (spec §9): `filter=assigned` (default) is "asked of me", `requested` is
    // "asked by me"; `status=open|closed` narrows it. Scoped to documents the caller is STILL a member
    // of — an approval must never leak a document name to someone who has lost access (spec §11).
    private static async Task<IResult> ListForCaller(
        HttpContext ctx, EasyDocsDbContext db, string? filter, string? status, string? cursor, int? limit, string? order)
    {
        if (filter is not (null or "assigned" or "requested"))
            return Problem.Of(400, "Invalid request", "filter must be 'assigned' or 'requested'.");
        if (status is not (null or "open" or "closed"))
            return Problem.Of(400, "Invalid request", "status must be 'open' or 'closed'.");

        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);

        var query = db.ApprovalRequests.Where(a => db.Versions.Any(v => v.Id == a.VersionId
            && db.Documents.Any(d => d.Id == v.DocumentId && d.OrgId == orgId && d.DeletedAt == null
                && db.DocumentMembers.Any(m => m.DocumentId == d.Id && m.UserId == userId))));

        query = filter == "requested"
            ? query.Where(a => a.RequestedBy == userId)
            : query.Where(a => a.ApproverId == userId);

        // "open" == no decision and not cancelled; the UI's default worklist.
        if (status == "open") query = query.Where(a => a.Decision == null && a.CancelledAt == null);
        else if (status == "closed") query = query.Where(a => a.Decision != null || a.CancelledAt != null);

        var page = await Pagination.PageAsync(query, cursor, limit, Pagination.Descending(order), ctx.RequestAborted);
        return Results.Ok(new
        {
            items = await RowsAsync(db, page.Items, ctx.RequestAborted),
            nextCursor = page.NextCursor,
        });
    }

    // The approvals panel on one version. Read-only, so Viewer suffices; goes through the same
    // chokepoint as every other version route so cross-org collapses to 404, not 403.
    private static async Task<IResult> ListForVersion(Guid vid, HttpContext ctx, EasyDocsDbContext db)
    {
        var version = await db.Versions.FirstOrDefaultAsync(v => v.Id == vid, ctx.RequestAborted);
        if (version is null) return Problem.Of(404, "Not found", "Version not found.");

        var (failure, _) = await AuthorizeAsync(db, ctx, version.DocumentId, requireEdit: false);
        if (failure is not null) return failure;

        var rows = await db.ApprovalRequests
            .Where(a => a.VersionId == vid)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .ToListAsync(ctx.RequestAborted);
        return Results.Ok(await RowsAsync(db, rows, ctx.RequestAborted));
    }

    // Page-then-lookup, same discipline as VersionListProjection: a fixed number of queries per page,
    // never one per row.
    private static async Task<List<ApprovalRow>> RowsAsync(
        EasyDocsDbContext db, IReadOnlyList<ApprovalRequest> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var versionIds = rows.Select(a => a.VersionId).Distinct().ToArray();
        var meta = await (from v in db.Versions
                          join d in db.Documents on v.DocumentId equals d.Id
                          where versionIds.Contains(v.Id)
                          select new { v.Id, v.DocumentId, DocumentName = d.Name, v.Major, v.Minor, v.Revision })
            .ToDictionaryAsync(x => x.Id, ct);

        var names = await AuthorNames.ForAsync(
            db, rows.SelectMany(a => new[] { a.ApproverId, a.RequestedBy }), ct);

        return rows.Select(a =>
        {
            var m = meta[a.VersionId];
            return new ApprovalRow(
                a.Id, a.VersionId, m.DocumentId, m.DocumentName, $"{m.Major}.{m.Minor}.{m.Revision}",
                a.ApproverId, names.GetValueOrDefault(a.ApproverId, AuthorNames.Unknown),
                a.RequestedBy, names.GetValueOrDefault(a.RequestedBy, AuthorNames.Unknown),
                a.Decision, a.DecisionComment, a.DueAt, a.DecidedAt, a.CancelledAt,
                // Derived, not stored: cancel wins over a decision that can never arrive.
                a.CancelledAt is not null ? "cancelled" : a.Decision ?? "open",
                a.CreatedAt);
        }).ToList();
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

        // Every approver must already be a member of this document. Without this an approver gets a
        // decision right (Respond authorizes on ApproverId alone) over a version they cannot read —
        // an §11 "membership is strictly per-document" violation, and it makes the approval
        // undeliverable in the UI because the read endpoint scopes to readable documents.
        var memberIds = await db.DocumentMembers
            .Where(m => m.DocumentId == version.DocumentId)
            .Select(m => m.UserId)
            .ToListAsync(ctx.RequestAborted);
        if (ids.Except(memberIds).Any())
            return Problem.Of(400, "Invalid request", "Every approverId must be a member of this document.");

        var now = DateTimeOffset.UtcNow;
        var rows = ids.Select(a => new ApprovalRequest
        {
            Id = Guid.NewGuid(), VersionId = vid, ApproverId = a,
            RequestedBy = actor, DueAt = req.DueAt, CreatedAt = now,
        }).ToList();
        db.ApprovalRequests.AddRange(rows);
        foreach (var r in rows)
            db.Add(Audit.Event(CurrentUser.OrgId(ctx.User), version.DocumentId, actor, "approval.requested",
                "approval", r.Id.ToString(), new { versionId = vid, approverId = r.ApproverId, dueAt = r.DueAt }));
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

        var documentId = await db.Versions.Where(v => v.Id == ar.VersionId).Select(v => v.DocumentId).FirstAsync(ctx.RequestAborted);

        // Defence in depth, and it runs before the 409 so a stranger learns nothing about the row's
        // state: being named as approver is not by itself access. Request now refuses non-members, but
        // any row written by the vulnerable build — or by a future code path — would otherwise still
        // hand a decision right over a document the caller cannot read (spec §11, E12).
        var (denied, _) = await AuthorizeAsync(db, ctx, documentId, requireEdit: false);
        if (denied is not null) return denied;

        if (ar.DecidedAt is not null || ar.CancelledAt is not null)
            return Problem.Of(409, "Already closed", "This approval request has already been decided or cancelled.");

        ar.Decision = decision;
        ar.DecisionComment = req.Comment;
        ar.DecidedAt = DateTimeOffset.UtcNow;

        db.Add(Audit.Event(CurrentUser.OrgId(ctx.User), documentId, CurrentUser.UserId(ctx.User), "approval.responded",
            "approval", ar.Id.ToString(), new { versionId = ar.VersionId, decision = ar.Decision }));
        await db.SaveChangesAsync(ctx.RequestAborted);
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
        db.Add(Audit.Event(CurrentUser.OrgId(ctx.User), documentId, CurrentUser.UserId(ctx.User), "approval.cancelled",
            "approval", ar.Id.ToString(), new { versionId = ar.VersionId }));
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
