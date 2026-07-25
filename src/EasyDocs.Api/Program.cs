using EasyDocs.Api.Auth;
using EasyDocs.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<IPasswordHasher, Argon2idPasswordHasher>();
builder.Services.AddSingleton<JwtService>();

// Resolve the connection string at DbContext-resolution time (not registration time) so test
// hosts that inject config via WebApplicationFactory override it before Migrate() runs.
// Fallback keeps `dotnet ef migrations add` working with no live DB (it never connects).
builder.Services.AddDbContext<EasyDocsDbContext>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")
        ?? "Host=localhost;Database=easydocs;Username=postgres;Password=postgres"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>().Database.Migrate();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();

app.Run();

public partial class Program { }
