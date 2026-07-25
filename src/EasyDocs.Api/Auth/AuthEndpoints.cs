using System.Text.RegularExpressions;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Auth;

public static partial class AuthEndpoints
{
    public record RegisterRequest(string Email, string DisplayName, string Password, string OrgName);
    public record LoginRequest(string Email, string Password);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/auth/register", Register);
        app.MapPost("/api/v1/auth/login", Login);
        app.MapGet("/api/v1/me", Me).RequireAuthorization();
    }

    private static async Task<IResult> Register(
        RegisterRequest req, HttpContext ctx, EasyDocsDbContext db,
        IPasswordHasher hasher, JwtService jwt)
    {
        var email = req.Email?.Trim() ?? "";
        var displayName = req.DisplayName?.Trim() ?? "";
        var orgName = req.OrgName?.Trim() ?? "";

        if (email.Length == 0 || displayName.Length == 0 || orgName.Length == 0)
            return Problem.Of(400, "Invalid request", "email, displayName and orgName are required.");
        if ((req.Password?.Length ?? 0) < 12)
            return Problem.Of(400, "Invalid request", "password must be at least 12 characters.");

        if (await db.Users.AnyAsync(u => u.Email == email))
            return Problem.Of(409, "Email in use", "A user with that email already exists.");

        var now = DateTimeOffset.UtcNow;
        var org = new Organization
        {
            Id = Guid.NewGuid(),
            Name = orgName,
            Slug = await UniqueSlugAsync(db, Slugify(orgName)),
            CreatedAt = now,
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = displayName,
            PasswordHash = hasher.Hash(req.Password!),
            CreatedAt = now,
        };
        db.Add(org);
        db.Add(user);
        db.Add(new OrgMember { OrgId = org.Id, UserId = user.Id, Role = OrgRole.Owner, CreatedAt = now });

        try
        {
            await db.SaveChangesAsync(); // single SaveChanges = one transaction
        }
        catch (DbUpdateException) // unique-violation fallback (concurrent duplicate email)
        {
            return Problem.Of(409, "Email in use", "A user with that email already exists.");
        }

        ctx.Response.Cookies.Append("ed_session", jwt.Issue(user.Id, org.Id), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });

        return Results.Created($"/api/v1/users/{user.Id}",
            new { id = user.Id, email = user.Email, displayName = user.DisplayName, orgId = org.Id });
    }

    private static async Task<IResult> Login(
        LoginRequest req, HttpContext ctx, EasyDocsDbContext db, IPasswordHasher hasher, JwtService jwt)
    {
        var email = req.Email?.Trim() ?? "";
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        // Uniform 401 whether the email is unknown, SSO-only (null hash), or the password is wrong.
        if (user?.PasswordHash is null || !hasher.Verify(req.Password ?? "", user.PasswordHash))
            return Problem.Of(401, "Invalid credentials", "Email or password is incorrect.");

        var member = await db.OrgMembers.FirstAsync(m => m.UserId == user.Id);

        ctx.Response.Cookies.Append("ed_session", jwt.Issue(user.Id, member.OrgId), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });

        return Results.Ok(new { id = user.Id, email = user.Email, displayName = user.DisplayName, orgId = member.OrgId });
    }

    private static async Task<IResult> Me(HttpContext ctx, EasyDocsDbContext db)
    {
        var userId = CurrentUser.UserId(ctx.User);
        var orgId = CurrentUser.OrgId(ctx.User);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        return user is null
            ? Problem.Of(401, "Invalid credentials", "User no longer exists.")
            : Results.Ok(new { id = user.Id, email = user.Email, displayName = user.DisplayName, orgId });
    }

    private static string Slugify(string name)
    {
        var slug = NonSlugChars().Replace(name.Trim().ToLowerInvariant().Replace(' ', '-'), "");
        slug = MultiHyphen().Replace(slug, "-").Trim('-');
        return slug.Length == 0 ? "org" : slug;
    }

    private static async Task<string> UniqueSlugAsync(EasyDocsDbContext db, string baseSlug)
    {
        var slug = baseSlug;
        while (await db.Organizations.AnyAsync(o => o.Slug == slug))
            slug = $"{baseSlug}-{Guid.NewGuid():N}"[..(baseSlug.Length + 5)];
        return slug;
    }

    [GeneratedRegex("[^a-z0-9-]")] private static partial Regex NonSlugChars();
    [GeneratedRegex("-{2,}")] private static partial Regex MultiHyphen();
}
