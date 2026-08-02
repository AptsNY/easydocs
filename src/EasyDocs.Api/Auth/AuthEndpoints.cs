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

    // The one definition of the session cookie's attributes, shared by every route that writes it
    // (register, login, invitation accept, logout). Deleting a cookie only works when the delete
    // presents the SAME Path/Secure/SameSite as the append — get that wrong and the browser keeps the
    // original, which is precisely how a "Sign out" button silently does nothing.
    public static CookieOptions SessionCookie => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
    };

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").WithTags("Auth");
        // Rate-limited (spec §11, see RateLimits.Auth): unthrottled, register is free org creation and
        // login is free credential stuffing, and both burn an Argon2id hash per request. The policy
        // keys on path as well as caller, so a register flood cannot exhaust the login budget.
        g.MapPost("/api/v1/auth/register", Register).RequireRateLimiting(RateLimits.Auth);
        g.MapPost("/api/v1/auth/login", Login).RequireRateLimiting(RateLimits.Auth);
        // Deliberately NOT RequireAuthorization: signing out has to work when the cookie is already
        // expired or corrupt, and a 401 there would strand the user signed-in-looking with no way out.
        g.MapPost("/api/v1/auth/logout", Logout);
        g.MapGet("/api/v1/me", Me).RequireAuthorization();
        g.MapGet("/api/v1/orgs", MyOrgs).RequireAuthorization();
        g.MapPost("/api/v1/auth/switch-org", SwitchOrg).RequireAuthorization();
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
        db.Add(Audit.Event(org.Id, null, user.Id, "org.created", "org", org.Id.ToString(), new { name = org.Name, slug = org.Slug }));

        try
        {
            await db.SaveChangesAsync(); // single SaveChanges = one transaction
        }
        catch (DbUpdateException) // unique-violation fallback (concurrent duplicate email)
        {
            return Problem.Of(409, "Email in use", "A user with that email already exists.");
        }

        ctx.Response.Cookies.Append("ed_session", jwt.Issue(user.Id, org.Id), SessionCookie);

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

        // MFA (issue #10): a correct password is half a login. The challenge token can finish MFA
        // and nothing else — no org claim, so the default policy rejects it everywhere.
        if (user.TotpEnabledAt is not null)
            return Results.Ok(new { mfaRequired = true, mfaToken = jwt.IssueMfaChallenge(user.Id) });

        // A session carries exactly one org. Accepting an invitation can make a user a member of more
        // than one, so pick deterministically (oldest membership) rather than whatever the DB returns
        // first. Anyone with several moves between them afterwards via POST /auth/switch-org.
        var member = await db.OrgMembers
            .Where(m => m.UserId == user.Id)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.OrgId)
            .FirstAsync();

        ctx.Response.Cookies.Append("ed_session", jwt.Issue(user.Id, member.OrgId), SessionCookie);

        return Results.Ok(new { id = user.Id, email = user.Email, displayName = user.DisplayName, orgId = member.OrgId });
    }

    // Until now "Sign out" only dropped the client's in-memory copy of /me: the httpOnly ed_session
    // cookie stayed in the browser and valid on the server, so pressing Back or reloading restored the
    // session. On a shared machine that is the whole point of the button failing to work.
    //
    // ponytail: clearing the cookie, not revoking the JWT — the token stays valid until it expires, so
    // anyone who already copied it out of the browser keeps it. Ceiling: a stolen token outlives the
    // sign-out. Upgrade path: a revoked-jti denylist in Postgres checked by the auth handler, once
    // there is a reason to pay a lookup per request.
    //
    // ponytail: no CSRF token on this route. SameSite=Lax already blocks the cookie on a cross-site
    // POST, so the worst a forged request achieves is signing someone out — annoyance, not data loss.
    private static IResult Logout(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete("ed_session", SessionCookie);
        return Results.NoContent();
    }

    public record SwitchOrgRequest(Guid OrgId);

    // Every organization this user belongs to, oldest membership first — the same order Login picks its
    // default from, so the list reads in the order the user acquired them.
    private static async Task<IResult> MyOrgs(HttpContext ctx, EasyDocsDbContext db)
    {
        var userId = CurrentUser.UserId(ctx.User);
        var currentOrgId = CurrentUser.OrgId(ctx.User);

        var orgs = await db.OrgMembers
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.OrgId)
            .Select(m => new
            {
                id = m.OrgId,
                name = db.Organizations.Where(o => o.Id == m.OrgId).Select(o => o.Name).First(),
                slug = db.Organizations.Where(o => o.Id == m.OrgId).Select(o => o.Slug).First(),
                myRole = m.Role.ToString(),
                current = m.OrgId == currentOrgId,
            })
            .ToListAsync(ctx.RequestAborted);

        return Results.Ok(new { items = orgs });
    }

    // Re-binds the session to another org the caller already belongs to. A session carries exactly one
    // org (spec §10.2), and Login deterministically picks the OLDEST membership — which meant an invited
    // colleague could work in the inviting org for exactly one session: accepting rebound their cookie,
    // but their next sign-in sent them back to the org their own registration created, with no way
    // across. That made every collaborative feature — shared documents, approvals, concurrent branches,
    // push-back review — unreachable after a single logout.
    //
    // Membership is re-checked here rather than trusted from the request: this endpoint mints a session
    // for an org, so an unverified orgId would be a straight cross-org escalation.
    private static async Task<IResult> SwitchOrg(
        SwitchOrgRequest req, HttpContext ctx, EasyDocsDbContext db, JwtService jwt)
    {
        var userId = CurrentUser.UserId(ctx.User);
        var member = await db.OrgMembers
            .FirstOrDefaultAsync(m => m.UserId == userId && m.OrgId == req.OrgId, ctx.RequestAborted);
        // 404, not 403: a non-member must not learn whether the org exists.
        if (member is null) return Problem.Of(404, "Not found", "Organization not found.");

        ctx.Response.Cookies.Append("ed_session", jwt.Issue(userId, member.OrgId), SessionCookie);
        return Results.Ok(new { orgId = member.OrgId, myRole = member.Role.ToString() });
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
