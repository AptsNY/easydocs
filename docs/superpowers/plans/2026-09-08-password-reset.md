# Password Reset — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An org Owner mints a single-use, one-hour link that lets a locked-out member set a new password without a mailer.

**Architecture:** A `PasswordResets` table that mirrors `Invitation`, an authenticated mint under `/api/v1/org`, and an anonymous consume that takes the token in its **body**. Consuming revokes the target's `ed_` tokens, leaves MFA armed, and issues no session.

**Tech Stack:** ASP.NET Core 10 minimal APIs · EF Core / PostgreSQL 16 · xunit + Testcontainers · React 19 + react-router · Playwright.

Design: [`docs/superpowers/specs/2026-09-08-password-reset-design.md`](../specs/2026-09-08-password-reset-design.md) · RFC: [#49](https://github.com/AptsNY/easydocs/issues/49)

---

## Before You Start

Read the spec first. Three things in it look like over-caution and are not; each is easy to "simplify" straight back into a hole:

1. **An Admin may reset Members only, never an Owner or a peer Admin.** The gate you would copy from `Invite` (`caller.Role is not (OrgRole.Owner or OrgRole.Admin)`) is the wrong one — `UpdateRole` and `Remove` are Owner-only for less dangerous actions.
2. **The cross-org gate counts other orgs *that have more than one member*, not other orgs.** `Register` always creates an org and `Accept` requires a session, so every invited member is multi-org. A gate that counts orgs refuses everyone but founding Owners. There is a test whose whole job is to fail if someone simplifies this.
3. **The consume token goes in the request body, never the path.** `RateLimits.Auth` partitions on `$"{ctx.Request.Path}|{ClientKey(ctx)}"` (`src/EasyDocs.Api/Common/RateLimits.cs:86`). A token in the path gives every guess a fresh 1000-token bucket.

Repo conventions CI will fail you on:

- **Warnings are errors** (`Directory.Build.props`).
- **No test may skip** — CI greps for `Skipped: [1-9]`.
- **Every commit signed off** — `git commit -s`.
- Errors are always `problem+json` via `Problem.Of(status, title, detail)`.
- No navigation properties; all FKs `Restrict`.
- **`OpenApiTests.Openapi_snapshot_in_docs_site_matches_the_served_document` byte-compares the served OpenAPI document against the committed snapshot.** It goes red the moment you register a route, and it stays red until you regenerate. Regenerate with the command the test's own comment gives — nothing else produces a matching file, because the test strips `servers` and re-serializes with `WriteIndented`:

  ```bash
  UPDATE_OPENAPI_SNAPSHOT=1 dotnet test --filter Openapi_snapshot_in_docs_site_matches
  ```

Work on branch `feat/password-reset` (created; spec and plan already committed).

## File Structure

**Create:**

| File | Responsibility |
|---|---|
| `src/EasyDocs.Api/Domain/PasswordReset.cs` | The entity. Seven properties, no logic. |
| `src/EasyDocs.Api/Auth/PasswordResetEndpoints.cs` | Both endpoints and their gates. The only new server file with behaviour. |
| `tests/EasyDocs.Api.Tests/PasswordResetTests.cs` | Every backend test in this plan. |
| `web/src/routes/PasswordReset.tsx` | The public set-a-password page. |
| `web/e2e/password-reset.spec.ts` | The one end-to-end loop. |

**Modify:**

| File | Change |
|---|---|
| `src/EasyDocs.Api/Data/EasyDocsDbContext.cs` | `DbSet` + entity config |
| `src/EasyDocs.Api/Program.cs` | One `app.MapPasswordResetEndpoints();` line |
| `tests/EasyDocs.Api.Tests/TestAuth.cs` | Four seeding helpers |
| `web/src/App.tsx` | One public route + its import |
| `web/src/routes/Settings.tsx` | The per-member control on the **org** roster |
| `web/src/routes/Login.tsx` | One line of copy |
| `web/src/api.ts` | Two client calls |
| `SECURITY.md`, `CHANGELOG.md`, `docs-site/docs/user-guide.md` | Docs |
| `docs-site/docs/api/openapi/v1.json` | Regenerated snapshot (Tasks 2 and 4) |

The control goes on **`Settings.tsx`**, not `MembersPanel.tsx`. `MembersPanel` is the *per-document* roster — it reads `/api/v1/documents/{id}/members` and its roles are `DocRole` (`Owner | Editor | Viewer`), with no `Admin` and no `Member`. The org roster, `org.myRole`, `ROLES: OrgRole[]`, `canAdmin`/`isOwner`, and the "shown once" minted-PAT card this plan says to copy are all in `Settings.tsx`.

---

## Task 1: The entity, the schema, and the migration

**Files:**
- Create: `src/EasyDocs.Api/Domain/PasswordReset.cs`
- Modify: `src/EasyDocs.Api/Data/EasyDocsDbContext.cs`

- [ ] **Step 1: Write the entity**

```csharp
namespace EasyDocs.Api.Domain;

// An admin-issued, single-use password reset (spec 2026-09-08). Mirrors Invitation, minus the parts
// a reset does not need: no IssuedBy (the password_reset.issued audit event records the actor) and
// no RevokedAt (re-issuing stamps UsedAt on the outstanding row — one state machine, not two).
public class PasswordReset
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid OrgId { get; set; }

    // Capability token, hashed at rest like invitations and share links; the raw token is returned once.
    public string TokenHash { get; set; } = null!;

    // Non-nullable where Invitation.ExpiresAt is nullable: an invitation that never expires is a
    // convenience, a password reset that never expires is a permanent skeleton key.
    public DateTimeOffset ExpiresAt { get; set; }

    // Consumed OR superseded by a re-issue. Both mean "this link is dead".
    public DateTimeOffset? UsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
```

- [ ] **Step 2: Register it in the DbContext**

```csharp
public DbSet<PasswordReset> PasswordResets => Set<PasswordReset>();
```

And in `OnModelCreating`, next to the `Invitation` block:

```csharp
b.Entity<PasswordReset>(e =>
{
    e.HasIndex(x => x.TokenHash).IsUnique();
    e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(R);
    e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(R);
});
```

- [ ] **Step 3: Generate the migration**

```bash
dotnet ef migrations add AddPasswordResets --project src/EasyDocs.Api
```

Expected: **three** artifacts — `<ts>_AddPasswordResets.cs`, `<ts>_AddPasswordResets.Designer.cs`, and a modified `EasyDocsDbContextModelSnapshot.cs`.

- [ ] **Step 4: Read the generated migration before trusting it**

The `Up` method must contain exactly one `CreateTable` for `PasswordResets` and one unique `CreateIndex` on `TokenHash`. If it alters **any other table**, stop — something else drifted and must not ride along. (The model-snapshot file changing is normal and expected.)

- [ ] **Step 5: Verify it builds**

Run: `dotnet build -c Release`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 6: Commit**

```bash
git add src/EasyDocs.Api/Domain/PasswordReset.cs src/EasyDocs.Api/Data/
git commit -s -m "feat(auth): PasswordResets table"
```

---

## Task 2: The mint endpoint and its gates

**Files:**
- Create: `src/EasyDocs.Api/Auth/PasswordResetEndpoints.cs`, `tests/EasyDocs.Api.Tests/PasswordResetTests.cs`
- Modify: `src/EasyDocs.Api/Program.cs`, `docs-site/docs/api/openapi/v1.json`

- [ ] **Step 1: Write the failing authorization tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Domain;

namespace EasyDocs.Api.Tests;

// POST /api/v1/org/members/{uid}/password-reset — admin-issued reset links (spec 2026-09-08).
public class PasswordResetTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public PasswordResetTests(ApiFactory f) => _f = f;

    private record MintDto(string Token, string Url, DateTimeOffset ExpiresAt);

    private static Task<HttpResponseMessage> MintAsync(HttpClient c, Guid uid) =>
        c.PostAsync($"/api/v1/org/members/{uid}/password-reset", null);

    private static async Task<MintDto> MintOkAsync(HttpClient c, Guid uid)
    {
        var res = await MintAsync(c, uid);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<MintDto>())!;
    }

    [Fact]
    public async Task Member_may_not_mint()
    {
        var owner = await _f.RegisterAsync();
        var member = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        Assert.Equal(HttpStatusCode.Forbidden, (await MintAsync(member.Client, target.UserId)).StatusCode);
    }

    // The escalation gate. An Admin who could reset an Owner would sign in as them and self-promote,
    // which is why this endpoint is NOT gated like Invite.
    [Fact]
    public async Task Admin_may_reset_a_member_but_not_an_owner_or_a_peer_admin()
    {
        var owner = await _f.RegisterAsync();
        var admin = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Admin);
        var peer = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Admin);
        var member = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        Assert.Equal(HttpStatusCode.Forbidden, (await MintAsync(admin.Client, owner.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await MintAsync(admin.Client, peer.UserId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MintAsync(admin.Client, member.UserId)).StatusCode);
    }

    [Fact]
    public async Task Owner_may_reset_an_admin_and_an_owner()
    {
        var owner = await _f.RegisterAsync();
        var admin = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Admin);
        var coOwner = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Owner);

        var dto = await MintOkAsync(owner.Client, admin.UserId);
        Assert.NotEmpty(dto.Token);
        Assert.Equal($"/password-reset/{dto.Token}", dto.Url); // relative, like ShareEndpoints
        Assert.True(dto.ExpiresAt > DateTimeOffset.UtcNow);

        Assert.Equal(HttpStatusCode.OK, (await MintAsync(owner.Client, coOwner.UserId)).StatusCode);
    }

    // 404 not 403: a caller must not learn whether an account they cannot see exists (SwitchOrg's rule).
    [Fact]
    public async Task Target_in_another_org_is_404()
    {
        var owner = await _f.RegisterAsync();
        var stranger = await _f.RegisterAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await MintAsync(owner.Client, stranger.UserId)).StatusCode);
    }
}
```

- [ ] **Step 2: Run them and watch every one fail**

Run: `dotnet test --filter PasswordResetTests -v minimal`
Expected: all four FAIL with 404 — the route does not exist yet.

- [ ] **Step 3: Write the mint handler**

Create `src/EasyDocs.Api/Auth/PasswordResetEndpoints.cs`:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Documents;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Auth;

// Admin-issued password reset (spec 2026-09-08). easydocs has no mailer, so the link is delivered
// out of band by the admin who minted it — the same shape as an invitation.
public static class PasswordResetEndpoints
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public static void MapPasswordResetEndpoints(this WebApplication app)
    {
        // Registered standalone rather than on the /api/v1/org group so both halves of this feature
        // live in one file. Behaviourally identical: the default policy already requires the org claim.
        app.MapPost("/api/v1/org/members/{uid:guid}/password-reset", Mint)
            .RequireAuthorization().WithTags("Org").RequireRateLimiting(RateLimits.TokenMint);
    }

    // True when the target is a member of some OTHER org that has someone else in it. Deliberately not
    // "belongs to more than one org": Register always creates an org and Accept requires a session, so
    // every invited member is multi-org and that gate would refuse everyone but founding Owners. The
    // vestigial personal org is skipped; a real second team is not.
    private static Task<bool> OnAnotherTeamAsync(EasyDocsDbContext db, Guid uid, Guid thisOrg, CancellationToken ct) =>
        db.OrgMembers.AnyAsync(m => m.UserId == uid && m.OrgId != thisOrg
            && db.OrgMembers.Count(x => x.OrgId == m.OrgId) > 1, ct);

    private static async Task<IResult> Mint(Guid uid, HttpContext ctx, EasyDocsDbContext db)
    {
        var callerId = CurrentUser.UserId(ctx.User);
        var orgId = CurrentUser.OrgId(ctx.User);
        var ct = ctx.RequestAborted;

        var caller = await db.OrgMembers.FirstOrDefaultAsync(m => m.OrgId == orgId && m.UserId == callerId, ct);
        if (caller is null || caller.Role is not (OrgRole.Owner or OrgRole.Admin))
            return Problem.Of(403, "Forbidden", "Only an org owner or admin may issue a password reset.");

        // 404, not 403: a caller must not learn whether an account they cannot see exists.
        var target = await db.OrgMembers.FirstOrDefaultAsync(m => m.OrgId == orgId && m.UserId == uid, ct);
        if (target is null) return Problem.Of(404, "Not found", "Member not found.");

        // A reset is full account takeover — sharper than UpdateRole/Remove, which are Owner-only.
        // An Admin who could reset an Owner would sign in as them and promote themselves.
        if (caller.Role == OrgRole.Admin && target.Role is OrgRole.Owner or OrgRole.Admin)
            return Problem.Of(403, "Forbidden", "An admin may only reset the password of a member.");

        if (await OnAnotherTeamAsync(db, uid, orgId, ct))
            return Problem.Of(409, "Other organizations",
                "This account is a member of another organization, so it cannot be reset from here.");

        var user = await db.Users.FirstAsync(u => u.Id == uid, ct);
        if (user.PasswordHash is null)
            return Problem.Of(409, "No password", "This account signs in through SSO and has no password to reset.");

        var now = DateTimeOffset.UtcNow;

        // Re-issuing supersedes any outstanding link: the admin who sent the first one to the wrong
        // chat window expects it to stop working.
        await db.PasswordResets
            .Where(p => p.UserId == uid && p.UsedAt == null)
            .ForEachAsync(p => p.UsedAt = now, ct);

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        db.Add(new PasswordReset
        {
            UserId = uid,
            OrgId = orgId,
            TokenHash = MemberEndpoints.HashToken(token),
            ExpiresAt = now + Lifetime,
            CreatedAt = now,
        });
        db.Add(Audit.Event(orgId, null, callerId, "password_reset.issued", "user", uid.ToString(), null));
        await db.SaveChangesAsync(ct);

        // Relative, like ShareEndpoints: synthesizing an absolute URL means trusting Host or the
        // forwarded headers behind the documented reverse proxy.
        return Results.Ok(new { token, url = $"/password-reset/{token}", expiresAt = now + Lifetime });
    }
}
```

Register it in `Program.cs`, beside `app.MapInvitationEndpoints();`:

```csharp
app.MapPasswordResetEndpoints();
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter PasswordResetTests -v minimal`
Expected: all four PASS.

- [ ] **Step 5: Regenerate the OpenAPI snapshot**

Registering a route breaks `OpenApiTests` immediately. Do it now, not at the end, so no commit ships a red suite:

```bash
UPDATE_OPENAPI_SNAPSHOT=1 dotnet test --filter Openapi_snapshot_in_docs_site_matches
dotnet test --filter OpenApiTests -v minimal
```
Expected: the second run PASSES, and `git diff --stat` shows `docs-site/docs/api/openapi/v1.json` changed.

- [ ] **Step 6: Commit**

```bash
git add src/EasyDocs.Api/ tests/EasyDocs.Api.Tests/ docs-site/docs/api/openapi/v1.json
git commit -s -m "feat(auth): mint admin-issued password reset links"
```

---

## Task 3: The remaining mint gates

**Files:**
- Modify: `tests/EasyDocs.Api.Tests/PasswordResetTests.cs`, `tests/EasyDocs.Api.Tests/TestAuth.cs`

Task 2's handler already implements these; this task proves them.

Nothing here touches the consume endpoint. Every test in this task must be meaningful *now* — a test that posts to a route which does not exist yet gets a 404 from the router and passes vacuously, which is worse than no test. Consume, its helper, and the tests that need it all live in Task 4.

- [ ] **Step 1: Add the seeding helpers**

In `TestAuth.cs`, beside `SeedOrgUserAsync` (same kind of direct-to-database seeding; every namespace needed is already imported there):

```csharp
    // Adds an existing user to a second org. Registration always creates a NEW org, so this is the
    // only way to build the cross-team account the password-reset gate refuses.
    public static async Task AddOrgMemberAsync(this ApiFactory f, Guid orgId, Guid userId, OrgRole role)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        db.Add(new OrgMember { OrgId = orgId, UserId = userId, Role = role, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    // Simulates an SSO-provisioned account, which has no local password to reset.
    public static async Task ClearPasswordHashAsync(this ApiFactory f, Guid userId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null));
    }

    public static async Task ExpireResetsAsync(this ApiFactory f, Guid userId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        await db.PasswordResets.Where(p => p.UserId == userId && p.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    public static async Task ArmMfaAsync(this ApiFactory f, Guid userId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.TotpSecret, "AAAAAAAAAAAAAAAA")
            .SetProperty(u => u.TotpEnabledAt, DateTimeOffset.UtcNow));
    }
```

- [ ] **Step 2: Write the failing tests**

```csharp
    [Fact]
    public async Task Target_active_on_another_team_is_409()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        // A second org that has someone else in it: its registering owner, plus the target.
        var other = await _f.RegisterAsync();
        await _f.AddOrgMemberAsync(other.OrgId, target.UserId, OrgRole.Member);

        Assert.Equal(HttpStatusCode.Conflict, (await MintAsync(owner.Client, target.UserId)).StatusCode);
    }

    // The gate counts other orgs THAT HAVE SOMEONE ELSE IN THEM. This is the test that fails if anyone
    // simplifies it to "belongs to more than one org" — which would refuse every invited member alive.
    [Fact]
    public async Task Target_whose_only_other_org_is_their_own_solo_one_succeeds()
    {
        var owner = await _f.RegisterAsync();

        // Exactly what an invited colleague looks like: they registered (creating a solo personal org),
        // then joined this org.
        var invitee = await _f.RegisterAsync();
        await _f.AddOrgMemberAsync(owner.OrgId, invitee.UserId, OrgRole.Member);

        Assert.Equal(HttpStatusCode.OK, (await MintAsync(owner.Client, invitee.UserId)).StatusCode);
    }

    [Fact]
    public async Task Sso_only_target_is_409()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        await _f.ClearPasswordHashAsync(target.UserId);

        Assert.Equal(HttpStatusCode.Conflict, (await MintAsync(owner.Client, target.UserId)).StatusCode);
    }
```

- [ ] **Step 3: Run**

Run: `dotnet test --filter PasswordResetTests -v minimal`
Expected: all three new tests PASS — Task 2's handler already implements these gates, so this task is pinning behaviour rather than driving it.

- [ ] **Step 4: Commit**

```bash
git add tests/EasyDocs.Api.Tests/
git commit -s -m "test(auth): cross-team and SSO-only mint gates"
```

---

## Task 4: The consume endpoint

**Files:**
- Modify: `src/EasyDocs.Api/Auth/PasswordResetEndpoints.cs`, `tests/EasyDocs.Api.Tests/PasswordResetTests.cs`, `docs-site/docs/api/openapi/v1.json`

- [ ] **Step 1: Write the failing tests**

The helper first — every test below posts through it:

```csharp
    private record CompleteRequest(string Token, string Password);

    private Task<HttpResponseMessage> CompleteAsync(string token, string password) =>
        _f.CreateClient().PostAsJsonAsync("/api/v1/auth/password-reset:complete",
            new CompleteRequest(token, password));

    [Fact]
    public async Task Issuing_a_second_link_invalidates_the_first()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        var first = await MintOkAsync(owner.Client, target.UserId);
        await MintOkAsync(owner.Client, target.UserId);

        Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(first.Token, "brand-new-password")).StatusCode);
    }

    [Fact]
    public async Task Consume_sets_a_working_password()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        var dto = await MintOkAsync(owner.Client, target.UserId);

        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(dto.Token, "a-brand-new-password")).StatusCode);

        var login = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = target.Email, password = "a-brand-new-password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // The uniform 404 is only uniform if the BODIES match. Problem.Of takes a free-text detail, and
    // three helpfully-worded details would rebuild the oracle the shared status exists to remove.
    [Fact]
    public async Task Unknown_expired_and_used_tokens_are_byte_identical_404s()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        var used = (await MintOkAsync(owner.Client, target.UserId)).Token;
        await CompleteAsync(used, "first-password-set");

        var expired = (await MintOkAsync(owner.Client, target.UserId)).Token;
        await _f.ExpireResetsAsync(target.UserId);

        var bodies = new List<string>();
        foreach (var t in new[] { "totally-unknown-token", used, expired })
        {
            var res = await CompleteAsync(t, "another-password1");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            bodies.Add(await res.Content.ReadAsStringAsync());
        }
        Assert.Single(bodies.Distinct());
    }

    // The cross-team gate is re-evaluated at consume, because the target can join a team inside the
    // token's one-hour life. Untested, that check is one careless refactor from vanishing.
    [Fact]
    public async Task A_token_issued_before_the_target_joined_a_team_is_dead()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        var dto = await MintOkAsync(owner.Client, target.UserId);

        var other = await _f.RegisterAsync();
        await _f.AddOrgMemberAsync(other.OrgId, target.UserId, OrgRole.Member);

        // 404, not the mint's 409: the caller here is anonymous and must learn nothing about the account.
        Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(dto.Token, "a-brand-new-password")).StatusCode);

        // And the old password still works — nothing was changed.
        Assert.Equal(HttpStatusCode.OK, (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = target.Email, password = "pw-at-least-12" })).StatusCode);
    }

    [Fact]
    public async Task Short_password_is_400()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        var dto = await MintOkAsync(owner.Client, target.UserId);

        Assert.Equal(HttpStatusCode.BadRequest, (await CompleteAsync(dto.Token, "short")).StatusCode);
    }

    // The half of this feature with no visible UI, so it gets the explicit test.
    [Fact]
    public async Task Consume_revokes_the_targets_api_tokens()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        var pat = await _f.PatClientAsync(target.Client);
        Assert.Equal(HttpStatusCode.OK, (await pat.GetAsync("/api/v1/me")).StatusCode);

        var dto = await MintOkAsync(owner.Client, target.UserId);
        await CompleteAsync(dto.Token, "a-brand-new-password");

        Assert.Equal(HttpStatusCode.Unauthorized, (await pat.GetAsync("/api/v1/me")).StatusCode);
    }

    // A reset must not be an MFA bypass.
    [Fact]
    public async Task Mfa_still_challenges_after_a_reset()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        await _f.ArmMfaAsync(target.UserId);

        var dto = await MintOkAsync(owner.Client, target.UserId);
        await CompleteAsync(dto.Token, "a-brand-new-password");

        var login = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = target.Email, password = "a-brand-new-password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("mfaRequired", await login.Content.ReadAsStringAsync());
    }
```

And the rate-limit guard, which needs `using Microsoft.AspNetCore.Mvc.Testing;` and `using Microsoft.Extensions.Configuration;`. This follows `RateLimitTests.cs` — a second host on the same container with the limit dialled down, so it actually observes the 429:

```csharp
    // RateLimits.Auth partitions on request path. If the token ever moves back into the path, each
    // guess gets its own fresh bucket and the second request below returns 404 instead of 429.
    [Fact]
    public async Task Bogus_tokens_share_one_rate_limit_bucket()
    {
        using var host = _f.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:Auth:BurstLimit"] = "1",
                ["RateLimit:Auth:TokensPerPeriod"] = "1",
                ["RateLimit:Auth:ReplenishmentSeconds"] = "3600",
            })));
        var c = host.CreateClient();

        var first = await c.PostAsJsonAsync("/api/v1/auth/password-reset:complete",
            new CompleteRequest("bogus-token-a", "a-valid-length-pw"));
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);

        // A DIFFERENT token, same fixed path: it must draw from the bucket the first guess emptied.
        var second = await c.PostAsJsonAsync("/api/v1/auth/password-reset:complete",
            new CompleteRequest("bogus-token-b", "a-valid-length-pw"));
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test --filter PasswordResetTests -v minimal`
Expected: the new tests FAIL — the consume route does not exist.

- [ ] **Step 3: Write the consume handler**

Add to `MapPasswordResetEndpoints`:

```csharp
        // Anonymous: the caller is locked out by definition, so the token IS the credential. The token
        // travels in the BODY, not the path — RateLimits.Auth partitions on request path, so a
        // /{token}: route would hand every guess its own fresh bucket. Fixed path, one bucket per IP.
        app.MapPost("/api/v1/auth/password-reset:complete", Complete)
            .WithTags("Auth").RequireRateLimiting(RateLimits.Auth);
```

And the handler:

```csharp
    public record CompleteRequest(string? Token, string? Password);

    private static async Task<IResult> Complete(
        CompleteRequest req, EasyDocsDbContext db, IPasswordHasher hasher, CancellationToken ct)
    {
        if ((req.Password?.Length ?? 0) < 12)
            return Problem.Of(400, "Invalid request", "password must be at least 12 characters.");

        // ONE detail string for every failure mode. Unknown, expired, already-used and
        // now-on-another-team must be indistinguishable, or the uniform 404 is not uniform.
        var notFound = Problem.Of(404, "Not found", "Reset link not found or expired.");

        var now = DateTimeOffset.UtcNow;
        var hash = MemberEndpoints.HashToken(req.Token ?? "");
        var reset = await db.PasswordResets.FirstOrDefaultAsync(
            p => p.TokenHash == hash && p.UsedAt == null && p.ExpiresAt > now, ct);
        if (reset is null) return notFound;

        // Re-checked here, not only at mint: the target can join a second team inside the token's
        // one-hour life. 404 rather than the mint's 409 — this caller is anonymous.
        if (await OnAnotherTeamAsync(db, reset.UserId, reset.OrgId, ct)) return notFound;

        var user = await db.Users.FirstAsync(u => u.Id == reset.UserId, ct);
        user.PasswordHash = hasher.Hash(req.Password!);
        reset.UsedAt = now;

        // Loaded and stamped through the change tracker rather than ExecuteUpdateAsync, so all of
        // this lands in the single SaveChanges below (the repo idiom: one save = one transaction).
        await db.ApiTokens.Where(t => t.UserId == reset.UserId && t.RevokedAt == null)
            .ForEachAsync(t => t.RevokedAt = now, ct);

        // TotpEnabledAt is deliberately untouched: MFA survives a reset, so this is not a bypass.
        db.Add(Audit.Event(reset.OrgId, null, reset.UserId, "password_reset.consumed",
            "user", reset.UserId.ToString(), null));
        await db.SaveChangesAsync(ct);

        // No session cookie on purpose — the link was relayed through a chat window, and signing the
        // holder in would skip the MFA challenge the line above just preserved.
        return Results.NoContent();
    }
```

- [ ] **Step 4: Run the whole file**

Run: `dotnet test --filter PasswordResetTests -v minimal`
Expected: every test PASSES — including `Issuing_a_second_link_invalidates_the_first`, which is only meaningful now that a real consume route exists.

- [ ] **Step 5: Regenerate the snapshot again and run everything**

```bash
UPDATE_OPENAPI_SNAPSHOT=1 dotnet test --filter Openapi_snapshot_in_docs_site_matches
dotnet test -c Release
```
Expected: all pass, `Skipped: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/EasyDocs.Api/ tests/EasyDocs.Api.Tests/ docs-site/docs/api/openapi/v1.json
git commit -s -m "feat(auth): consume a reset link, revoking the target's ed_ tokens"
```

---

## Task 5: The web UI

**Files:**
- Create: `web/src/routes/PasswordReset.tsx`
- Modify: `web/src/api.ts`, `web/src/App.tsx`, `web/src/routes/Settings.tsx`, `web/src/routes/Login.tsx`

- [ ] **Step 1: Add the two API calls to `api.ts`**

```ts
export const mintPasswordReset = (userId: string) =>
  api.post<{ token: string; url: string; expiresAt: string }>(
    `/api/v1/org/members/${userId}/password-reset`, {})

// 204 No Content — <void> so this reads like the other 204 callers.
export const completePasswordReset = (token: string, password: string) =>
  api.post<void>('/api/v1/auth/password-reset:complete', { token, password })
```

- [ ] **Step 2: Write the public page**

`web/src/routes/PasswordReset.tsx` — read the token from the path with `useParams`, post it in the **body**, send the user to `/login` on success. Mirror `ShareLanding.tsx` for the "public page, no app chrome" treatment.

- [ ] **Step 3: Register the route outside the auth guard**

In `App.tsx`, add the import beside the other route imports:

```tsx
import PasswordReset from './routes/PasswordReset'
```

and the route beside `/s/:token`, **above** the `RequireAuth` element:

```tsx
{/* Public on purpose: the holder is locked out by definition, so this sits outside RequireAuth. */}
<Route path="/password-reset/:token" element={<PasswordReset />} />
```

- [ ] **Step 4: Add the control to the org roster in `Settings.tsx`**

Not `MembersPanel.tsx` — see *File Structure*. In the org member roster (~L193–L270), add a "Reset password" action per member, gated so the UI never offers an action the API will 403:

```tsx
const canReset = isOwner || (canAdmin && m.role === 'Member')
```

On success, show the returned `url` once with a copy button, reusing the minted-PAT card pattern already in this file (~L138–L157) and its "shown once and cannot be recovered" wording.

- [ ] **Step 5: Add the login-screen copy**

In `Login.tsx`, below the form — `muted` is this repo's class for secondary text (`index.css:226`); there is no `hint` rule:

```tsx
<p className="muted">
  Forgot your password? Ask an organization owner or admin for a reset link.
</p>
```

- [ ] **Step 6: Build and lint**

```bash
npm --prefix web run build
npm --prefix web run lint
```
Expected: both clean.

- [ ] **Step 7: Commit**

```bash
git add web/src/
git commit -s -m "feat(web): issue and redeem password reset links"
```

---

## Task 6: End-to-end coverage

**Files:**
- Create: `web/e2e/password-reset.spec.ts`

The only test exercising a route registered *outside* `RequireAuth` — a classic place for a later refactor to silently re-gate the page.

- [ ] **Step 1: Write the spec**

One test, the whole loop. **Who to reset matters:** Playwright has no database access, so the only member it can create is one who registers and then accepts an invitation. That person's other org is their solo personal one, so the cross-team gate lets them through — this is exactly the `Target_whose_only_other_org_is_their_own_solo_one_succeeds` case, and it is the reason that gate is shaped the way it is. (Under the rejected count-the-orgs gate, this test would have been unwritable.)

Steps: Owner registers → invites a second person → that person registers and accepts → Owner opens Settings, issues a reset for them, copies the link → sign out → open the link → set a new password → sign in with it.

Follow `web/e2e/fixtures.ts` for the registration helpers and `invitations.spec.ts` for the closest existing shape.

- [ ] **Step 2: Run it against the built image**

```bash
docker compose -f deploy/compose/docker-compose.yml up --build -d
npm --prefix web run e2e -- password-reset
```
Expected: PASS. The suite runs against the shipped container, not the Vite dev server — deliberate (ADR-12).

- [ ] **Step 3: Commit**

```bash
git add web/e2e/password-reset.spec.ts
git commit -s -m "test(e2e): password reset end to end"
```

---

## Task 7: Docs

**Files:**
- Modify: `SECURITY.md`, `CHANGELOG.md`, `docs-site/docs/user-guide.md`

The OpenAPI snapshot was already regenerated in Tasks 2 and 4.

- [ ] **Step 1: SECURITY.md — the gaps**

Under *Functional limitations*: a sole Owner has nobody to mint them a link; anyone active on a second team is refused by the cross-org gate; and a signed-in member still cannot change a password they already know.

Under *Security-relevant*: a reset revokes `ed_` tokens but not sessions, so a stolen cookie survives up to seven days — the same limitation ADR-9 already records for sign-out. Also state plainly that an Owner can take over any resettable member's account, which is inherent to admin-issued reset and is why the action is audited.

- [ ] **Step 2: CHANGELOG.md — `[Unreleased] → Added`**

Match the register of the surrounding entries: what it does and why it is shaped that way, not a feature bullet.

- [ ] **Step 3: user-guide.md**

How an Owner issues a reset, that the link is one hour and single-use, that they must deliver it themselves because easydocs sends no email, and who cannot be reset this way.

- [ ] **Step 4: Verify the snapshot really carries both routes**

```bash
grep -c '"/api/v1/auth/password-reset:complete"' docs-site/docs/api/openapi/v1.json
```
Expected: `1`.

- [ ] **Step 5: Commit**

```bash
git add SECURITY.md CHANGELOG.md docs-site/docs/user-guide.md
git commit -s -m "docs: password reset, and the gaps it leaves"
```

---

## Done When

- [ ] `dotnet test -c Release` passes with `Skipped: 0`
- [ ] `dotnet build -c Release` produces zero warnings
- [ ] `npm --prefix web run build` and `run lint` are clean
- [ ] `password-reset.spec.ts` passes against the built container image
- [ ] An Admin gets 403 resetting an Owner, and 200 resetting a Member
- [ ] An invited colleague (solo personal org + this org) **can** be reset; someone on a second real team cannot
- [ ] The three 404 bodies are byte-identical
- [ ] A consumed reset kills the target's `ed_` tokens and leaves MFA armed
- [ ] Every commit compiles on its own — no task leaves the test assembly unbuildable
- [ ] Every commit is signed off, and the PR references #49
