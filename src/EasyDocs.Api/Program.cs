using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using System.Threading.Channels;
using EasyDocs.Api.Approvals;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Copies;
using EasyDocs.Api.Data;
using EasyDocs.Api.Diffing;
using EasyDocs.Api.Documents;
using EasyDocs.Api.Editing;
using EasyDocs.Api.Events;
using EasyDocs.Api.Folders;
using EasyDocs.Api.Merging;
using EasyDocs.Api.Publishing;
using EasyDocs.Api.Sharing;
using EasyDocs.Api.Versioning;
using EasyDocs.Api.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ASPNETCORE_FORWARDEDHEADERS_ENABLED=true turns the forwarded-headers middleware on, but the
// framework setup it triggers clears KnownProxies/KnownNetworks — i.e. it trusts X-Forwarded-* from
// anyone who can reach the port. These keys narrow that trust to named proxies:
//   ForwardedHeaders__KnownProxies__0=10.0.0.5
//   ForwardedHeaders__KnownNetworks__0=10.0.0.0/8
// PostConfigure so the framework's own setup (an IConfigureOptions) runs first and its Clear()
// cannot wipe these entries. Config is read inside the callback (not at this line) for the same
// reason RequireJwtKeyBytes runs after Build(): test hosts merge their config during Build().
// ParseTrustedProxies is also called once at boot, below, so a typo aborts startup with the
// offending value instead of surfacing as trust granted to nobody (or everybody).
builder.Services.AddOptions<ForwardedHeadersOptions>().PostConfigure<IConfiguration>((o, cfg) =>
{
    var (proxies, networks) = ParseTrustedProxies(cfg);
    foreach (var p in proxies) o.KnownProxies.Add(p);
    foreach (var n in networks) o.KnownIPNetworks.Add(n);
});

// Minimal-API body binding uses its own JSON options (Microsoft.AspNetCore.Http.Json.JsonOptions),
// separate from MVC's (which this project doesn't use) - so the UTC-normalizing DateTimeOffset
// converter has to be registered here to reach every endpoint's request body. See
// UtcDateTimeOffsetConverter for why: Npgsql rejects any non-UTC offset at save time.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter()));

builder.Services.AddScoped<IPasswordHasher, Argon2idPasswordHasher>();
builder.Services.AddScoped<EasyDocs.Api.Versioning.VersioningService>();
builder.Services.AddScoped<EasyDocs.Api.Publishing.PublishService>();
builder.Services.AddSingleton<EventBus>();

// Diff queue (spec §7): commits enqueue parent->child jobs as durable BackgroundJobs rows inside
// their own transaction (issue #16); DiffSummaryWorker drains the table. This channel is only the
// in-process wake-up nudge that keeps latency instant — losing it costs one poll interval, nothing more.
builder.Services.AddSingleton(Channel.CreateUnbounded<DiffJob>());
builder.Services.AddSingleton(sp => sp.GetRequiredService<Channel<DiffJob>>().Writer);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Channel<DiffJob>>().Reader);
builder.Services.AddScoped<WmlComparerDiffService>();
builder.Services.AddScoped<WmlComparerMergeService>();
builder.Services.AddScoped<EasyDocs.Api.Copies.PushService>();
builder.Services.AddHostedService<DiffSummaryWorker>();

// PDF render queue (spec §7): publish enqueues the version id as a durable BackgroundJobs row inside
// the publish transaction (issue #16); PdfRenderBackgroundService drains the table and shells out to
// LibreOffice. This channel is only the wake-up nudge, exactly like the diff one above.
builder.Services.AddSingleton(Channel.CreateUnbounded<Guid>());
builder.Services.AddSingleton(sp => sp.GetRequiredService<Channel<Guid>>().Writer);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Channel<Guid>>().Reader);
builder.Services.AddScoped<LibreOfficePdfRenderer>();
builder.Services.AddHostedService<PdfRenderBackgroundService>();
// Daily sweep of blobs no Versions/VersionDiffs column references (issue #15); grace window
// protects commits in flight. BlobGc__Enabled=false turns it off.
builder.Services.AddHostedService<BlobGarbageCollector>();
// Content indexing for search (issue #12): drains 'extract' jobs, no nudge channel — poll-only.
builder.Services.AddHostedService<EasyDocs.Api.Documents.TextIndexWorker>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<ApiTokenService>(); // stateless: mint/hash `ed_` PATs
builder.Services.AddSingleton<WopiAccessToken>(); // only reads Jwt:Secret
// Singleton so the ~24h discovery cache persists across requests; one long-lived HttpClient is fine
// for a once-daily call. Test/dev use the COLLABORA_ACTION_URL seam and never hit the network.
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp => new CollaboraDiscovery(
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
// Blob backend (issue #14): BlobStore=filesystem (default) keeps the content-addressed volume;
// BlobStore=s3 swaps in any S3-compatible endpoint (AWS, MinIO, R2) configured via S3__* keys.
// An unknown value is a boot error, not a silent fallback to the filesystem — a typo that quietly
// wrote blobs to the wrong place would surface as data loss at the next redeploy.
builder.Services.AddSingleton<IBlobStore>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    return cfg["BlobStore"]?.ToLowerInvariant() switch
    {
        null or "" or "filesystem" => new FileSystemBlobStore(cfg["BLOB_ROOT"]
            ?? throw new InvalidOperationException("BLOB_ROOT not configured")),
        "s3" => S3BlobStore.FromConfiguration(cfg),
        var other => throw new InvalidOperationException(
            $"BlobStore is '{other}' — the supported values are 'filesystem' (default) and 's3'."),
    };
});

// "sub"/"org" claims come through verbatim (no legacy mapping to ClaimTypes.NameIdentifier).
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

// Read Jwt:Secret at DI-resolution time, not here: test hosts (WebApplicationFactory) only merge their
// injected config during Build(), after these top-level statements run. RequireJwtKeyBytes validates it.
// "Composite" is the default scheme: it forwards `Authorization: Bearer ed_...` to the ApiToken PAT
// handler and everything else (JWT bearer / ed_session cookie) to the JWT scheme, so a single
// .RequireAuthorization() accepts either credential without regressing the existing web-app auth.
builder.Services.AddAuthentication("Composite")
    .AddJwtBearer() // stays JwtBearerDefaults.AuthenticationScheme; options configured below
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthHandler>(ApiTokenAuthHandler.SchemeName, null)
    .AddPolicyScheme("Composite", "Composite", o => o.ForwardDefaultSelector = ctx =>
        ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ed_", StringComparison.Ordinal)
            ? ApiTokenAuthHandler.SchemeName
            : JwtBearerDefaults.AuthenticationScheme)
    // OIDC/SSO (issue #9): the handshake cookie exists only to carry the IdP's claims from the
    // OpenIdConnect handler to /api/v1/auth/oidc/complete, which converts them into the ordinary
    // ed_session JWT and signs the handshake back out.
    .AddCookie(OidcEndpoints.HandshakeScheme, o =>
    {
        o.Cookie.Name = "ed_oidc_handshake";
        o.Cookie.SameSite = SameSiteMode.None; // the IdP POSTs/redirects back cross-site
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddOpenIdConnect(OidcEndpoints.Scheme, o => { /* configured below, from IConfiguration */ });
builder.Services.AddOptions<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>(OidcEndpoints.Scheme)
    .Configure<IConfiguration>((o, cfg) =>
    {
        // The handler participates in every request (it watches CallbackPath), so its options must
        // validate even when SSO is off — hence syntactically-valid dummies that can never be hit.
        // The /oidc/login endpoint refuses to challenge unless the real keys are present.
        var configured = OidcEndpoints.Configured(cfg);
        o.Authority = configured ? cfg["Oidc:Authority"] : "https://oidc-not-configured.invalid";
        o.ClientId = configured ? cfg["Oidc:ClientId"] : "not-configured";
        o.ClientSecret = cfg["Oidc:ClientSecret"] ?? "not-configured";
        o.SignInScheme = OidcEndpoints.HandshakeScheme;
        o.CallbackPath = "/api/v1/auth/oidc/callback";
        o.ResponseType = "code";
        o.UsePkce = true;
        o.SaveTokens = false; // easydocs never calls the IdP on the user's behalf; keep the cookie light
        o.GetClaimsFromUserInfoEndpoint = true;
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("email");
        o.Scope.Add("profile");
        o.MapInboundClaims = false; // "email"/"name"/"sub" arrive under their own names
        o.TokenValidationParameters.NameClaimType = "name";
        // An http authority is a dev/test IdP; demand https metadata everywhere else.
        o.RequireHttpsMetadata = !(o.Authority?.StartsWith("http://", StringComparison.Ordinal) ?? false);
    });
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IConfiguration>((o, cfg) =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(RequireJwtKeyBytes(cfg)),
            ValidateLifetime = true,
        };
        // Fall back to the ed_session cookie so browser EventSource/cookie requests authenticate (spec §10.2).
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (string.IsNullOrEmpty(ctx.Token) && ctx.Request.Cookies.TryGetValue("ed_session", out var c))
                    ctx.Token = c;
                return Task.CompletedTask;
            },
        };
    });
// Every session and ed_ token carries an "org" claim; the MFA challenge token deliberately does
// not (issue #10). Requiring it in the default policy is what confines that token to the one
// endpoint that validates it explicitly.
builder.Services.AddAuthorization(o => o.DefaultPolicy =
    new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser().RequireClaim("org").Build());
builder.Services.AddEasyDocsRateLimiter(builder.Configuration); // spec §11 — see RateLimits

// OpenAPI 3.1 doc generated from minimal-API metadata (spec §10.1). Declare the `Bearer`
// (ed_ token) security scheme via a document transformer; served at /openapi/v1.json below.
builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "easydocs API";
        document.Info.Version = "v1";
        document.Components ??= new Microsoft.OpenApi.OpenApiComponents();
        document.Components.SecuritySchemes ??=
            new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "ed_ API token or JWT, sent as `Authorization: Bearer <token>`.",
        };
        return Task.CompletedTask;
    });
});

// Resolve the connection string at DbContext-resolution time (not registration time) so test
// hosts that inject config via WebApplicationFactory override it before Migrate() runs.
// Fallback keeps `dotnet ef migrations add` working with no live DB (it never connects).
builder.Services.AddDbContext<EasyDocsDbContext>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")
        ?? "Host=localhost;Database=easydocs;Username=postgres;Password=postgres"));

var app = builder.Build();

// Fail fast at boot (not first login) if the signing key is missing/too short for HS256.
RequireJwtKeyBytes(app.Configuration);

// Resolve the blob store once so a bad BlobStore value or missing S3__*/BLOB_ROOT key aborts boot
// here, not at the first upload after a redeploy.
_ = app.Services.GetRequiredService<IBlobStore>();

// Fail fast on unparseable trusted-proxy entries, and on entries that would be silently ignored
// because the middleware itself is off — silently-ignored configuration is the exact defect that
// created this knob (issue #17).
{
    var (proxies, networks) = ParseTrustedProxies(app.Configuration);
    if ((proxies.Length > 0 || networks.Length > 0)
        && !app.Configuration.GetValue<bool>("ForwardedHeaders_Enabled"))
        throw new InvalidOperationException(
            "ForwardedHeaders:KnownProxies/KnownNetworks are configured but the forwarded-headers"
            + " middleware is off, so they would be silently ignored — set"
            + " ASPNETCORE_FORWARDEDHEADERS_ENABLED=true as well.");
}

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>().Database.Migrate();

// RFC-7807 for malformed JSON bodies (API-conventions clause: every error is problem+json).
// A body-binding failure (bad GUID/enum/number, truncated JSON, an unparseable date) throws
// BadHttpRequestException wrapping a JsonException *before* any handler runs. The framework
// already turns that into a 400 via the exception's own StatusCode even with nothing registered
// here, but the body it writes is either empty (no Content-Type at all) or, in Development, a
// leaked internal exception dump via the implicit dev-exception-page - never problem+json, and
// never actionable. Registered first (ahead of auth/endpoints) so it wraps the whole downstream
// pipeline and gets first claim on the exception, ahead of that implicit dev page. Narrow by
// type: only BadHttpRequestException (unambiguously a client mistake) is handled; anything else
// is rethrown so a genuine server fault (e.g. a NullReferenceException) still surfaces as a 500.
app.UseExceptionHandler(branch => branch.Run(async ctx =>
{
    var error = ctx.Features.Get<IExceptionHandlerPathFeature>()?.Error
        ?? throw new InvalidOperationException("Exception handler ran without a captured error.");
    if (error is not BadHttpRequestException bad)
        throw error;

    await Problem.Of(bad.StatusCode, "Malformed request body",
        bad.InnerException?.Message ?? bad.Message).ExecuteAsync(ctx);
}));

app.UseAuthentication();
app.UseAuthorization();

// Only endpoints that opt in with .RequireRateLimiting(<policy>) are metered — there is no global
// limiter on purpose, so UseStaticFiles/MapFallbackToFile at the bottom of this file stay unthrottled:
// serving index.html or a JS chunk is not abuse, and 429ing an asset looks like an outage. Placed
// after UseAuthentication so the token-mint policy can partition on the caller's user id, and after
// UseAuthorization so an unauthenticated request is rejected 401 without spending anyone's allowance.
app.UseRateLimiter();

app.MapOpenApi("/openapi/{documentName}.json");
// Self-contained docs UI: Swagger UI ships its assets as embedded resources served same-origin
// under the route prefix (no external CDN — spec §3), pointed at the generated /openapi/v1.json.
app.UseSwaggerUI(o =>
{
    o.RoutePrefix = "docs";
    o.SwaggerEndpoint("/openapi/v1.json", "easydocs API v1");
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();
app.MapMfaEndpoints();
app.MapOidcEndpoints();
app.MapOrgEndpoints();
app.MapInvitationEndpoints();
app.MapTokenEndpoints();
app.MapFolderEndpoints();
app.MapDocumentEndpoints();
app.MapMemberEndpoints();
app.MapAuditEndpoints();
app.MapPublishEndpoints();
app.MapVersionActionEndpoints();
app.MapApprovalEndpoints();
app.MapMergeEndpoints();
app.MapCopyEndpoints();
app.MapPushEndpoints();
app.MapEditingEndpoints();
app.MapEventEndpoints();
app.MapWopiEndpoints(); // token-authorized (query param) — must precede the /wopi/{**rest} 404 below.
app.MapShareEndpoints(); // public /s/{token} viewer — must precede the /s/{**rest} 404 below.

// Serve the SPA. Real endpoints above win on precedence; unmatched non-SPA prefixes
// must 404 (not fall through to index.html), so terminate them before the fallback.
app.UseDefaultFiles();
app.UseStaticFiles();
app.Map("/api/{**rest}", () => Results.NotFound());
app.Map("/wopi/{**rest}", () => Results.NotFound());
app.Map("/s/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

// The narrowing lists for the forwarded-headers middleware. Throws on a malformed entry — callers
// rely on that for boot-time fail-fast.
static (IPAddress[] Proxies, IPNetwork[] Networks) ParseTrustedProxies(IConfiguration cfg) => (
    (cfg.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
        .Select(s => IPAddress.TryParse(s, out var ip) ? ip : throw new InvalidOperationException(
            $"ForwardedHeaders:KnownProxies contains '{s}', which is not an IP address."))
        .ToArray(),
    (cfg.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
        .Select(s => IPNetwork.TryParse(s, out var net) ? net : throw new InvalidOperationException(
            $"ForwardedHeaders:KnownNetworks contains '{s}', which is not CIDR notation (e.g. 10.0.0.0/8)."))
        .ToArray());

// HS256 requires a >= 256-bit (32-byte) key; reject a missing or short secret with a clear message.
static byte[] RequireJwtKeyBytes(IConfiguration cfg)
{
    var secret = cfg["Jwt:Secret"];
    var keyBytes = Encoding.UTF8.GetBytes(secret ?? "");
    if (string.IsNullOrEmpty(secret) || keyBytes.Length < 32)
        throw new InvalidOperationException("Jwt:Secret must be configured and at least 32 bytes (HS256 requires a 256-bit key).");
    return keyBytes;
}

public partial class Program { }
