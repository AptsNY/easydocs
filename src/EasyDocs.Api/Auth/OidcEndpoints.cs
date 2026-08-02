using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Auth;

// OIDC sign-in (issue #9), configured by three keys: Oidc__Authority, Oidc__ClientId,
// Oidc__ClientSecret. The framework's OpenIdConnect handler runs the whole authorization-code +
// PKCE dance into a throwaway handshake cookie; /complete turns that into the same ed_session JWT a
// password login issues, so everything downstream — org claim, switch-org, ed_ tokens — is
// identical no matter how the user proved who they are.
//
// Provisioning is by verified email. A first-time SSO user gets a user row and their own
// organization, exactly like self-serve registration; joining an existing org stays what it always
// was — an invitation. An IdP that says email_verified=false is refused: matching an unverified
// email onto an existing local account would let anyone with a rogue IdP claim any mailbox.
public static class OidcEndpoints
{
    public const string Scheme = "Oidc";
    public const string HandshakeScheme = "OidcHandshake";

    public static bool Configured(IConfiguration cfg) =>
        !string.IsNullOrEmpty(cfg["Oidc:Authority"]) && !string.IsNullOrEmpty(cfg["Oidc:ClientId"]);

    public static void MapOidcEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").WithTags("SSO");
        g.MapGet("/api/v1/auth/oidc", (IConfiguration cfg) => Results.Ok(new { enabled = Configured(cfg) }));
        g.MapGet("/api/v1/auth/oidc/login", Login).RequireRateLimiting(RateLimits.Auth);
        g.MapGet("/api/v1/auth/oidc/complete", Complete);
    }

    private static IResult Login(IConfiguration cfg) =>
        Configured(cfg)
            ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/api/v1/auth/oidc/complete" }, [Scheme])
            : Problem.Of(404, "SSO not configured", "This install has no OIDC provider configured.");

    private static async Task<IResult> Complete(HttpContext ctx, EasyDocsDbContext db, JwtService jwt)
    {
        var handshake = await ctx.AuthenticateAsync(HandshakeScheme);
        if (!handshake.Succeeded)
            return Problem.Of(401, "SSO sign-in failed", "The identity provider did not complete the sign-in.");
        // One-shot: the handshake cookie's only purpose is to carry the IdP's claims to this line.
        await ctx.SignOutAsync(HandshakeScheme);

        var email = handshake.Principal.FindFirst("email")?.Value?.Trim();
        if (string.IsNullOrEmpty(email))
            return Problem.Of(400, "SSO sign-in failed", "The identity provider returned no email claim.");
        if (handshake.Principal.FindFirst("email_verified")?.Value is "false" or "False")
            return Problem.Of(403, "SSO sign-in failed", "The identity provider reports this email as unverified.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            var displayName = handshake.Principal.FindFirst("name")?.Value?.Trim();
            if (string.IsNullOrEmpty(displayName)) displayName = email[..email.IndexOf('@')];
            var now = DateTimeOffset.UtcNow;
            var org = new Organization
            {
                Id = Guid.NewGuid(),
                Name = $"{displayName}'s organization",
                Slug = await AuthEndpoints.UniqueSlugAsync(db, AuthEndpoints.Slugify(displayName)),
                CreatedAt = now,
            };
            user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                DisplayName = displayName,
                PasswordHash = null, // SSO-only account; local login's uniform 401 already handles this
                CreatedAt = now,
            };
            db.Add(org);
            db.Add(user);
            db.Add(new OrgMember { OrgId = org.Id, UserId = user.Id, Role = OrgRole.Owner, CreatedAt = now });
            db.Add(Audit.Event(org.Id, null, user.Id, "user.sso_provisioned", "user", user.Id.ToString(),
                new { issuer = handshake.Principal.FindFirst("iss")?.Value }));
            await db.SaveChangesAsync();
        }

        // ponytail: MFA is not demanded on the SSO path — the IdP owns the second factor there.
        // Revisit only if someone needs local TOTP stacked on top of SSO.
        var member = await db.OrgMembers
            .Where(m => m.UserId == user.Id)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.OrgId)
            .FirstAsync();
        ctx.Response.Cookies.Append("ed_session", jwt.Issue(user.Id, member.OrgId), AuthEndpoints.SessionCookie);
        return Results.Redirect("/");
    }
}
