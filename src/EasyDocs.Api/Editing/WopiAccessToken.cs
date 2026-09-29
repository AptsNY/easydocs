using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace EasyDocs.Api.Editing;

// WOPI/WebDAV access token. typ="wopi" firewalls it from login JWTs: a login cookie must NOT
// authorize WOPI, and a WOPI token must NOT authorize the app (spec §6.1). Same Jwt:Secret/HS256 as JwtService.
//
// Its lifetime is the edit session's, not a refresh interval: neither Collabora nor desktop Word ever
// refreshes a token, so a 30-minute TTL silently stopped every save in an editor left open past ~35
// minutes. What cuts a session off is the per-request DB check (WopiEndpoints.LiveSessionAsync: session
// open, user still an org member with Editor+ on the document), not this expiry, which is only the outer bound.
public class WopiAccessToken(IConfiguration cfg)
{
    public const int TtlSeconds = 12 * 3600; // a working day, and then some

    private readonly byte[] _key = Encoding.UTF8.GetBytes(
        cfg["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret not configured"));

    // ExpiresAt is the token's exact exp (whole seconds), for WOPI's access_token_ttl.
    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid sid, Guid uid, string perms)
    {
        var expires = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TtlSeconds);
        var creds = new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            claims:
            [
                new Claim("sid", sid.ToString()),
                new Claim("sub", uid.ToString()),
                new Claim("perms", perms),
                new Claim("typ", "wopi"),
            ],
            expires: expires.UtcDateTime,
            signingCredentials: creds);
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    // Returns null on any failure: bad signature, expired, or typ != "wopi" (a login JWT must not validate here).
    public (Guid Sid, Guid Uid, string Perms)? Validate(string token)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false }; // "typ"/"sid"/"sub" verbatim
        try
        {
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(_key),
                ValidateLifetime = true,
            }, out _);

            if (principal.FindFirstValue("typ") != "wopi") return null;
            var sid = principal.FindFirstValue("sid");
            var sub = principal.FindFirstValue("sub");
            var perms = principal.FindFirstValue("perms");
            if (sid is null || sub is null || perms is null) return null;
            return (Guid.Parse(sid), Guid.Parse(sub), perms);
        }
        catch
        {
            return null;
        }
    }
}
