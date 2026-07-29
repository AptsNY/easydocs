using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Documents;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Events;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Auth;

// POST /api/v1/invitations/{token}:accept (spec §10.1 Auth). Invitations are minted by
// POST /documents/{id}/members for an email that is not yet in the org; this is where that email
// becomes an org member and a document member.
//
// The caller must be signed in as the invited email — the token proves *someone* was invited, the
// session proves *who is claiming it*. Accepting on a token alone would let anyone holding a leaked
// link join the org.
public static class InvitationEndpoints
{
    public static void MapInvitationEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").WithTags("Auth");
        g.MapPost("/api/v1/invitations/{token}:accept", Accept).RequireAuthorization();
    }

    private static async Task<IResult> Accept(
        string token, HttpContext ctx, EasyDocsDbContext db, JwtService jwt, EventBus bus)
    {
        var userId = CurrentUser.UserId(ctx.User);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ctx.RequestAborted);
        if (user is null) return Problem.Of(401, "Invalid credentials", "User no longer exists.");

        // Unknown and expired are both 404 — a probe learns nothing about which tokens exist.
        var hash = MemberEndpoints.HashToken(token);
        var now = DateTimeOffset.UtcNow;
        var invite = await db.Invitations.FirstOrDefaultAsync(
            i => i.TokenHash == hash && (i.ExpiresAt == null || i.ExpiresAt > now), ctx.RequestAborted);
        if (invite is null) return Problem.Of(404, "Not found", "Invitation not found or expired.");

        if (invite.AcceptedAt is not null)
            return Problem.Of(409, "Already accepted", "This invitation has already been accepted.");
        if (!string.Equals(invite.Email, user.Email, StringComparison.OrdinalIgnoreCase))
            return Problem.Of(403, "Forbidden", "This invitation was issued to a different email address.");

        invite.AcceptedAt = now;

        if (!await db.OrgMembers.AnyAsync(m => m.OrgId == invite.OrgId && m.UserId == userId, ctx.RequestAborted))
            db.Add(new OrgMember { OrgId = invite.OrgId, UserId = userId, Role = invite.Role, CreatedAt = now });

        // Only a fresh row is a real roster change (accept is a no-op retry if the row already exists) —
        // that is the moment, not the invitation mint, this actually becomes a member.added (spec §10.2).
        var joinedDocument = false;
        if (invite.DocumentId is { } docId && invite.DocRole is { } docRole)
        {
            joinedDocument = !await db.DocumentMembers.AnyAsync(m => m.DocumentId == docId && m.UserId == userId, ctx.RequestAborted);
            if (joinedDocument)
                db.Add(new DocumentMember { DocumentId = docId, UserId = userId, Role = docRole, CreatedAt = now });
            db.Add(Audit.Event(invite.OrgId, docId, userId, "invitation.accepted",
                "invitation", invite.Id.ToString(), new { role = docRole.ToString() }));
        }
        else
        {
            db.Add(Audit.Event(invite.OrgId, null, userId, "invitation.accepted",
                "invitation", invite.Id.ToString(), null));
        }

        await db.SaveChangesAsync(ctx.RequestAborted);

        if (joinedDocument)
            bus.Publish(invite.DocumentId!.Value, "member.added",
                new { userId, email = user.Email, role = invite.DocRole!.Value.ToString() });

        // Rebind the session to the invited org. A session carries exactly one org (spec §10.2), so
        // without this an invitee who already had their own org would authenticate against that one and
        // still read the invited document as cross-org (404).
        // ponytail: no org switcher in v1 — a multi-org user's next /auth/login binds to their oldest
        // org. Add POST /auth/switch-org when multi-org membership is a real workflow.
        ctx.Response.Cookies.Append("ed_session", jwt.Issue(userId, invite.OrgId), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });

        return Results.Ok(new
        {
            orgId = invite.OrgId,
            documentId = invite.DocumentId,
            docRole = invite.DocRole?.ToString(),
        });
    }
}
