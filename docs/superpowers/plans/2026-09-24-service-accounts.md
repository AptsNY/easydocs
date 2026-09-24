# Service Accounts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Integrations get an identity of their own — a password-less, manager-owned "service account" user — so no person's password reset can revoke an integration's `ed_` token.

**Architecture:** A service account is a `Users` row with `ManagedBy` set (one nullable column). It reuses every existing authorization path unchanged (document membership, `DocumentAuthorization`, audit). New code is one endpoints file (create/list/mint/delete), one endpoint filter (`RequirePerson`) that refuses service callers on person-only routes, and ~8 small guards in existing handlers. Spec: `docs/superpowers/specs/2026-09-24-service-accounts-design.md`. RFC: AptsNY/easydocs#60.

**Tech Stack:** ASP.NET Core 10 minimal APIs, EF Core 10 + Npgsql (Postgres 16), xUnit + Testcontainers, React 19 + Playwright.

---

## Ground rules for every task

- Branch: `feat/service-accounts` (already exists, holds the spec). Work in `/Users/robertozuniga/Desktop/code/blm/easydocs`.
- `dotnet build easydocs.slnx` must produce **0 warnings** (`TreatWarningsAsErrors`).
- Tests need Docker running (Testcontainers Postgres). Run a filter with:
  `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~ServiceAccountTests"`
- **Known pre-existing failures:** 7 `S3ApiTests.*` / `S3BlobStoreTests.*` tests fail on `main` too. Ignore those; everything else must pass.
- Every commit uses `git commit -s` (DCO check) and ends with the trailer
  `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
- `dotnet ef` is installed globally (`export PATH="$PATH:$HOME/.dotnet/tools"` if not found).
- Match the surrounding comment style: comments explain *why*, not *what*. Problem responses always go through `Problem.Of(status, title, detail)`.

## File structure

| File | Responsibility |
|---|---|
| `src/EasyDocs.Api/Domain/User.cs` | add `ManagedBy` |
| `src/EasyDocs.Api/Data/EasyDocsDbContext.cs` | FK `ManagedBy → Users` RESTRICT |
| `src/EasyDocs.Api/Data/Migrations/*_ServiceAccounts.cs` | generated migration |
| `src/EasyDocs.Api/Auth/ServiceAccounts.cs` (new) | `ServiceDomain`, `IsServiceAsync`, `RequirePerson()` filter |
| `src/EasyDocs.Api/Auth/ServiceAccountEndpoints.cs` (new) | the 4 routes |
| `src/EasyDocs.Api/Program.cs` | map the new endpoints |
| `AuthEndpoints.cs`, `InvitationEndpoints.cs`, `TokenEndpoints.cs`, `MfaEndpoints.cs`, `Sharing/ShareEndpoints.cs` | apply `RequirePerson()` |
| `OrgEndpoints.cs`, `Documents/MemberEndpoints.cs`, `Approvals/ApprovalEndpoints.cs`, `PasswordResetEndpoints.cs`, `OidcEndpoints.cs`, `AuthEndpoints.cs` | guards + `managedBy` on list rows |
| `tests/EasyDocs.Api.Tests/ServiceAccountTests.cs` (new) | all backend tests |
| `web/src/api.ts`, `web/src/routes/Settings.tsx`, `web/src/components/MembersPanel.tsx`, `web/src/routes/Approvals.tsx` | UI |
| `web/e2e/reset-kills-integration-token.spec.ts` | companion e2e |
| `docs/superpowers/specs/2026-07-24-easydocs-v1-design.md` §10.1, `CHANGELOG.md`, `docs-site/docs/api/openapi/v1.json` | docs |

---

### Task 1: Schema — `Users.ManagedBy`

**Files:**
- Modify: `src/EasyDocs.Api/Domain/User.cs`
- Modify: `src/EasyDocs.Api/Data/EasyDocsDbContext.cs` (the `b.Entity<User>(e => { ... })` block, ~line 48)
- Create: `src/EasyDocs.Api/Data/Migrations/<timestamp>_ServiceAccounts.cs` (+ Designer, snapshot update — generated)

- [ ] **Step 1: Add the property** — in `User.cs`, after `CreatedAt`:

```csharp
    // Non-null = a service account (spec 2026-09-24): an integration's identity that can never sign in,
    // managed by this user. Only the manager may mint its tokens.
    public Guid? ManagedBy { get; set; }
```

- [ ] **Step 2: Configure the FK** — in `EasyDocsDbContext.cs` replace the `b.Entity<User>` block with:

```csharp
        b.Entity<User>(e =>
        {
            e.Property(x => x.Email).HasColumnType("citext");
            e.HasIndex(x => x.Email).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ManagedBy).OnDelete(R);
        });
```

(`R` is the file's existing `DeleteBehavior.Restrict` alias — confirm it is declared above this block; if the block precedes its declaration, use `DeleteBehavior.Restrict` literally.)

- [ ] **Step 3: Generate the migration**

Run: `dotnet ef migrations add ServiceAccounts -p src/EasyDocs.Api -o Data/Migrations`
Expected: three files changed under `Data/Migrations`; the `Up` adds a nullable `uuid` column `ManagedBy` to `Users`, an index on it, and an FK with `onDelete: ReferentialAction.Restrict`. Open the generated `Up` and confirm exactly that — nothing else.

- [ ] **Step 4: Build**

Run: `dotnet build easydocs.slnx`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Run an existing suite to prove the migration applies**

Run: `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~PasswordResetTests"`
Expected: `Passed! - Failed: 0, Passed: 18`

- [ ] **Step 6: Commit**

```bash
git add src/EasyDocs.Api/Domain/User.cs src/EasyDocs.Api/Data
git commit -s -m "feat(service-accounts): Users.ManagedBy column" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Shared helpers — `ServiceAccounts.cs`

**Files:**
- Create: `src/EasyDocs.Api/Auth/ServiceAccounts.cs`

- [ ] **Step 1: Write the file**

```csharp
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
```

- [ ] **Step 2: Build** — `dotnet build easydocs.slnx` → 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/EasyDocs.Api/Auth/ServiceAccounts.cs
git commit -s -m "feat(service-accounts): RequirePerson filter and IsServiceAsync" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: The four routes — create, list, mint, delete

**Files:**
- Create: `src/EasyDocs.Api/Auth/ServiceAccountEndpoints.cs`
- Modify: `src/EasyDocs.Api/Program.cs` (after `app.MapOrgEndpoints();`)
- Create: `tests/EasyDocs.Api.Tests/ServiceAccountTests.cs`

- [ ] **Step 1: Write the failing tests** — create `tests/EasyDocs.Api.Tests/ServiceAccountTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Domain;

namespace EasyDocs.Api.Tests;

public class ServiceAccountTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ServiceAccountTests(ApiFactory f) => _f = f;

    private record SvcDto(Guid UserId, string Name, string Email);
    private record TokenDto(Guid Id, string Token);
    private record IdDto(Guid Id);
    private record ManagerDto(Guid UserId, string DisplayName);
    private record SvcRowDto(Guid UserId, string Name, string Email, ManagerDto ManagedBy, int LiveTokens);

    private static async Task<SvcDto> CreateAsync(HttpClient c, string name = "docassemble")
    {
        var res = await c.PostAsJsonAsync("/api/v1/org/service-accounts", new { name });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<SvcDto>())!;
    }

    private static Task<HttpResponseMessage> MintAsync(HttpClient c, Guid svc, string name = "runtime") =>
        c.PostAsJsonAsync($"/api/v1/org/service-accounts/{svc}/tokens", new { name });

    private HttpClient Bearer(string token)
    {
        var client = _f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<HttpClient> SvcClientAsync(HttpClient manager, Guid svc)
    {
        var res = await MintAsync(manager, svc);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return Bearer((await res.Content.ReadFromJsonAsync<TokenDto>())!.Token);
    }

    private static async Task<Guid> CreateDocAsync(HttpClient c, string name = "Lease")
    {
        var res = await c.PostAsJsonAsync("/api/v1/documents", new { name });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<IdDto>())!.Id;
    }

    private static Task<HttpResponseMessage> AddMemberAsync(HttpClient c, Guid doc, string email, string role) =>
        c.PostAsJsonAsync($"/api/v1/documents/{doc}/members", new { email, role });

    [Fact]
    public async Task Owner_creates_a_service_account_that_joins_documents_and_authenticates()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        Assert.EndsWith("@service.invalid", svc.Email);

        var doc = await CreateDocAsync(owner.Client);
        var other = await CreateDocAsync(owner.Client, "Unshared");
        Assert.Equal(HttpStatusCode.Created, (await AddMemberAsync(owner.Client, doc, svc.Email, "Viewer")).StatusCode);

        var bot = await SvcClientAsync(owner.Client, svc.UserId);
        Assert.Equal(HttpStatusCode.OK, (await bot.GetAsync($"/api/v1/documents/{doc}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.GetAsync($"/api/v1/documents/{other}/versions")).StatusCode);
    }

    [Fact]
    public async Task The_service_account_cannot_sign_in()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        var res = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = svc.Email, password = "anything-at-all" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task A_plain_member_cannot_create_and_sees_an_empty_list()
    {
        var owner = await _f.RegisterAsync();
        await CreateAsync(owner.Client);
        var member = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        var res = await member.Client.PostAsJsonAsync("/api/v1/org/service-accounts", new { name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var list = await member.Client.GetFromJsonAsync<SvcRowDto[]>("/api/v1/org/service-accounts");
        Assert.Empty(list!);
    }

    [Fact]
    public async Task List_shows_manager_and_live_token_count()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        await SvcClientAsync(owner.Client, svc.UserId);

        var row = Assert.Single((await owner.Client.GetFromJsonAsync<SvcRowDto[]>("/api/v1/org/service-accounts"))!);
        Assert.Equal(svc.UserId, row.UserId);
        Assert.Equal(owner.UserId, row.ManagedBy.UserId);
        Assert.Equal(1, row.LiveTokens);
    }

    [Fact]
    public async Task Only_the_manager_may_mint_even_an_org_owner_may_not()
    {
        var owner = await _f.RegisterAsync();
        var admin = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Admin);
        var otherOwner = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Owner);
        var svc = await CreateAsync(admin.Client); // an Admin creates it, so the Admin manages it

        Assert.Equal(HttpStatusCode.Forbidden, (await MintAsync(owner.Client, svc.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await MintAsync(otherOwner.Client, svc.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await MintAsync(admin.Client, svc.UserId)).StatusCode);
    }

    [Fact]
    public async Task A_cross_org_service_account_is_404()
    {
        var a = await _f.RegisterAsync();
        var b = await _f.RegisterAsync();
        var svc = await CreateAsync(a.Client);
        Assert.Equal(HttpStatusCode.NotFound, (await MintAsync(b.Client, svc.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.DeleteAsync($"/api/v1/org/service-accounts/{svc.UserId}")).StatusCode);
    }

    [Fact]
    public async Task Delete_revokes_its_tokens_and_hands_its_own_documents_to_the_manager()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        var bot = await SvcClientAsync(owner.Client, svc.UserId);
        var created = await CreateDocAsync(bot, "Ingested"); // the bot is this document's sole Owner

        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/v1/org/service-accounts/{svc.UserId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await bot.GetAsync("/api/v1/me")).StatusCode);
        // The manager was not a member before; now they own it, so the document is not orphaned.
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync($"/api/v1/documents/{created}")).StatusCode);
    }

    [Fact]
    public async Task An_org_owner_who_is_not_the_manager_may_delete()
    {
        var owner = await _f.RegisterAsync();
        var admin = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Admin);
        var svc = await CreateAsync(admin.Client);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/v1/org/service-accounts/{svc.UserId}")).StatusCode);
    }
}
```

- [ ] **Step 2: Run to confirm they fail**

Run: `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~ServiceAccountTests"`
Expected: FAIL — every test gets 404 from the unmapped `/api/v1/org/service-accounts` route (the `/api/{**rest}` catch-all).

- [ ] **Step 3: Implement** — create `src/EasyDocs.Api/Auth/ServiceAccountEndpoints.cs`:

```csharp
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
                    .Select(x => new { userId = x.Id, displayName = x.DisplayName }).First(),
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
```

In `Program.cs`, after `app.MapOrgEndpoints();` add:

```csharp
app.MapServiceAccountEndpoints();
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~ServiceAccountTests"`
Expected: `Passed! - Failed: 0, Passed: 8`. If `The_service_account_cannot_sign_in` fails, stop — local login must already refuse null hashes (AuthEndpoints `Login`); do not add code there, investigate.

- [ ] **Step 5: Commit**

```bash
git add src/EasyDocs.Api/Auth/ServiceAccountEndpoints.cs src/EasyDocs.Api/Program.cs tests/EasyDocs.Api.Tests/ServiceAccountTests.cs
git commit -s -m "feat(service-accounts): create, list, manager-only mint, delete" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Person-only routes refuse service callers

**Files:**
- Modify: `src/EasyDocs.Api/Auth/AuthEndpoints.cs` (route map, `switch-org` line)
- Modify: `src/EasyDocs.Api/Auth/InvitationEndpoints.cs` (route map)
- Modify: `src/EasyDocs.Api/Auth/TokenEndpoints.cs` (route map)
- Modify: `src/EasyDocs.Api/Auth/MfaEndpoints.cs` (the four `/api/v1/account/mfa*` lines)
- Modify: `src/EasyDocs.Api/Sharing/ShareEndpoints.cs` (the share-link `Create` line)
- Test: `tests/EasyDocs.Api.Tests/ServiceAccountTests.cs`

- [ ] **Step 1: Add failing tests** — append inside `ServiceAccountTests`:

```csharp
    // switch-org mints a 7-day ed_session: a service token must never be able to trade itself for one.
    [Fact]
    public async Task A_service_token_cannot_reach_person_only_routes()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        var bot = await SvcClientAsync(owner.Client, svc.UserId);
        var doc = await CreateDocAsync(bot);
        var vid = await UploadAsync(bot, doc);

        var switched = await bot.PostAsJsonAsync("/api/v1/auth/switch-org", new { orgId = owner.OrgId });
        Assert.Equal(HttpStatusCode.Forbidden, switched.StatusCode);
        Assert.False(switched.Headers.Contains("Set-Cookie"));

        Assert.Equal(HttpStatusCode.Forbidden, (await bot.PostAsJsonAsync("/api/v1/tokens", new { name = "self", scopes = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.GetAsync("/api/v1/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.PostAsync("/api/v1/invitations/anything:accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.GetAsync("/api/v1/account/mfa")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.PostAsync("/api/v1/account/mfa/setup", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.PostAsJsonAsync($"/api/v1/versions/{vid}/share-links", new { })).StatusCode);
    }

    private record VersionDto(Guid VersionId);

    private static async Task<Guid> UploadAsync(HttpClient c, Guid doc)
    {
        var res = await c.PostAsync($"/api/v1/documents/{doc}/versions", TestAuth.DocxForm());
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<VersionDto>())!.VersionId;
    }
```

- [ ] **Step 2: Run** — `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~A_service_token_cannot_reach"` → FAIL (switch-org returns 200).

- [ ] **Step 3: Apply `RequirePerson()`** — append `.RequirePerson()` to each of these existing route registrations (no other change to them):

```csharp
// AuthEndpoints.cs
        g.MapPost("/api/v1/auth/switch-org", SwitchOrg).RequireAuthorization().RequirePerson();
// InvitationEndpoints.cs
        g.MapPost("/api/v1/invitations/{token}:accept", Accept).RequireAuthorization().RequirePerson();
// TokenEndpoints.cs — all three
        g.MapPost("/api/v1/tokens", Create).RequireAuthorization().RequireRateLimiting(RateLimits.TokenMint).RequirePerson();
        g.MapGet("/api/v1/tokens", List).RequireAuthorization().RequirePerson();
        g.MapDelete("/api/v1/tokens/{id:guid}", Revoke).RequireAuthorization().RequirePerson();
// MfaEndpoints.cs — the four /api/v1/account/mfa* routes (NOT /api/v1/auth/login/mfa)
        g.MapGet("/api/v1/account/mfa", Status).RequireAuthorization().RequirePerson();
        g.MapPost("/api/v1/account/mfa/setup", Setup).RequireAuthorization().RequirePerson();
        g.MapPost("/api/v1/account/mfa/enable", Enable).RequireAuthorization().RequirePerson();
        g.MapPost("/api/v1/account/mfa/disable", Disable).RequireAuthorization().RequirePerson();
// ShareEndpoints.cs — create only
        g.MapPost("/api/v1/versions/{vid:guid}/share-links", Create).RequireAuthorization().RequirePerson();
```

- [ ] **Step 4: Run** — the new test passes; then run `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~Token|FullyQualifiedName~Mfa|FullyQualifiedName~Share|FullyQualifiedName~Invitation|FullyQualifiedName~AuthSession"` → all pass (persons are unaffected).

- [ ] **Step 5: Commit**

```bash
git add src/EasyDocs.Api tests/EasyDocs.Api.Tests/ServiceAccountTests.cs
git commit -s -m "feat(service-accounts): refuse service callers on session-minting and person-only routes" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Guards in existing handlers

**Files:**
- Modify: `src/EasyDocs.Api/Auth/OrgEndpoints.cs` (`UpdateRole`, `Remove`)
- Modify: `src/EasyDocs.Api/Documents/MemberEndpoints.cs` (`Add`, `Update`)
- Modify: `src/EasyDocs.Api/Approvals/ApprovalEndpoints.cs` (`Request`)
- Modify: `src/EasyDocs.Api/Auth/PasswordResetEndpoints.cs` (`OnAnotherTeamAsync`)
- Modify: `src/EasyDocs.Api/Auth/AuthEndpoints.cs` (`Register`)
- Modify: `src/EasyDocs.Api/Auth/OidcEndpoints.cs` (`Complete`)
- Test: `tests/EasyDocs.Api.Tests/ServiceAccountTests.cs`

- [ ] **Step 1: Add failing tests** — append inside `ServiceAccountTests`:

```csharp
    [Fact]
    public async Task A_service_account_is_capped_at_editor_on_documents()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        var doc = await CreateDocAsync(owner.Client);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddMemberAsync(owner.Client, doc, svc.Email, "Owner")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddMemberAsync(owner.Client, doc, svc.Email, "Editor")).StatusCode);
        var promote = await owner.Client.PatchAsJsonAsync($"/api/v1/documents/{doc}/members/{svc.UserId}", new { role = "Owner" });
        Assert.Equal(HttpStatusCode.BadRequest, promote.StatusCode);
    }

    [Fact]
    public async Task A_service_token_cannot_mint_invitations_even_on_its_own_document()
    {
        var owner = await _f.RegisterAsync();
        var colleague = await _f.SeedOrgUserAsync(owner.OrgId);
        var svc = await CreateAsync(owner.Client);
        var bot = await SvcClientAsync(owner.Client, svc.UserId);
        var doc = await CreateDocAsync(bot);
        Assert.Equal(HttpStatusCode.Forbidden, (await AddMemberAsync(bot, doc, "outsider@example.com", "Viewer")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddMemberAsync(bot, doc, colleague.Email, "Viewer")).StatusCode);
    }

    [Fact]
    public async Task Org_member_routes_refuse_to_treat_a_service_account_as_a_person()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.Client.PatchAsJsonAsync($"/api/v1/org/members/{svc.UserId}", new { role = "Admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.Client.DeleteAsync($"/api/v1/org/members/{svc.UserId}")).StatusCode);
        // Resetting it is already refused: it has no password.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.Client.PostAsync($"/api/v1/org/members/{svc.UserId}/password-reset", null)).StatusCode);
    }

    [Fact]
    public async Task A_manager_cannot_be_removed_from_the_org_while_managing()
    {
        var owner = await _f.RegisterAsync();
        var admin = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Admin);
        var svc = await CreateAsync(admin.Client);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.Client.DeleteAsync($"/api/v1/org/members/{admin.UserId}")).StatusCode);
        await owner.Client.DeleteAsync($"/api/v1/org/service-accounts/{svc.UserId}");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/v1/org/members/{admin.UserId}")).StatusCode);
    }

    [Fact]
    public async Task A_service_account_cannot_be_an_approver()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        var doc = await CreateDocAsync(owner.Client);
        var vid = await UploadAsync(owner.Client, doc);
        await AddMemberAsync(owner.Client, doc, svc.Email, "Viewer");
        (await owner.Client.PostAsJsonAsync($"/api/v1/versions/{vid}/publish", new { kind = "minor" })).EnsureSuccessStatusCode();

        var res = await owner.Client.PostAsJsonAsync($"/api/v1/versions/{vid}/approvals", new { approverIds = new[] { svc.UserId } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Register_refuses_the_service_domain()
    {
        var res = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "svc-x-12345678@service.invalid", displayName = "X", password = "pw-at-least-12", orgName = "X",
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // OnAnotherTeamAsync counts members per org; a service account in your personal org must not make
    // you "active on another team" and so un-resettable from the team you actually work in.
    [Fact]
    public async Task A_service_account_in_your_personal_org_keeps_you_resettable_from_a_team()
    {
        var team = await _f.RegisterAsync();
        var person = await _f.RegisterAsync(); // owns their personal org
        var invite = await team.Client.PostAsJsonAsync("/api/v1/org/members", new { email = person.Email, role = "Member" });
        var token = (await invite.Content.ReadFromJsonAsync<InviteDto>())!.InvitationToken;
        (await person.Client.PostAsync($"/api/v1/invitations/{token}:accept", null)).EnsureSuccessStatusCode();

        await CreateAsync(person.Client); // person.Client's JWT is still bound to their personal org

        Assert.Equal(HttpStatusCode.OK, (await team.Client.PostAsync($"/api/v1/org/members/{person.UserId}/password-reset", null)).StatusCode);
    }

    private record InviteDto(string InvitationToken);
```

Before running, confirm the publish request body in `src/EasyDocs.Api/Publishing/PublishEndpoints.cs` (the record bound by `POST /versions/{vid}/publish`) and adjust `new { kind = "minor" }` to its real field name/value if it differs.

- [ ] **Step 2: Run** — `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~ServiceAccountTests"` → the 7 new tests FAIL.

- [ ] **Step 3: Implement the guards**

`OrgEndpoints.UpdateRole` — right after the `target is null` 404 line:

```csharp
        if (await ServiceAccounts.IsServiceAsync(db, uid, ctx.RequestAborted))
            return Problem.Of(409, "Service account", "A service account is always a Member; its reach is its document roles.");
```

`OrgEndpoints.Remove` — right after the `target is null` 404 line:

```csharp
        // Plain removal would drop only the OrgMembers row, and DocumentAuthorization never reads it —
        // the tokens would keep full document access while vanishing from every list.
        if (await ServiceAccounts.IsServiceAsync(db, uid, ctx.RequestAborted))
            return Problem.Of(409, "Service account", $"Use DELETE /api/v1/org/service-accounts/{uid}.");
        // A service account nobody can mint for, whose documents nobody can reach, could never be cleaned up.
        if (await db.Users.AnyAsync(u => u.ManagedBy == uid && db.OrgMembers.Any(m => m.OrgId == org.Id && m.UserId == u.Id), ctx.RequestAborted))
            return Problem.Of(409, "Manages service accounts", "Delete the service accounts this member manages first.");
```

`MemberEndpoints.Add` — inside the `if (user is not null && inOrg)` branch, as its first statement:

```csharp
            // Ownership of a document is only ever its own creation (handed to its manager on delete).
            if (role == DocRole.Owner && user.ManagedBy is not null)
                return Problem.Of(400, "Invalid request", "A service account can be at most an Editor.");
```

and immediately before the invitation branch (the `var token = Base64Url...` line):

```csharp
        // An invitation hands out a raw token; an integration must not mint them, even on its own document.
        if (await Auth.ServiceAccounts.IsServiceAsync(db, CurrentUser.UserId(ctx.User), ctx.RequestAborted))
            return Problem.Of(403, "Forbidden", "A service account cannot invite people.");
```

`MemberEndpoints.Update` — after the `member is null` 404 line:

```csharp
        if (role == DocRole.Owner && await Auth.ServiceAccounts.IsServiceAsync(db, uid, ctx.RequestAborted))
            return Problem.Of(400, "Invalid request", "A service account can be at most an Editor.");
```

`ApprovalEndpoints.Request` — after the `ids.Except(memberIds).Any()` 400:

```csharp
        // Respond authorizes on ApproverId alone, so a service approver would let its token holder
        // approve their own request.
        if (await db.Users.AnyAsync(u => ids.Contains(u.Id) && u.ManagedBy != null, ctx.RequestAborted))
            return Problem.Of(400, "Invalid request", "A service account cannot be an approver.");
```

`PasswordResetEndpoints.OnAnotherTeamAsync` — replace the body with:

```csharp
        db.OrgMembers.AnyAsync(m => m.UserId == uid && m.OrgId != thisOrg
            && db.OrgMembers.Count(x => x.OrgId == m.OrgId
                && db.Users.Any(u => u.Id == x.UserId && u.ManagedBy == null)) > 1, ct);
```

and add one line to the comment above it: `// Service accounts are not people and do not make an org a "team".`

`AuthEndpoints.Register` — after the required-fields 400:

```csharp
        if (email.EndsWith("@" + ServiceAccounts.ServiceDomain, StringComparison.OrdinalIgnoreCase))
            return Problem.Of(400, "Invalid request", "That email domain is reserved.");
```

`OidcEndpoints.Complete` — right after `var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);`:

```csharp
        // Defence in depth behind the reserved domain: no IdP should ever assert it, but refuse if one does.
        if (user?.ManagedBy is not null)
            return Problem.Of(403, "SSO sign-in failed", "Service accounts cannot sign in.");
```

- [ ] **Step 4: Run** — `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~ServiceAccountTests|FullyQualifiedName~PasswordReset|FullyQualifiedName~Members|FullyQualifiedName~OrgEndpoint|FullyQualifiedName~Approval|FullyQualifiedName~Oidc|FullyQualifiedName~Auth"` → all pass.

- [ ] **Step 5: Commit**

```bash
git add src/EasyDocs.Api tests/EasyDocs.Api.Tests/ServiceAccountTests.cs
git commit -s -m "feat(service-accounts): editor cap, no invitations, no approvals, org-member and reset-count guards" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: List shapes — `managedBy` on member rows, service tokens visible to their managers

**Files:**
- Modify: `src/EasyDocs.Api/Auth/OrgEndpoints.cs` (`ListMembers` projection)
- Modify: `src/EasyDocs.Api/Documents/MemberEndpoints.cs` (`List` projection)
- Modify: `src/EasyDocs.Api/Auth/TokenEndpoints.cs` (`VisibleAsync`, `List` projection, the comment above `VisibleAsync`)
- Test: `tests/EasyDocs.Api.Tests/ServiceAccountTests.cs`

- [ ] **Step 1: Add failing tests** — append inside `ServiceAccountTests`:

```csharp
    private record MemberRowDto(Guid UserId, ManagerDto? ManagedBy);
    private record ServiceRefDto(Guid UserId, string Name);
    private record TokenRowDto(Guid Id, string ServiceName, ServiceRefDto? ServiceAccount);

    [Fact]
    public async Task Member_lists_say_who_manages_a_service_account()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        var doc = await CreateDocAsync(owner.Client);
        await AddMemberAsync(owner.Client, doc, svc.Email, "Viewer");

        var org = (await owner.Client.GetFromJsonAsync<MemberRowDto[]>("/api/v1/org/members"))!;
        Assert.Null(org.Single(m => m.UserId == owner.UserId).ManagedBy);
        Assert.Equal(owner.UserId, org.Single(m => m.UserId == svc.UserId).ManagedBy!.UserId);

        var onDoc = (await owner.Client.GetFromJsonAsync<MemberRowDto[]>($"/api/v1/documents/{doc}/members"))!;
        Assert.Equal(owner.UserId, onDoc.Single(m => m.UserId == svc.UserId).ManagedBy!.UserId);
    }

    [Fact]
    public async Task An_org_owner_can_find_and_revoke_one_leaked_service_token()
    {
        var owner = await _f.RegisterAsync();
        var admin = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Admin);
        var svc = await CreateAsync(admin.Client);
        var bot = await SvcClientAsync(admin.Client, svc.UserId);

        var row = (await owner.Client.GetFromJsonAsync<TokenRowDto[]>("/api/v1/tokens"))!
            .Single(t => t.ServiceAccount?.UserId == svc.UserId);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/v1/tokens/{row.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bot.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task A_plain_member_does_not_see_service_tokens()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        await SvcClientAsync(owner.Client, svc.UserId);
        var member = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        Assert.Empty((await member.Client.GetFromJsonAsync<TokenRowDto[]>("/api/v1/tokens"))!);
    }
```

- [ ] **Step 2: Run** — the 3 new tests FAIL (`ManagedBy`/`ServiceAccount` deserialize null; the Owner's token list is missing the Admin-managed token).

- [ ] **Step 3: Implement**

In **both** `OrgEndpoints.ListMembers` and `MemberEndpoints.List`, add one property to the anonymous projection, after `displayName = u.DisplayName,`:

```csharp
                managedBy = db.Users.Where(x => x.Id == u.ManagedBy)
                    .Select(x => new { userId = x.Id, displayName = x.DisplayName }).FirstOrDefault(),
```

In `TokenEndpoints.VisibleAsync`, replace the `return` line with:

```csharp
        // Service-account tokens (owned by a Users row with ManagedBy set) belong to its manager and to
        // whoever runs the org. Listing and revoking grant nothing, so Owner/Admin may; minting stays with
        // the manager on POST /api/v1/org/service-accounts/{uid}/tokens.
        return db.ApiTokens.Where(t => t.OrgId == orgId && (t.UserId == userId
            || (manages && t.UserId == null)
            || db.Users.Any(u => u.Id == t.UserId && u.ManagedBy != null && (manages || u.ManagedBy == userId))));
```

and update the comment block above `VisibleAsync`: replace the paragraph beginning `ApiToken.UserId is nullable for org-level service accounts` with:

```csharp
    // Service accounts (spec 2026-09-24) are Users rows with ManagedBy set; their tokens are visible to
    // their manager and to Owner/Admin — the pair OrgEndpoints gates org management on. UserId == null
    // tokens (the v1 schema's sketch, minted by nothing today) stay Owner/Admin-visible for the same reason.
```

In `TokenEndpoints.List`, add after `serviceName = t.ServiceName,`:

```csharp
                serviceAccount = db.Users.Where(u => u.Id == t.UserId && u.ManagedBy != null)
                    .Select(u => new { userId = u.Id, name = u.DisplayName }).FirstOrDefault(),
```

- [ ] **Step 4: Run** — `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~ServiceAccountTests|FullyQualifiedName~Token|FullyQualifiedName~Members|FullyQualifiedName~OrgEndpoint"` → all pass.

- [ ] **Step 5: Commit**

```bash
git add src/EasyDocs.Api tests/EasyDocs.Api.Tests/ServiceAccountTests.cs
git commit -s -m "feat(service-accounts): managedBy on member rows; service tokens listable and revocable by manager and Owner/Admin" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: The decisive test — a reset of the manager does not touch the integration

**Files:**
- Test: `tests/EasyDocs.Api.Tests/ServiceAccountTests.cs`

- [ ] **Step 1: Add the test** — append inside `ServiceAccountTests`:

```csharp
    private record ResetDto(string Token);

    // The reason this feature exists: a person's password reset revokes THEIR tokens, never the
    // integration's.
    [Fact]
    public async Task Resetting_the_managers_password_leaves_the_service_token_working()
    {
        var owner = await _f.RegisterAsync();
        var svc = await CreateAsync(owner.Client);
        var doc = await CreateDocAsync(owner.Client);
        await AddMemberAsync(owner.Client, doc, svc.Email, "Viewer");
        var bot = await SvcClientAsync(owner.Client, svc.UserId);
        var personal = await _f.PatClientAsync(owner.Client);

        var mint = await owner.Client.PostAsync($"/api/v1/org/members/{owner.UserId}/password-reset", null);
        var link = (await mint.Content.ReadFromJsonAsync<ResetDto>())!.Token;
        (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/password-reset:complete",
            new { token = link, password = "a-brand-new-password" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await personal.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bot.GetAsync($"/api/v1/documents/{doc}/versions")).StatusCode);
    }
```

- [ ] **Step 2: Run** — `dotnet test tests/EasyDocs.Api.Tests --filter "FullyQualifiedName~Resetting_the_managers_password"` → PASS (no new code needed; `Complete` only revokes `UserId == reset.UserId`). If it fails, the design is broken — stop and report.

- [ ] **Step 3: Commit**

```bash
git add tests/EasyDocs.Api.Tests/ServiceAccountTests.cs
git commit -s -m "test(service-accounts): the manager's password reset does not revoke the service token" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: Web UI

**Files:**
- Modify: `web/src/api.ts` (`Member`, `OrgMember`, `ApiTokenRow` types; add `ServiceAccount`)
- Modify: `web/src/routes/Settings.tsx`
- Modify: `web/src/components/MembersPanel.tsx`
- Modify: `web/src/routes/Approvals.tsx`

- [ ] **Step 1: Types** — in `web/src/api.ts`:

Add to both `Member` and `OrgMember`:

```ts
  // Non-null = a service account (an integration's identity), managed by this person.
  managedBy: { userId: string; displayName: string } | null
```

Add to `ApiTokenRow`:

```ts
  // Set when the token belongs to a service account this caller manages or, as Owner/Admin, can revoke.
  serviceAccount: { userId: string; name: string } | null
```

Add a new type after `OrgMember`:

```ts
// GET /api/v1/org/service-accounts — BARE array. Owner/Admin see all; anyone else sees what they manage.
export type ServiceAccount = {
  userId: string
  name: string
  email: string
  managedBy: { userId: string; displayName: string }
  liveTokens: number
  lastUsedAt: string | null
  createdAt: string
}
```

- [ ] **Step 2: Settings — state and load.** Add `type ServiceAccount` to the `../api` import. Add state next to `minted`:

```tsx
  const [services, setServices] = useState<ServiceAccount[]>([])
  const [svcMinted, setSvcMinted] = useState<{ name: string; token: string } | null>(null)
```

Change `load` to fetch it too:

```tsx
    const [ts, ms, ss] = await Promise.all([
      api.get<ApiTokenRow[]>('/api/v1/tokens'),
      api.get<OrgMember[]>('/api/v1/org/members'),
      api.get<ServiceAccount[]>('/api/v1/org/service-accounts'),
    ])
```

and after `setMembers(ms)` add `setServices(ss)`.

Add handlers after `createToken`:

```tsx
  const createService = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = e.currentTarget
    const name = String(new FormData(form).get('name') ?? '').trim()
    if (!name) return
    void act(async () => {
      await api.post('/api/v1/org/service-accounts', { name })
      form.reset()
    })
  }

  // Same one-shot rule as a personal token: only the hash is stored.
  const mintService = (s: ServiceAccount) =>
    void act(async () => {
      const created = await api.post<{ id: string; token: string }>(
        `/api/v1/org/service-accounts/${s.userId}/tokens`,
        { name: `${s.name} token` },
      )
      setSvcMinted({ name: s.name, token: created.token })
    })
```

- [ ] **Step 3: Settings — token rows name their service account.** In the tokens `<li>`, replace `<span>{t.serviceName}</span>` with:

```tsx
              <span>
                {t.serviceName}
                {t.serviceAccount && <span className="muted"> — {t.serviceAccount.name} (service)</span>}
              </span>
```

- [ ] **Step 4: Settings — the Service accounts section.** Immediately after the closing `</section>` of the API tokens section, add:

```tsx
      {(canAdmin || services.length > 0) && (
        <section data-testid="service-accounts">
          <h3>Service accounts</h3>
          <p className="muted">
            An integration’s own identity. It cannot sign in and no one’s password reset affects it. Only
            its manager can create its tokens; add it to documents by its email, like a person.
          </p>
          {canAdmin && (
            <details className="disclose" data-testid="new-service-account">
              <summary>New service account</summary>
              <form className="stack" onSubmit={createService}>
                <label className="visually-hidden" htmlFor="service-name">
                  Service account name
                </label>
                <input id="service-name" name="name" placeholder="Service account name" required />
                <button type="submit">Create service account</button>
              </form>
            </details>
          )}
          {svcMinted && (
            <div className="invitation" role="status">
              <p>Copy the token for “{svcMinted.name}” now — it is shown once and cannot be recovered.</p>
              <code data-testid="service-token-value">{svcMinted.token}</code>
            </div>
          )}
          <ul className="rows">
            {services.map((s) => (
              <li key={s.userId} data-testid="service-account-row" data-name={s.name}>
                <span>
                  {s.name} <span className="muted">managed by {s.managedBy.displayName}</span>
                </span>
                <code data-testid="service-account-email">{s.email}</code>
                <span className="muted">
                  {s.liveTokens} token{s.liveTokens === 1 ? '' : 's'}
                  {s.lastUsedAt && ` · last used ${new Date(s.lastUsedAt).toLocaleDateString()}`}
                </span>
                {s.managedBy.userId === me?.id && (
                  <button type="button" className="link" onClick={() => mintService(s)}>
                    New token
                  </button>
                )}
                <button
                  type="button"
                  className="link danger"
                  aria-label={`Remove service account ${s.name}`}
                  onClick={() => void act(() => api.del(`/api/v1/org/service-accounts/${s.userId}`))}
                >
                  Remove
                </button>
              </li>
            ))}
          </ul>
        </section>
      )}
```

- [ ] **Step 5: Settings — org roster.** In the org member row, change the name rendering so a service account says so, and hide the role select, reset and remove controls for it. Find the `org-member-row` `<li>`; wrap the existing role-control block, the reset-password block and the remove block each in `{!m.managedBy && (...)}` (they are already conditional — add `!m.managedBy &&` to each existing condition), and after the display name add:

```tsx
                {m.managedBy && <span className="muted"> (service, managed by {m.managedBy.displayName})</span>}
```

- [ ] **Step 6: MembersPanel.** After `{m.displayName} <span className="muted">{m.email}</span>` (line ~61) add the same `(service, managed by …)` span. In the add-member role `<select>`, nothing changes — the API's 400 on Owner surfaces through the existing error display.

- [ ] **Step 7: Approvals picker.** In `Approvals.tsx`, change `{members.map((m) => (` inside the `approvers` fieldset to `{members.filter((m) => !m.managedBy).map((m) => (` and add to the comment above it: `Service accounts are left out: the API refuses them as approvers.`

- [ ] **Step 8: Typecheck and lint**

Run: `cd web && npx tsc -b && npx oxlint src e2e`
Expected: no errors (the two pre-existing `only-export-components` warnings are fine).

- [ ] **Step 9: Commit**

```bash
git add web/src
git commit -s -m "feat(service-accounts): Settings section, member labels, approver picker excludes services" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: e2e — the integration survives its manager's reset

**Files:**
- Modify: `web/e2e/reset-kills-integration-token.spec.ts`

- [ ] **Step 1: Append the companion test**

```ts
test('a service-account token survives its manager\'s password reset', async ({
  signedIn: owner,
  account,
  request,
  browser,
}) => {
  const docId = await createDocument(owner, 'Lease Agreement')
  await uploadVersion(owner, docId, 'base.docx')

  await owner.goto('/settings')
  await disclose(owner.getByTestId('new-service-account'))
  await owner.getByPlaceholder('Service account name').fill('integration')
  await owner.getByRole('button', { name: 'Create service account' }).click()
  const row = owner.locator('[data-testid="service-account-row"][data-name="integration"]')
  const email = await row.getByTestId('service-account-email').innerText()
  await row.getByRole('button', { name: 'New token' }).click()
  const token = await owner.getByTestId('service-token-value').innerText()

  // Joined to the document like a person.
  const added = await owner.request.post(`/api/v1/documents/${docId}/members`, { data: { email, role: 'Viewer' } })
  expect(added.ok(), `add failed: ${added.status()} ${await added.text()}`).toBeTruthy()

  const lookup = async () =>
    (await request.get(`/api/v1/documents/${docId}/versions?order=desc&limit=100`, {
      headers: { Authorization: `Bearer ${token}` },
    })).status()
  expect(await lookup(), 'works before the reset').toBe(200)

  await owner.goto('/settings')
  await owner.getByRole('button', { name: `Reset the password for ${account.email}` }).click()
  const link = await owner.getByTestId('password-reset-url').innerText()
  const other = await browser.newContext()
  const page = await other.newPage()
  await page.goto(new URL(link).pathname)
  await page.getByLabel('New password', { exact: true }).fill('a-brand-new-password')
  await page.getByLabel('Confirm new password').fill('a-brand-new-password')
  await page.getByTestId('password-reset-submit').click()
  await expect(page).toHaveURL(/\/login$/)
  await other.close()

  expect(await lookup(), 'the service token is not the manager\'s').toBe(200)
})
```

- [ ] **Step 2: Run against a local stack**

```bash
docker run -d --rm --name ed-e2e-pg -e POSTGRES_PASSWORD=pg -e POSTGRES_DB=easydocs -p 55432:5432 postgres:16
ConnectionStrings__Postgres="Host=localhost;Port=55432;Database=easydocs;Username=postgres;Password=pg" \
  Jwt__Secret="$(openssl rand -base64 48)" BLOB_ROOT="$(mktemp -d)" ASPNETCORE_URLS=http://localhost:8080 \
  ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/EasyDocs.Api --no-launch-profile &
# wait until: curl -s localhost:8080/health  →  {"status":"ok"}
cd web && npx playwright test e2e/reset-kills-integration-token.spec.ts e2e/password-reset.spec.ts e2e/settings.spec.ts e2e/approvals.spec.ts --reporter=list
```

Expected: all pass. Then stop the API, `pkill -f vite`, `docker stop ed-e2e-pg`.

- [ ] **Step 3: Commit**

```bash
git add web/e2e/reset-kills-integration-token.spec.ts
git commit -s -m "test(e2e): a service-account token survives its manager's password reset" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: Docs — §10.1, CHANGELOG, OpenAPI snapshot

**Files:**
- Modify: `docs/superpowers/specs/2026-07-24-easydocs-v1-design.md` (§10.1, the `Org:` line)
- Modify: `CHANGELOG.md` (`[Unreleased]`)
- Modify: `docs-site/docs/api/openapi/v1.json` (regenerated)

- [ ] **Step 1: §10.1** — change the `Org:` line to:

```markdown
Org: `GET/PATCH /org`, `GET/POST /org/members`, `PATCH/DELETE /org/members/{uid}`, `GET/POST /org/service-accounts`, `POST /org/service-accounts/{uid}/tokens`, `DELETE /org/service-accounts/{uid}` (service accounts — see `2026-09-24-service-accounts-design.md`).
```

- [ ] **Step 2: CHANGELOG** — add a bullet under `[Unreleased]`, matching the existing bullets' voice:

```markdown
- **Service accounts: integrations get an identity no person can lose.** An org Owner or Admin creates
  one in **Settings → Service accounts**; it cannot sign in, joins documents by its email like a person
  (at most as Editor), and only the person who created it can mint its tokens. Resetting anyone's
  password — including its manager's — no longer breaks the integration running on it. Its tokens
  cannot mint sessions, invitations or share links, and it cannot be named an approver. RFC #60.
```

- [ ] **Step 3: Regenerate the OpenAPI snapshot**

Run: `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/EasyDocs.Api.Tests --filter Openapi_snapshot_in_docs_site_matches`
then: `dotnet test tests/EasyDocs.Api.Tests --filter Openapi_snapshot_in_docs_site_matches`
Expected: second run PASS; `git diff --stat docs-site` shows `v1.json` changed with the four new paths.

- [ ] **Step 4: Commit**

```bash
git add docs CHANGELOG.md docs-site/docs/api/openapi/v1.json
git commit -s -m "docs(service-accounts): §10.1, CHANGELOG, OpenAPI snapshot" -m "Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>"
```

---

### Task 11: Full verification and PR

- [ ] **Step 1: Full backend suite**

Run: `dotnet build easydocs.slnx && dotnet test tests/EasyDocs.Api.Tests`
Expected: 0 warnings; failures limited to the 7 known `S3ApiTests.*`/`S3BlobStoreTests.*`.

- [ ] **Step 2: Push and open the PR**

```bash
git push -u origin feat/service-accounts
gh pr create --title "feat: service accounts — integration identities no person can lose" --body "Closes #60. ..."
```

The PR body: link RFC #60 and the spec; list the four routes, the refusals, and the guards; paste the test counts from Step 1 and the e2e run; note the migration adds one nullable column. End with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
