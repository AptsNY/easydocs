using System.Security.Claims;
using System.Text.Encodings.Web;
using EasyDocs.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EasyDocs.Api.Auth;

// `ed_` bearer PAT authentication (spec §10/§11). Composed with the JWT/cookie scheme by the "Composite"
// policy scheme in Program.cs. A request with `Authorization: Bearer ed_...` authenticates as the token's
// OWNER: it emits sub=UserId, org=OrgId (what CurrentUser reads), so DocumentAuthorization enforces the
// owner's document role and the token never escalates beyond it. Non-`ed_` inputs return NoResult so the
// composite falls through to JWT; a bad/revoked/expired `ed_` fails (-> 401).
public sealed class ApiTokenAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    EasyDocsDbContext db, ApiTokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        const string prefix = "Bearer ";
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(prefix, StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        var raw = header[prefix.Length..].Trim();
        if (!raw.StartsWith("ed_", StringComparison.Ordinal)) return AuthenticateResult.NoResult();

        var hash = tokens.Hash(raw);
        var now = DateTimeOffset.UtcNow;
        var token = await db.ApiTokens.FirstOrDefaultAsync(t =>
            t.TokenHash == hash && t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now));
        if (token?.UserId is not Guid userId) return AuthenticateResult.Fail("Invalid ed_ token.");

        token.LastUsedAt = now; // best-effort; a failed touch must not fail auth
        try { await db.SaveChangesAsync(); } catch (DbUpdateException) { }

        var identity = new ClaimsIdentity(
            [new Claim("sub", userId.ToString()), new Claim("org", token.OrgId.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
