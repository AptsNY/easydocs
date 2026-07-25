using System.Security.Claims;

namespace EasyDocs.Api.Auth;

// Reads identity from JWT claims. Startup sets DefaultMapInboundClaims=false so "sub"/"org" come through verbatim.
public static class CurrentUser
{
    public static Guid UserId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("sub")!);
    public static Guid OrgId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue("org")!);
}
