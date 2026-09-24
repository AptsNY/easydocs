using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Auth;

// Service accounts (spec 2026-09-24): a Users row with ManagedBy set. It cannot sign in — no password,
// and its address is under a reserved domain no identity provider can assert — but its `ed_` token
// authenticates it on every RequireAuthorization() route. Person-only routes opt in to RequirePerson().
public static class ServiceAccounts
{
    // RFC 2606 reserves .invalid: register refuses it, so the domain means "service" and nothing else.
    public const string ServiceDomain = "service.invalid";

    public static Task<bool> IsServiceAsync(EasyDocsDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Id == userId && u.ManagedBy != null, ct);

    // Per route, not global: the hot document/version paths should not pay a Users read per request.
    // Endpoint filters run after authorization, so the caller is always authenticated here.
    public static TBuilder RequirePerson<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var db = http.RequestServices.GetRequiredService<EasyDocsDbContext>();
            if (await IsServiceAsync(db, CurrentUser.UserId(http.User), http.RequestAborted))
                return Problem.Of(403, "Forbidden", "A service account cannot do this.");
            return await next(ctx);
        });
}
