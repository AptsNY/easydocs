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
}
