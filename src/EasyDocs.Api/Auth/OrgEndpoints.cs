using System.Buffers.Text;
using System.Security.Cryptography;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Documents;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Auth;

// Org/member management (spec §10): org role grants no implicit document access - membership stays
// strictly per-document (see DocumentAuthorization / MemberEndpoints) - but until now nothing exposed
// org management itself: no member list, no way to rename the org, change a role, remove someone, or
// invite without a document. Spec §9's settings screen, and the SPA's person pickers, bind to this.
//
// This file must never reach into DocumentAuthorization or grant document access from an org role -
// that boundary is what E12's role matrix asserts.
public static class OrgEndpoints
{
    public record RenameRequest(string? Name);
    public record InviteRequest(string? Email, string? Role);
    public record UpdateRoleRequest(string? Role);

    public static void MapOrgEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/org").RequireAuthorization().WithTags("Org");
        g.MapGet("", GetOrg);
        g.MapPatch("", Rename);
        g.MapGet("/members", ListMembers);
        g.MapPost("/members", Invite);
        g.MapPatch("/members/{uid:guid}", UpdateRole);
        g.MapDelete("/members/{uid:guid}", Remove);
    }

    private static bool TryParseRole(string? value, out OrgRole role) =>
        Enum.TryParse(value, ignoreCase: true, out role) && Enum.IsDefined(role);

    // Every handler needs the caller's own membership row (for its role) before doing anything else -
    // one lookup, one place that turns "not a member of your own org" into a Problem, rather than
    // repeating the query and the 403/404 split six times.
    private static async Task<(Organization Org, OrgMember Caller, IResult? Failure)> LoadAsync(
        HttpContext ctx, EasyDocsDbContext db, CancellationToken ct)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);

        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == orgId, ct);
        var caller = await db.OrgMembers.FirstOrDefaultAsync(m => m.OrgId == orgId && m.UserId == userId, ct);
        if (org is null || caller is null)
            return (null!, null!, Problem.Of(404, "Not found", "Organization not found."));

        return (org, caller, null);
    }

    private static async Task<IResult> GetOrg(HttpContext ctx, EasyDocsDbContext db)
    {
        var (org, caller, failure) = await LoadAsync(ctx, db, ctx.RequestAborted);
        if (failure is not null) return failure;

        return Results.Ok(new { id = org.Id, name = org.Name, slug = org.Slug, myRole = caller.Role.ToString() });
    }

    // Owner or Admin: org/member management is theirs (spec §10); a plain Member may read but not rename.
    private static async Task<IResult> Rename(RenameRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        var (org, caller, failure) = await LoadAsync(ctx, db, ctx.RequestAborted);
        if (failure is not null) return failure;
        if (caller.Role is not (OrgRole.Owner or OrgRole.Admin))
            return Problem.Of(403, "Forbidden", "Only an org owner or admin may rename the org.");

        var name = req.Name?.Trim() ?? "";
        if (name.Length == 0) return Problem.Of(400, "Invalid request", "name is required.");

        // Slug is intentionally untouched: R8 download filenames bake it in
        // (Numbering.DownloadFileName(slug, ...), used by DocumentEndpoints.Download and
        // ShareEndpoints.PublicDownload) - re-slugging on rename would silently change every future
        // download filename.
        org.Name = name;
        db.Add(Audit.Event(org.Id, null, CurrentUser.UserId(ctx.User), "org.updated",
            "org", org.Id.ToString(), new { name }));
        await db.SaveChangesAsync(ctx.RequestAborted);

        return Results.Ok(new { id = org.Id, name = org.Name, slug = org.Slug, myRole = caller.Role.ToString() });
    }

    // Any org member may read the roster - the SPA's person pickers (approvers, document members) need
    // this, so it cannot be Owner/Admin-only. One join, no per-row query.
    private static async Task<IResult> ListMembers(HttpContext ctx, EasyDocsDbContext db)
    {
        var (org, _, failure) = await LoadAsync(ctx, db, ctx.RequestAborted);
        if (failure is not null) return failure;

        var rows = await db.OrgMembers
            .Where(m => m.OrgId == org.Id)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new
            {
                userId = m.UserId,
                email = u.Email,
                displayName = u.DisplayName,
                role = m.Role.ToString(),
                createdAt = m.CreatedAt,
            })
            .OrderBy(x => x.createdAt).ThenBy(x => x.userId)
            .ToListAsync(ctx.RequestAborted);

        return Results.Ok(rows);
    }

    // Owner or Admin may invite. An org-only invitation (DocumentId/DocRole both null) is accepted by
    // the existing POST /api/v1/invitations/{token}:accept - InvitationEndpoints already takes the
    // null-document branch and audits invitation.accepted with documentId: null.
    private static async Task<IResult> Invite(InviteRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        var (org, caller, failure) = await LoadAsync(ctx, db, ctx.RequestAborted);
        if (failure is not null) return failure;
        if (caller.Role is not (OrgRole.Owner or OrgRole.Admin))
            return Problem.Of(403, "Forbidden", "Only an org owner or admin may invite members.");

        var email = req.Email?.Trim() ?? "";
        if (email.Length == 0) return Problem.Of(400, "Invalid request", "email is required.");
        if (!TryParseRole(req.Role, out var role))
            return Problem.Of(400, "Invalid request", "role must be Owner, Admin or Member.");

        var now = DateTimeOffset.UtcNow;
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        db.Add(new Invitation
        {
            OrgId = org.Id,
            Email = email,
            Role = role,
            DocumentId = null,
            DocRole = null,
            TokenHash = MemberEndpoints.HashToken(token),
            InvitedBy = CurrentUser.UserId(ctx.User),
            ExpiresAt = now.AddDays(14),
            CreatedAt = now,
        });
        db.Add(Audit.Event(org.Id, null, CurrentUser.UserId(ctx.User), "org_member.invited",
            "email", email, new { role = role.ToString() }));
        await db.SaveChangesAsync(ctx.RequestAborted);

        // Raw token returned exactly once, like share links and document invitations - only the hash
        // is stored at rest.
        return Results.Created($"/api/v1/org/members", new { email, role = role.ToString(), invitationToken = token });
    }

    // Owner only: changing someone else's org role is the sharpest of these actions.
    private static async Task<IResult> UpdateRole(Guid uid, UpdateRoleRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        var (org, caller, failure) = await LoadAsync(ctx, db, ctx.RequestAborted);
        if (failure is not null) return failure;
        if (caller.Role != OrgRole.Owner)
            return Problem.Of(403, "Forbidden", "Only an org owner may change member roles.");

        if (!TryParseRole(req.Role, out var role))
            return Problem.Of(400, "Invalid request", "role must be Owner, Admin or Member.");

        var target = await db.OrgMembers.FirstOrDefaultAsync(m => m.OrgId == org.Id && m.UserId == uid, ctx.RequestAborted);
        if (target is null) return Problem.Of(404, "Not found", "That user is not a member of this org.");

        if (target.Role == OrgRole.Owner && role != OrgRole.Owner && !await HasAnotherOwnerAsync(db, org.Id, uid, ctx.RequestAborted))
            return Problem.Of(409, "Last owner", "An organization must keep at least one owner.");

        target.Role = role;
        db.Add(Audit.Event(org.Id, null, CurrentUser.UserId(ctx.User), "org_member.role_changed",
            "user", uid.ToString(), new { role = role.ToString() }));
        await db.SaveChangesAsync(ctx.RequestAborted);

        return Results.Ok(new { userId = uid, role = role.ToString() });
    }

    // Owner only, mirroring UpdateRole's gating.
    private static async Task<IResult> Remove(Guid uid, HttpContext ctx, EasyDocsDbContext db)
    {
        var (org, caller, failure) = await LoadAsync(ctx, db, ctx.RequestAborted);
        if (failure is not null) return failure;
        if (caller.Role != OrgRole.Owner)
            return Problem.Of(403, "Forbidden", "Only an org owner may remove members.");

        var target = await db.OrgMembers.FirstOrDefaultAsync(m => m.OrgId == org.Id && m.UserId == uid, ctx.RequestAborted);
        if (target is null) return Problem.Of(404, "Not found", "That user is not a member of this org.");

        if (target.Role == OrgRole.Owner && !await HasAnotherOwnerAsync(db, org.Id, uid, ctx.RequestAborted))
            return Problem.Of(409, "Last owner", "An organization must keep at least one owner.");

        db.Remove(target);
        db.Add(Audit.Event(org.Id, null, CurrentUser.UserId(ctx.User), "org_member.removed", "user", uid.ToString(), null));
        await db.SaveChangesAsync(ctx.RequestAborted);

        return Results.NoContent();
    }

    private static Task<bool> HasAnotherOwnerAsync(EasyDocsDbContext db, Guid orgId, Guid exceptUserId, CancellationToken ct) =>
        db.OrgMembers.AnyAsync(m => m.OrgId == orgId && m.UserId != exceptUserId && m.Role == OrgRole.Owner, ct);
}
