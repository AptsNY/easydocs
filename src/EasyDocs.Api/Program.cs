using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Threading.Channels;
using EasyDocs.Api.Approvals;
using EasyDocs.Api.Auth;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<IPasswordHasher, Argon2idPasswordHasher>();
builder.Services.AddScoped<EasyDocs.Api.Versioning.VersioningService>();
builder.Services.AddScoped<EasyDocs.Api.Publishing.PublishService>();
builder.Services.AddSingleton<EventBus>();

// In-process diff queue (spec §7): commits enqueue parent->child jobs; DiffSummaryWorker drains them
// and computes the numeric summary eagerly. Unbounded is fine — jobs are tiny and recomputable on restart.
builder.Services.AddSingleton(Channel.CreateUnbounded<DiffJob>());
builder.Services.AddSingleton(sp => sp.GetRequiredService<Channel<DiffJob>>().Writer);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Channel<DiffJob>>().Reader);
builder.Services.AddScoped<WmlComparerDiffService>();
builder.Services.AddScoped<WmlComparerMergeService>();
builder.Services.AddHostedService<DiffSummaryWorker>();

// In-process PDF render queue (spec §7): publish enqueues a version id; PdfRenderBackgroundService drains
// it and shells out to LibreOffice. Unbounded — jobs are tiny and re-triggerable by re-publishing.
builder.Services.AddSingleton(Channel.CreateUnbounded<Guid>());
builder.Services.AddSingleton(sp => sp.GetRequiredService<Channel<Guid>>().Writer);
builder.Services.AddSingleton(sp => sp.GetRequiredService<Channel<Guid>>().Reader);
builder.Services.AddScoped<LibreOfficePdfRenderer>();
builder.Services.AddHostedService<PdfRenderBackgroundService>();
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<ApiTokenService>(); // stateless: mint/hash `ed_` PATs
builder.Services.AddSingleton<WopiAccessToken>(); // only reads Jwt:Secret
// Singleton so the ~24h discovery cache persists across requests; one long-lived HttpClient is fine
// for a once-daily call. Test/dev use the COLLABORA_ACTION_URL seam and never hit the network.
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp => new CollaboraDiscovery(
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
builder.Services.AddSingleton<IBlobStore>(sp =>
    new FileSystemBlobStore(sp.GetRequiredService<IConfiguration>()["BLOB_ROOT"]
        ?? throw new InvalidOperationException("BLOB_ROOT not configured")));

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
            : JwtBearerDefaults.AuthenticationScheme);
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
builder.Services.AddAuthorization();

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

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>().Database.Migrate();

app.UseAuthentication();
app.UseAuthorization();

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
app.MapTokenEndpoints();
app.MapFolderEndpoints();
app.MapDocumentEndpoints();
app.MapPublishEndpoints();
app.MapVersionActionEndpoints();
app.MapApprovalEndpoints();
app.MapMergeEndpoints();
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
