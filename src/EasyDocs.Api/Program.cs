using System.IdentityModel.Tokens.Jwt;
using System.Text;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<IPasswordHasher, Argon2idPasswordHasher>();
builder.Services.AddSingleton<JwtService>();

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
