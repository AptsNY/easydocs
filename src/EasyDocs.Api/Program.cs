using System.IdentityModel.Tokens.Jwt;
using System.Text;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Data;
using EasyDocs.Api.Documents;
using EasyDocs.Api.Editing;
using EasyDocs.Api.Folders;
using EasyDocs.Api.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<IPasswordHasher, Argon2idPasswordHasher>();
builder.Services.AddScoped<EasyDocs.Api.Versioning.VersioningService>();
builder.Services.AddSingleton<JwtService>();
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
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
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

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();
app.MapFolderEndpoints();
app.MapDocumentEndpoints();
app.MapEditingEndpoints();
app.MapWopiEndpoints(); // token-authorized (query param) — must precede the /wopi/{**rest} 404 below.

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
