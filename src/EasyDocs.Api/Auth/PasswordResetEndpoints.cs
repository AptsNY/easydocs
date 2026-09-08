using System.Buffers.Text;
using System.Security.Cryptography;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Documents;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Auth;

// Admin-issued password reset (spec 2026-09-08). easydocs has no mailer — no SMTP config, no mail
// container — so the link is delivered out of band by the admin who minted it, exactly as invitations
// already work. There is no self-service "forgot password" flow, and the login screen says so.
public static class PasswordResetEndpoints
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public static void MapPasswordResetEndpoints(this WebApplication app)
    {
        // Registered standalone rather than on the /api/v1/org group so both halves of this feature
        // live in one file. Behaviourally identical: the default policy already requires the org claim.
        app.MapPost("/api/v1/org/members/{uid:guid}/password-reset", Mint)
            .RequireAuthorization().WithTags("Org").RequireRateLimiting(RateLimits.TokenMint);
    }

    // True when the target is a member of some OTHER org that has someone else in it.
    //
    // Deliberately not "belongs to more than one org": Register ALWAYS creates an org and
    // InvitationEndpoints.Accept requires a session, so an invited colleague must register (acquiring a
    // personal org) before they can accept. Every invited member is therefore multi-org, and the naive
    // gate would refuse the entire population this feature exists to serve, admitting only founding
    // Owners who were never invited anywhere. Counting other orgs that have more than one member skips
    // the vestigial personal org and still refuses anyone genuinely active on a second team.
    //
    // "Holds Owner or Admin elsewhere" fails for the same reason — everyone owns their personal org.
    private static Task<bool> OnAnotherTeamAsync(
        EasyDocsDbContext db, Guid uid, Guid thisOrg, CancellationToken ct) =>
        db.OrgMembers.AnyAsync(m => m.UserId == uid && m.OrgId != thisOrg
            && db.OrgMembers.Count(x => x.OrgId == m.OrgId) > 1, ct);

    private static async Task<IResult> Mint(Guid uid, HttpContext ctx, EasyDocsDbContext db)
    {
        var callerId = CurrentUser.UserId(ctx.User);
        var orgId = CurrentUser.OrgId(ctx.User);
        var ct = ctx.RequestAborted;

        var caller = await db.OrgMembers.FirstOrDefaultAsync(m => m.OrgId == orgId && m.UserId == callerId, ct);
        if (caller is null || caller.Role is not (OrgRole.Owner or OrgRole.Admin))
            return Problem.Of(403, "Forbidden", "Only an org owner or admin may issue a password reset.");

        // 404, not 403: a caller must not learn whether an account they cannot see exists.
        var target = await db.OrgMembers.FirstOrDefaultAsync(m => m.OrgId == orgId && m.UserId == uid, ct);
        if (target is null) return Problem.Of(404, "Not found", "Member not found.");

        // A reset is full account takeover — strictly sharper than UpdateRole and Remove, which are
        // both Owner-only. An Admin who could reset an Owner would sign in as them and self-promote.
        if (caller.Role == OrgRole.Admin && target.Role is OrgRole.Owner or OrgRole.Admin)
            return Problem.Of(403, "Forbidden", "An admin may only reset the password of a member.");

        if (await OnAnotherTeamAsync(db, uid, orgId, ct))
            return Problem.Of(409, "Other organizations",
                "This account is a member of another organization, so it cannot be reset from here.");

        var user = await db.Users.FirstAsync(u => u.Id == uid, ct);
        if (user.PasswordHash is null)
            return Problem.Of(409, "No password",
                "This account signs in through SSO and has no password to reset.");

        var now = DateTimeOffset.UtcNow;

        // Re-issuing supersedes any outstanding link: the admin who sent the first one to the wrong
        // chat window expects it to stop working.
        await db.PasswordResets
            .Where(p => p.UserId == uid && p.UsedAt == null)
            .ForEachAsync(p => p.UsedAt = now, ct);

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        db.Add(new PasswordReset
        {
            UserId = uid,
            OrgId = orgId,
            TokenHash = MemberEndpoints.HashToken(token),
            ExpiresAt = now + Lifetime,
            CreatedAt = now,
        });
        db.Add(Audit.Event(orgId, null, callerId, "password_reset.issued", "user", uid.ToString(), null));
        await db.SaveChangesAsync(ct);

        // Relative, like ShareEndpoints' `/s/{token}`: synthesizing an absolute URL means trusting Host
        // or the forwarded headers behind the reverse proxy the README documents as the TLS terminator.
        return Results.Ok(new { token, url = $"/password-reset/{token}", expiresAt = now + Lifetime });
    }
}
