using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace EasyDocs.Api.Auth;

public class JwtService(IConfiguration cfg)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(
        cfg["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret not configured"));

    public string Issue(Guid userId, Guid orgId)
    {
        var creds = new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            claims: [new Claim("sub", userId.ToString()), new Claim("org", orgId.ToString())],
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // The half-signed-in state between a correct password and a correct TOTP code (issue #10).
    // Deliberately NOT a session: no "org" claim, so the default authorization policy (which
    // requires one) rejects it at every endpoint; the only thing it can do is finish MFA.
    public string IssueMfaChallenge(Guid userId)
    {
        var creds = new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            claims: [new Claim("sub", userId.ToString()), new Claim("purpose", "mfa")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>The userId inside a valid, unexpired MFA challenge token — null for anything else.</summary>
    public Guid? ValidateMfaChallenge(string token)
    {
        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(_key),
                ValidateLifetime = true,
            }, out _);
            if (principal.FindFirst("purpose")?.Value != "mfa") return null;
            return Guid.TryParse(principal.FindFirst("sub")?.Value, out var id) ? id : null;
        }
        catch (Exception e) when (e is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }
}
