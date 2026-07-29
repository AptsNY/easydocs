using System.Buffers.Text;
using System.Security.Cryptography;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Documents;

// Per-document membership (spec §10.1 "Members", §11). Membership is strictly per-document: org role
// grants no implicit access, so this is the only way a second person reaches a document.
//
// Roles: any member may read the roster; only an Owner may change it. Adding by email either grants
// membership directly (the user is already in this org) or mints an invitation (§10.1 Auth accept flow) —
// never a silent cross-org grant.
public static class MemberEndpoints
{
    public record AddRequest(string? Email, string? Role);
    public record UpdateRequest(string? Role);

    public static void MapMemberEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/documents").RequireAuthorization().WithTags("Members");
        g.MapGet("/{id:guid}/members", List);
        g.MapPost("/{id:guid}/members", Add);
        g.MapPatch("/{id:guid}/members/{uid:guid}", Update);
        g.MapDelete("/{id:guid}/members/{uid:guid}", Remove);
    }

    internal static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static bool TryParseRole(string? value, out DocRole role) =>
        Enum.TryParse(value, ignoreCase: true, out role) && Enum.IsDefined(role);

    private static async Task<IResult> List(Guid id, HttpContext ctx, EasyDocsDbContext db)
    {
        var (_, _, failure) = await DocumentAuthorization.AuthorizeAsync(db, ctx, id, Need.Read, ct: ctx.RequestAborted);
        if (failure is not null) return failure;

        var rows = await db.DocumentMembers
            .Where(m => m.DocumentId == id)
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

    private static async Task<IResult> Add(Guid id, AddRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        var (doc, _, failure) = await DocumentAuthorization.AuthorizeAsync(db, ctx, id, Need.Own, ct: ctx.RequestAborted);
        if (failure is not null) return failure;

        var email = req.Email?.Trim() ?? "";
        if (email.Length == 0) return Problem.Of(400, "Invalid request", "email is required.");
        if (!TryParseRole(req.Role, out var role))
            return Problem.Of(400, "Invalid request", "role must be Owner, Editor or Viewer.");

        var now = DateTimeOffset.UtcNow;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ctx.RequestAborted);
        var inOrg = user is not null
            && await db.OrgMembers.AnyAsync(m => m.OrgId == doc!.OrgId && m.UserId == user.Id, ctx.RequestAborted);

        // Already an org member -> grant document membership now.
        if (user is not null && inOrg)
        {
            if (await db.DocumentMembers.AnyAsync(m => m.DocumentId == id && m.UserId == user.Id, ctx.RequestAborted))
                return Problem.Of(409, "Already a member", "That user is already a member of this document.");

            db.Add(new DocumentMember { DocumentId = id, UserId = user.Id, Role = role, CreatedAt = now });
            db.Add(Audit.Event(doc!.OrgId, id, CurrentUser.UserId(ctx.User), "member.added",
                "user", user.Id.ToString(), new { role = role.ToString() }));
            await db.SaveChangesAsync(ctx.RequestAborted);

            return Results.Created($"/api/v1/documents/{id}/members/{user.Id}",
                new { userId = user.Id, email, role = role.ToString() });
        }

        // Unknown email, or a user outside this org: mint an invitation. Only the hash is stored (§11);
        // the raw token is returned exactly once, like share links.
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        db.Add(new Invitation
        {
            OrgId = doc!.OrgId,
            Email = email,
            Role = OrgRole.Member,
            DocumentId = id,
            DocRole = role,
            TokenHash = HashToken(token),
            InvitedBy = CurrentUser.UserId(ctx.User),
            ExpiresAt = now.AddDays(14),
            CreatedAt = now,
        });
        db.Add(Audit.Event(doc.OrgId, id, CurrentUser.UserId(ctx.User), "member.invited",
            "email", email, new { role = role.ToString() }));
        await db.SaveChangesAsync(ctx.RequestAborted);

        return Results.Created($"/api/v1/documents/{id}/members",
            new { email, role = role.ToString(), invitationToken = token });
    }

    private static async Task<IResult> Update(Guid id, Guid uid, UpdateRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        var (doc, _, failure) = await DocumentAuthorization.AuthorizeAsync(db, ctx, id, Need.Own, ct: ctx.RequestAborted);
        if (failure is not null) return failure;

        if (!TryParseRole(req.Role, out var role))
            return Problem.Of(400, "Invalid request", "role must be Owner, Editor or Viewer.");

        var member = await db.DocumentMembers.FirstOrDefaultAsync(m => m.DocumentId == id && m.UserId == uid, ctx.RequestAborted);
        if (member is null) return Problem.Of(404, "Not found", "That user is not a member of this document.");

        if (member.Role == DocRole.Owner && role != DocRole.Owner && !await HasAnotherOwnerAsync(db, id, uid, ctx.RequestAborted))
            return Problem.Of(409, "Last owner", "A document must keep at least one owner.");

        member.Role = role;
        db.Add(Audit.Event(doc!.OrgId, id, CurrentUser.UserId(ctx.User), "member.role_changed",
            "user", uid.ToString(), new { role = role.ToString() }));
        await db.SaveChangesAsync(ctx.RequestAborted);

        return Results.Ok(new { userId = uid, role = role.ToString() });
    }

    private static async Task<IResult> Remove(Guid id, Guid uid, HttpContext ctx, EasyDocsDbContext db)
    {
        var (doc, _, failure) = await DocumentAuthorization.AuthorizeAsync(db, ctx, id, Need.Own, ct: ctx.RequestAborted);
        if (failure is not null) return failure;

        var member = await db.DocumentMembers.FirstOrDefaultAsync(m => m.DocumentId == id && m.UserId == uid, ctx.RequestAborted);
        if (member is null) return Problem.Of(404, "Not found", "That user is not a member of this document.");

        if (member.Role == DocRole.Owner && !await HasAnotherOwnerAsync(db, id, uid, ctx.RequestAborted))
            return Problem.Of(409, "Last owner", "A document must keep at least one owner.");

        db.Remove(member);
        db.Add(Audit.Event(doc!.OrgId, id, CurrentUser.UserId(ctx.User), "member.removed", "user", uid.ToString(), null));
        await db.SaveChangesAsync(ctx.RequestAborted);

        return Results.NoContent();
    }

    private static Task<bool> HasAnotherOwnerAsync(EasyDocsDbContext db, Guid docId, Guid exceptUserId, CancellationToken ct) =>
        db.DocumentMembers.AnyAsync(m => m.DocumentId == docId && m.UserId != exceptUserId && m.Role == DocRole.Owner, ct);
}
