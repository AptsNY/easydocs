using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Events;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Copies;

// Push back (spec §8, §10.1, E9): a member of a copy sends one of the copy's versions to the master,
// where the master's members Accept/Reject it.
//
// THE ONE SANCTIONED CHOKEPOINT BYPASS (spec §8, §11). Every other mutation authorizes on the document
// it mutates. A push authorizes on the SOURCE COPY and deliberately does not require any role on the
// target — that is what lets an external reviewer, who is a member of the copy only, return their
// redline. Two things keep the bypass from becoming a privilege escalation:
//
//   1. The target must be exactly the document the copy was forked from (ParentDocumentId). Without that
//      check, membership of ANY copy would grant a write into ANY document in the org.
//   2. A push never lands on main. It creates an incoming_push branch, and unless the pusher independently
//      holds an editing role on the target it stays `pending` until a target Editor+ accepts it.
public static class PushEndpoints
{
    public record PushBody(Guid? TargetDocumentId, Guid VersionId);

    public static void MapPushEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").RequireAuthorization().WithTags("Copies");
        g.MapPost("/api/v1/documents/{id:guid}/pushes", Push);
        g.MapGet("/api/v1/documents/{id:guid}/push-requests", ListRequests);
        g.MapPost("/api/v1/push-requests/{id:guid}:accept", Accept);
        g.MapPost("/api/v1/push-requests/{id:guid}:reject", Reject);
    }

    private static async Task<IResult> Push(
        Guid id, PushBody req, HttpContext ctx, EasyDocsDbContext db, PushService pushes, EventBus bus)
    {
        var ct = ctx.RequestAborted;
        var userId = CurrentUser.UserId(ctx.User);
        var orgId = CurrentUser.OrgId(ctx.User);

        // Authorized on the source copy — the documented bypass. Editor+ because pushing publishes the
        // copy's content into another document; a Viewer on the copy may read it and nothing more.
        var (copy, _, failure) = await DocumentAuthorization.AuthorizeAsync(db, ctx, id, Need.Edit, ct: ct);
        if (failure is not null) return failure;

        if (copy!.ParentDocumentId is not { } forkedFrom)
            return Problem.Of(400, "Not a copy", "Only a copy created by Push To Copy can push back.");

        // Guard 1: the bypass is scoped to this copy's own master, never an arbitrary document.
        var targetId = req.TargetDocumentId ?? forkedFrom;
        if (targetId != forkedFrom)
            return Problem.Of(400, "Invalid target",
                "A copy may only push back to the document it was forked from.");

        var source = await db.Versions.FirstOrDefaultAsync(v => v.Id == req.VersionId && v.DocumentId == id, ct);
        if (source is null)
            return Problem.Of(404, "Not found", "versionId must reference a version of this copy.");

        if (await pushes.IsNoOpAsync(targetId, source.BlobSha256, ct))
            return Problem.Of(409, "Nothing to push",
                "This version's content already matches the target's current head.");

        // "Pusher also holds a target role" (spec §8) read as an *editing* role: materializing writes to
        // the target's history, and :accept requires Editor+, so a mere Viewer on the target cannot let
        // content in by pushing it either — their push goes to review like anyone else's.
        var (targetAccess, targetRole) = await DocumentAuthorization.ResolveAsync(db, orgId, userId, targetId, ct);
        var auto = targetAccess == AccessResult.Ok && DocumentAuthorization.CanEdit(targetRole!.Value);

        var now = DateTimeOffset.UtcNow;
        var pr = new PushRequest
        {
            Id = Guid.NewGuid(),
            CopyDocumentId = id,
            TargetDocumentId = targetId,
            SourceVersionId = source.Id,
            PushedBy = userId,
            Status = auto ? "auto_accepted" : "pending",
            DecidedAt = auto ? now : null,
            CreatedAt = now,
        };
        db.Add(pr);
        AuditBoth(db, orgId, pr, userId, "push.requested",
            new { status = pr.Status, sourceVersionId = source.Id });
        await db.SaveChangesAsync(ct);

        if (auto && await pushes.MaterializeAsync(pr, ct) is null)
            return Problem.Of(409, "Nothing to push",
                "This version's content already matches the target's current head.");
        if (auto) await db.SaveChangesAsync(ct); // persist MaterializedVersionId

        // To the TARGET's consoles: its members are the ones who may need to review this (spec §10.2).
        bus.Publish(targetId, "push.requested",
            new { pushRequestId = pr.Id, copyDocumentId = id, status = pr.Status });

        return Results.Created($"/api/v1/push-requests/{pr.Id}", Dto(pr));
    }

    // Push requests touching this document: inbound (to review) when {id} is the target, outbound (to
    // follow) when {id} is the copy. One route serves both because the pusher may hold no target role and
    // would otherwise have no way to learn the decision (§10.1 defines a single push-requests route).
    private static async Task<IResult> ListRequests(
        Guid id, string? status, HttpContext ctx, EasyDocsDbContext db)
    {
        var (_, _, failure) = await DocumentAuthorization.AuthorizeAsync(db, ctx, id, Need.Read, ct: ctx.RequestAborted);
        if (failure is not null) return failure;

        var q = db.PushRequests.Where(p => p.TargetDocumentId == id || p.CopyDocumentId == id);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(p => p.Status == status);

        var rows = await q.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).ToListAsync(ctx.RequestAborted);
        return Results.Ok(rows.Select(Dto));
    }

    private static Task<IResult> Accept(Guid id, HttpContext ctx, EasyDocsDbContext db, PushService pushes, EventBus bus) =>
        DecideAsync(id, accept: true, ctx, db, pushes, bus);

    private static Task<IResult> Reject(Guid id, HttpContext ctx, EasyDocsDbContext db, PushService pushes, EventBus bus) =>
        DecideAsync(id, accept: false, ctx, db, pushes, bus);

    // Accept/reject: Editor+ on the TARGET (this is the target's own decision about its own history).
    private static async Task<IResult> DecideAsync(
        Guid id, bool accept, HttpContext ctx, EasyDocsDbContext db, PushService pushes, EventBus bus)
    {
        var ct = ctx.RequestAborted;
        var pr = await db.PushRequests.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pr is null) return Problem.Of(404, "Not found", "Push request not found.");

        var (target, _, failure) = await DocumentAuthorization.AuthorizeAsync(
            db, ctx, pr.TargetDocumentId, Need.Edit, ct: ct);
        if (failure is not null) return failure;

        // Decisions are immutable, like approvals (§12.1 E7): a decided push is settled.
        if (pr.Status != "pending")
            return Problem.Of(409, "Already decided", $"This push request is already {pr.Status}.");

        var userId = CurrentUser.UserId(ctx.User);
        if (accept && await pushes.MaterializeAsync(pr, ct) is null)
            return Problem.Of(409, "Nothing to push",
                "This version's content already matches the target's current head.");

        pr.Status = accept ? "accepted" : "rejected";
        pr.DecidedAt = DateTimeOffset.UtcNow;
        AuditBoth(db, target!.OrgId, pr, userId, accept ? "push.accepted" : "push.rejected",
            new { materializedVersionId = pr.MaterializedVersionId });
        await db.SaveChangesAsync(ct);

        // The pusher is notified on the COPY, not the target: they may hold no target role, so a target
        // event would never reach them. Rejected content is simply never in the target's history — there is
        // nothing to hide after the fact.
        bus.Publish(pr.CopyDocumentId, "push.reviewed",
            new { pushRequestId = pr.Id, status = pr.Status, materializedVersionId = pr.MaterializedVersionId });

        return Results.Ok(Dto(pr));
    }

    // Both documents are audited: the target's owners need the trail of what entered their history, and
    // the copy's need the trail of what was sent and how it was decided (spec §11 — mutations are audited).
    private static void AuditBoth(
        EasyDocsDbContext db, Guid orgId, PushRequest pr, Guid actorUserId, string action, object metadata)
    {
        db.Add(Audit.Event(orgId, pr.TargetDocumentId, actorUserId, action, "push_request", pr.Id.ToString(), metadata));
        db.Add(Audit.Event(orgId, pr.CopyDocumentId, actorUserId, action, "push_request", pr.Id.ToString(), metadata));
    }

    private static object Dto(PushRequest pr) => new
    {
        id = pr.Id,
        status = pr.Status,
        copyDocumentId = pr.CopyDocumentId,
        targetDocumentId = pr.TargetDocumentId,
        sourceVersionId = pr.SourceVersionId,
        materializedVersionId = pr.MaterializedVersionId,
        pushedBy = pr.PushedBy,
        createdAt = pr.CreatedAt,
        decidedAt = pr.DecidedAt,
    };
}
