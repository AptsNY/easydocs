using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Auth;

// `ed_` personal access token management (spec §10). A logged-in user mints/lists/revokes tokens for
// their own org. The raw token is returned exactly once at creation; only the hash is stored.
// NOTE: accepting `ed_` on requests is Task 2 — not wired here.
public static class TokenEndpoints
{
    public record CreateRequest(string Name, string[] Scopes, DateTimeOffset? ExpiresAt);

    public static void MapTokenEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").WithTags("Tokens");
        // Per-user rate limit (spec §11, see RateLimits): a stolen session should not be able to mint an
        // unbounded pile of long-lived PATs that survive a password change.
        g.MapPost("/api/v1/tokens", Create).RequireAuthorization().RequireRateLimiting(RateLimits.TokenMint);
        g.MapGet("/api/v1/tokens", List).RequireAuthorization();
        g.MapDelete("/api/v1/tokens/{id:guid}", Revoke).RequireAuthorization();
    }

    private static async Task<IResult> Create(CreateRequest req, HttpContext ctx, EasyDocsDbContext db, ApiTokenService tokens)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length == 0)
            return Problem.Of(400, "Invalid request", "name is required.");

        var (raw, hash) = tokens.Mint();
        var row = new ApiToken
        {
            OrgId = CurrentUser.OrgId(ctx.User),
            UserId = CurrentUser.UserId(ctx.User),
            ServiceName = name,
            TokenHash = hash,
            Scopes = req.Scopes ?? [],
            ExpiresAt = req.ExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(row);
        db.Add(Audit.Event(row.OrgId, null, row.UserId, "token.created", "token", row.Id.ToString(),
            new { name, scopes = row.Scopes, expiresAt = row.ExpiresAt }));
        await db.SaveChangesAsync(ctx.RequestAborted);

        return Results.Created($"/api/v1/tokens/{row.Id}", new { id = row.Id, token = raw });
    }

    private static async Task<IResult> List(HttpContext ctx, EasyDocsDbContext db)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var list = await db.ApiTokens
            .Where(t => t.OrgId == orgId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new
            {
                id = t.Id,
                serviceName = t.ServiceName,
                scopes = t.Scopes,
                expiresAt = t.ExpiresAt,
                lastUsedAt = t.LastUsedAt,
                revokedAt = t.RevokedAt,
                createdAt = t.CreatedAt,
            })
            .ToListAsync(ctx.RequestAborted);
        return Results.Ok(list);
    }

    private static async Task<IResult> Revoke(Guid id, HttpContext ctx, EasyDocsDbContext db)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var row = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id && t.OrgId == orgId, ctx.RequestAborted);
        if (row is null) return Problem.Of(404, "Not found", "Token not found.");

        row.RevokedAt = DateTimeOffset.UtcNow;
        db.Add(Audit.Event(orgId, null, CurrentUser.UserId(ctx.User), "token.revoked", "token", row.Id.ToString(), null));
        await db.SaveChangesAsync(ctx.RequestAborted);
        return Results.NoContent();
    }
}
