using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Auth;

// TOTP second factor for local accounts (issue #10). Setup is two steps on purpose: /setup stores a
// pending secret, /enable arms it only after the authenticator proves it can produce a code —
// enabling MFA off a mistyped secret would lock the account the moment the session ends. Recovery
// codes are single-use fallbacks shown exactly once, stored hashed.
public static class MfaEndpoints
{
    public static void MapMfaEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").WithTags("MFA");
        // Finishing a login burns nothing but a hash compare, but it IS a credential guess — same
        // budget as login itself (spec §11).
        g.MapPost("/api/v1/auth/login/mfa", FinishLogin).RequireRateLimiting(RateLimits.Auth);
        g.MapGet("/api/v1/account/mfa", Status).RequireAuthorization();
        g.MapPost("/api/v1/account/mfa/setup", Setup).RequireAuthorization();
        g.MapPost("/api/v1/account/mfa/enable", Enable).RequireAuthorization();
        g.MapPost("/api/v1/account/mfa/disable", Disable).RequireAuthorization();
    }

    public record FinishLoginRequest(string? MfaToken, string? Code);
    public record CodeRequest(string? Code);

    private static async Task<IResult> FinishLogin(
        FinishLoginRequest req, HttpContext ctx, EasyDocsDbContext db, JwtService jwt)
    {
        var userId = jwt.ValidateMfaChallenge(req.MfaToken ?? "");
        var user = userId is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        // Uniform 401 for a bad token, a disarmed account, a wrong code, or a spent recovery code.
        if (user?.TotpSecret is null || user.TotpEnabledAt is null
            || !await ConsumeAsync(db, user, req.Code ?? ""))
            return Problem.Of(401, "Invalid code", "The code is incorrect or expired.");

        var member = await db.OrgMembers
            .Where(m => m.UserId == user.Id)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.OrgId)
            .FirstAsync();
        ctx.Response.Cookies.Append("ed_session", jwt.Issue(user.Id, member.OrgId), AuthEndpoints.SessionCookie);
        return Results.Ok(new { id = user.Id, email = user.Email, displayName = user.DisplayName, orgId = member.OrgId });
    }

    private static async Task<IResult> Status(HttpContext ctx, EasyDocsDbContext db)
    {
        var user = await db.Users.FirstAsync(u => u.Id == CurrentUser.UserId(ctx.User));
        return Results.Ok(new
        {
            enabled = user.TotpEnabledAt is not null,
            recoveryCodesLeft = user.TotpEnabledAt is null ? 0 : user.RecoveryCodeHashes.Length,
        });
    }

    private static async Task<IResult> Setup(HttpContext ctx, EasyDocsDbContext db)
    {
        var user = await db.Users.FirstAsync(u => u.Id == CurrentUser.UserId(ctx.User));
        if (user.TotpEnabledAt is not null)
            return Problem.Of(409, "MFA already enabled", "Disable it before setting up a new authenticator.");

        // Re-running setup replaces the pending secret — abandoning a half-finished setup is normal.
        user.TotpSecret = Totp.NewSecret();
        await db.SaveChangesAsync();
        return Results.Ok(new
        {
            secret = user.TotpSecret,
            otpauthUri = Totp.OtpauthUri(user.Email, user.TotpSecret),
        });
    }

    private static async Task<IResult> Enable(CodeRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        var user = await db.Users.FirstAsync(u => u.Id == CurrentUser.UserId(ctx.User));
        if (user.TotpEnabledAt is not null)
            return Problem.Of(409, "MFA already enabled", "It is already on.");
        if (user.TotpSecret is null)
            return Problem.Of(400, "No pending setup", "Call /api/v1/account/mfa/setup first.");
        if (!Totp.Verify(user.TotpSecret, req.Code ?? "", DateTimeOffset.UtcNow))
            return Problem.Of(400, "Invalid code", "The code does not match the pending secret.");

        var codes = RecoveryCodes.Generate();
        user.TotpEnabledAt = DateTimeOffset.UtcNow;
        user.RecoveryCodeHashes = [.. codes.Select(RecoveryCodes.Hash)];
        db.Add(Audit.Event(CurrentUser.OrgId(ctx.User), null, user.Id, "user.mfa_enabled",
            "user", user.Id.ToString(), new { }));
        await db.SaveChangesAsync();
        // The only time the plaintext codes exist outside this response.
        return Results.Ok(new { recoveryCodes = codes });
    }

    private static async Task<IResult> Disable(CodeRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        var user = await db.Users.FirstAsync(u => u.Id == CurrentUser.UserId(ctx.User));
        if (user.TotpEnabledAt is null || user.TotpSecret is null)
            return Problem.Of(400, "MFA not enabled", "There is nothing to disable.");
        // A code is required even mid-session: a walked-up-to unlocked browser must not be enough
        // to strip the account's second factor.
        if (!await ConsumeAsync(db, user, req.Code ?? ""))
            return Problem.Of(400, "Invalid code", "A current authenticator or recovery code is required.");

        user.TotpSecret = null;
        user.TotpEnabledAt = null;
        user.RecoveryCodeHashes = [];
        db.Add(Audit.Event(CurrentUser.OrgId(ctx.User), null, user.Id, "user.mfa_disabled",
            "user", user.Id.ToString(), new { }));
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    // Accepts a live TOTP code, or spends one recovery code. Persists the spend itself so a
    // recovery code can never be replayed even when the caller's SaveChanges is skipped later.
    private static async Task<bool> ConsumeAsync(EasyDocsDbContext db, User user, string code)
    {
        if (Totp.Verify(user.TotpSecret!, code, DateTimeOffset.UtcNow)) return true;
        var hash = RecoveryCodes.Hash(code);
        if (!user.RecoveryCodeHashes.Contains(hash)) return false;
        user.RecoveryCodeHashes = [.. user.RecoveryCodeHashes.Where(h => h != hash)];
        await db.SaveChangesAsync();
        return true;
    }
}
