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

    // What this caller may see and revoke. ONE predicate for both the list and the delete: a scoped list
    // with an unscoped delete would be theatre.
    //
    // A PAT is a per-user capability whose authority never exceeds its owner's (spec §11), so its metadata
    // is its owner's too — a Member enumerating a colleague's token names, scopes and last-used times is
    // not part of that, and neither is an org Owner doing it. Seniority is not ownership.
    //
    // ApiToken.UserId is nullable for org-level service accounts (spec §4): those have no owning user, so
    // `own tokens only` would orphan them. They belong to whoever runs the org — Owner/Admin, the same pair
    // OrgEndpoints gates org management on.
    private static async Task<IQueryable<ApiToken>> VisibleAsync(HttpContext ctx, EasyDocsDbContext db)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);
        var role = await db.OrgMembers
            .Where(m => m.OrgId == orgId && m.UserId == userId)
            .Select(m => (OrgRole?)m.Role)
            .FirstOrDefaultAsync(ctx.RequestAborted);
        var manages = role is OrgRole.Owner or OrgRole.Admin;

        return db.ApiTokens.Where(t => t.OrgId == orgId && (t.UserId == userId || (manages && t.UserId == null)));
    }

    private static async Task<IResult> List(HttpContext ctx, EasyDocsDbContext db)
    {
        var list = await (await VisibleAsync(ctx, db))
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
        // 404 rather than 403 for someone else's token: it is not in this caller's list, so it does not
        // exist for them — the same no-existence-leak rule DocumentAuthorization follows.
        var row = await (await VisibleAsync(ctx, db)).FirstOrDefaultAsync(t => t.Id == id, ctx.RequestAborted);
        if (row is null) return Problem.Of(404, "Not found", "Token not found.");

        row.RevokedAt = DateTimeOffset.UtcNow;
        db.Add(Audit.Event(row.OrgId, null, CurrentUser.UserId(ctx.User), "token.revoked", "token", row.Id.ToString(), null));
        await db.SaveChangesAsync(ctx.RequestAborted);
        return Results.NoContent();
    }
}
