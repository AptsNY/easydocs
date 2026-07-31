using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;

namespace EasyDocs.Api.Common;

// Rate limits for the surfaces reachable without a trusted caller (spec §11). Three named policies,
// opt-in per endpoint — deliberately NOT a global limiter, because the SPA's static assets and the
// index.html fallback must never be metered: serving a shell is not abuse, and a throttled asset
// load reads as an outage rather than as a limit.
//
// The partition key and the tightness are chosen per threat rather than uniformly:
//   AnonShare    — client IP. /s/{token} is fully anonymous and the token IS the capability, so an
//                  unthrottled route is a token-enumeration oracle. Tight.
//   AnonDownload — client IP, and separate from AnonShare precisely because it is the expensive half:
//                  a viewer costs a row read, a download streams the whole .docx. Sharing one bucket
//                  would force the egress cap up to whatever page views need. Tight.
//   Auth         — (path, client IP). One shared budget, but login and register get INDEPENDENT
//                  buckets, so flooding registration cannot 429 every legitimate login on the install —
//                  which is exactly what a shared bucket would do once a proxy collapses all callers
//                  onto one address (see below). A token bucket, not a fixed window, because burst and
//                  sustained rates have to differ by an order of magnitude here.
//   TokenMint    — the authenticated user id. Genuinely more meaningful than an IP: flooding PAT
//                  creation is per-account abuse and every caller is already authenticated. Requires
//                  UseRateLimiter to sit after UseAuthentication (see Program.cs).
//
// Everything is configurable (RateLimit:<Policy>:*), and the Auth defaults are deliberately loose — a
// flood brake, not a quota. Be clear-eyed about what that does and does not buy (SECURITY.md says the
// same): it stops one client from burning unbounded Argon2id CPU or creating unbounded orgs; it does
// NOT stop patient or distributed credential stuffing, which is a WAF/fail2ban job at the proxy.
// The numbers are forced by two structural facts, not by taste:
//   1. easydocs documents a reverse proxy as the TLS terminator (README). Unless the operator also
//      sets ASPNETCORE_FORWARDEDHEADERS_ENABLED=true, every request arrives carrying the PROXY's
//      address, so a per-IP limit degrades to an install-wide one. It fails closed rather than open,
//      but it means the shipped numbers must tolerate a whole office behind one NAT.
//   2. This project's own Playwright suite registers ~70 orgs and signs in ~60 times in ~11 seconds
//      from a single address, and contributors re-run it back to back. Measured, not assumed: an
//      earlier 300-token bucket passed one run and 429'd nineteen specs on the second.
public static class RateLimits
{
    public const string AnonShare = "anon-share";
    public const string AnonDownload = "anon-download";
    public const string Auth = "auth";
    public const string TokenMint = "token-mint";

    public static IServiceCollection AddEasyDocsRateLimiter(this IServiceCollection services, IConfiguration cfg) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests; // the default is 503, which lies
            o.OnRejected = async (ctx, _) =>
            {
                // Token bucket and fixed window both surface RetryAfter; pass it on so a well-behaved
                // client backs off instead of hammering.
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after))
                    ctx.HttpContext.Response.Headers.RetryAfter =
                        ((int)after.TotalSeconds).ToString(CultureInfo.InvariantCulture);

                // problem+json like every other error in the API (API-conventions clause).
                await Problem.Of(StatusCodes.Status429TooManyRequests, "Too many requests",
                    "Rate limit exceeded for this endpoint. Retry later.").ExecuteAsync(ctx.HttpContext);
            };

            // A recipient opens the landing page once and the SPA re-fetches it as JSON, so 120/min is
            // ~50x any real need while leaving a 128-bit token utterly infeasible to guess.
            o.AddPolicy(AnonShare, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = cfg.GetValue("RateLimit:AnonShare:PermitLimit", 120),
                    Window = TimeSpan.FromSeconds(cfg.GetValue("RateLimit:AnonShare:WindowSeconds", 60)),
                }));

            // The egress cap. 30/min of a multi-MB .docx is already generous for one client; a whole
            // team opening the same link is the case that makes an operator raise it.
            o.AddPolicy(AnonDownload, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = cfg.GetValue("RateLimit:AnonDownload:PermitLimit", 30),
                    Window = TimeSpan.FromSeconds(cfg.GetValue("RateLimit:AnonDownload:WindowSeconds", 60)),
                }));

            // 1000 burst, refilling 50 every 10s => 300/min sustained. See the header note on why this
            // is a flood brake rather than a quota.
            // Path is part of the key, so login and register meter independently. Only two endpoints
            // carry this policy, so the key space stays bounded — no partition-per-URL growth.
            o.AddPolicy(Auth, ctx => RateLimitPartition.GetTokenBucketLimiter(
                $"{ctx.Request.Path}|{ClientKey(ctx)}", _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = cfg.GetValue("RateLimit:Auth:BurstLimit", 1000),
                    TokensPerPeriod = cfg.GetValue("RateLimit:Auth:TokensPerPeriod", 50),
                    ReplenishmentPeriod =
                        TimeSpan.FromSeconds(cfg.GetValue("RateLimit:Auth:ReplenishmentSeconds", 10)),
                    QueueLimit = 0, // reject, never queue: a queued login is a parked Argon2id thread
                }));

            // Per user, so one compromised session cannot mint an unbounded pile of long-lived PATs.
            o.AddPolicy(TokenMint, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.FindFirstValue("sub") ?? ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = cfg.GetValue("RateLimit:TokenMint:PermitLimit", 20),
                    Window = TimeSpan.FromSeconds(cfg.GetValue("RateLimit:TokenMint:WindowSeconds", 60)),
                }));
        });

    // TestServer leaves RemoteIpAddress null, and so would a unix-socket peer; "" is a correct shared
    // partition for "no distinguishable client" — it groups them, it never exempts them.
    private static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "";
}
