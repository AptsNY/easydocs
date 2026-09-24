using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Auth;

// Service accounts (spec 2026-09-24). Creating one grants nothing — it has no documents until a document
// Owner adds it — so create is an org-management action (Owner/Admin). Minting grants the account's
// document access to whoever holds the token, so only its manager may mint: org role must never imply
// document access (DocumentAuthorization). Delete and revoke grant nothing, so Owner/Admin may also do
// those, as an emergency brake.
public static class ServiceAccountEndpoints
{
    public record CreateRequest(string? Name);
    public record MintRequest(string? Name, DateTimeOffset? ExpiresAt);

    public static void MapServiceAccountEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/org/service-accounts").RequireAuthorization().RequirePerson().WithTags("Org");
        g.MapPost("", Create);
        g.MapGet("", List);
        g.MapPost("/{uid:guid}/tokens", Mint).RequireRateLimiting(RateLimits.TokenMint);
        g.MapDelete("/{uid:guid}", Delete);
    }

    private static Task<OrgRole?> CallerRoleAsync(HttpContext ctx, EasyDocsDbContext db)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);
        return db.OrgMembers.Where(m => m.OrgId == orgId && m.UserId == userId)
            .Select(m => (OrgRole?)m.Role).FirstOrDefaultAsync(ctx.RequestAborted);
    }

    private static bool ManagesOrg(OrgRole? role) => role is OrgRole.Owner or OrgRole.Admin;

    // A service account that belongs to THIS org, or null — cross-org is 404, never 403.
    private static Task<User?> FindAsync(EasyDocsDbContext db, Guid orgId, Guid uid, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.ManagedBy != null
            && db.OrgMembers.Any(m => m.OrgId == orgId && m.UserId == uid), ct);

    private static async Task<IResult> Create(CreateRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        if (!ManagesOrg(await CallerRoleAsync(ctx, db)))
            return Problem.Of(403, "Forbidden", "Only an org owner or admin may create a service account.");
        var name = req.Name?.Trim() ?? "";
        if (name.Length == 0) return Problem.Of(400, "Invalid request", "name is required.");

        var orgId = CurrentUser.OrgId(ctx.User);
        var callerId = CurrentUser.UserId(ctx.User);
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Email = $"svc-{AuthEndpoints.Slugify(name)}-{id.ToString("N")[..8]}@{ServiceAccounts.ServiceDomain}",
            DisplayName = name,
            PasswordHash = null, // local login's `PasswordHash is null` check already refuses it
            ManagedBy = callerId,
            CreatedAt = now,
        };
        db.Add(user);
        db.Add(new OrgMember { OrgId = orgId, UserId = id, Role = OrgRole.Member, CreatedAt = now });
        db.Add(Audit.Event(orgId, null, callerId, "service_account.created", "user", id.ToString(), new { name }));
        await db.SaveChangesAsync(ctx.RequestAborted);

        return Results.Created($"/api/v1/org/service-accounts/{id}", new { userId = id, name, email = user.Email });
    }

    // Owner/Admin see every account; anyone else sees the ones they manage (a demoted manager keeps theirs).
    private static async Task<IResult> List(HttpContext ctx, EasyDocsDbContext db)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var callerId = CurrentUser.UserId(ctx.User);
        var all = ManagesOrg(await CallerRoleAsync(ctx, db));
        var now = DateTimeOffset.UtcNow;

        var rows = await db.Users
            .Where(u => u.ManagedBy != null && (all || u.ManagedBy == callerId)
                && db.OrgMembers.Any(m => m.OrgId == orgId && m.UserId == u.Id))
            .OrderBy(u => u.CreatedAt).ThenBy(u => u.Id)
            .Select(u => new
            {
                userId = u.Id,
                name = u.DisplayName,
                email = u.Email,
                managedBy = db.Users.Where(x => x.Id == u.ManagedBy)
                    .Select(x => new { userId = x.Id, displayName = x.DisplayName }).FirstOrDefault(),
                liveTokens = db.ApiTokens.Count(t => t.UserId == u.Id && t.OrgId == orgId
                    && t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now)),
                lastUsedAt = db.ApiTokens.Where(t => t.UserId == u.Id && t.OrgId == orgId).Max(t => t.LastUsedAt),
                createdAt = u.CreatedAt,
            })
            .ToListAsync(ctx.RequestAborted);

        return Results.Ok(rows);
    }

    private static async Task<IResult> Mint(Guid uid, MintRequest req, HttpContext ctx, EasyDocsDbContext db, ApiTokenService tokens)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var callerId = CurrentUser.UserId(ctx.User);
        var svc = await FindAsync(db, orgId, uid, ctx.RequestAborted);
        if (svc is null) return Problem.Of(404, "Not found", "Service account not found.");
        if (svc.ManagedBy != callerId || await CallerRoleAsync(ctx, db) is null)
            return Problem.Of(403, "Forbidden", "Only this service account's manager may mint its tokens.");

        var name = req.Name?.Trim() ?? "";
        if (name.Length == 0) return Problem.Of(400, "Invalid request", "name is required.");

        var (raw, hash) = tokens.Mint();
        var row = new ApiToken
        {
            OrgId = orgId,
            UserId = uid,
            ServiceName = name,
            TokenHash = hash,
            Scopes = [],
            ExpiresAt = req.ExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(row);
        db.Add(Audit.Event(orgId, null, callerId, "token.created", "token", row.Id.ToString(),
            new { name, serviceAccount = uid, expiresAt = row.ExpiresAt }));
        await db.SaveChangesAsync(ctx.RequestAborted);

        return Results.Created($"/api/v1/tokens/{row.Id}", new { id = row.Id, token = raw });
    }

    private static async Task<IResult> Delete(Guid uid, HttpContext ctx, EasyDocsDbContext db)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var callerId = CurrentUser.UserId(ctx.User);
        var ct = ctx.RequestAborted;
        var svc = await FindAsync(db, orgId, uid, ct);
        if (svc is null) return Problem.Of(404, "Not found", "Service account not found.");
        if (svc.ManagedBy != callerId && !ManagesOrg(await CallerRoleAsync(ctx, db)))
            return Problem.Of(403, "Forbidden", "Only its manager or an org owner or admin may delete a service account.");

        var now = DateTimeOffset.UtcNow;
        var manager = svc.ManagedBy!.Value;

        // Documents it created are solely its own. Hand them to the manager rather than orphan them —
        // no new access: the manager could already reach them by minting a token.
        var solelyOwned = await db.DocumentMembers
            .Where(m => m.UserId == uid && m.Role == DocRole.Owner
                && db.Documents.Any(d => d.Id == m.DocumentId && d.OrgId == orgId)
                && !db.DocumentMembers.Any(o => o.DocumentId == m.DocumentId && o.UserId != uid && o.Role == DocRole.Owner))
            .Select(m => m.DocumentId)
            .ToListAsync(ct);
        foreach (var docId in solelyOwned)
        {
            var existing = await db.DocumentMembers.FirstOrDefaultAsync(m => m.DocumentId == docId && m.UserId == manager, ct);
            if (existing is null)
                db.Add(new DocumentMember { DocumentId = docId, UserId = manager, Role = DocRole.Owner, CreatedAt = now });
            else
                existing.Role = DocRole.Owner;
        }

        await db.ApiTokens.Where(t => t.UserId == uid && t.OrgId == orgId && t.RevokedAt == null)
            .ForEachAsync(t => t.RevokedAt = now, ct);
        db.RemoveRange(await db.DocumentMembers
            .Where(m => m.UserId == uid && db.Documents.Any(d => d.Id == m.DocumentId && d.OrgId == orgId))
            .ToListAsync(ct));
        db.Remove(await db.OrgMembers.SingleAsync(m => m.OrgId == orgId && m.UserId == uid, ct));
        // The Users row stays: audit rows and version authorship reference it (ON DELETE RESTRICT).
        db.Add(Audit.Event(orgId, null, callerId, "service_account.removed", "user", uid.ToString(),
            new { handedOverDocuments = solelyOwned }));
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }
}
