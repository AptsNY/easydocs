# Password Reset — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An org Owner mints a single-use, one-hour link that lets a locked-out member set a new password without a mailer.

**Architecture:** A `PasswordResets` table that mirrors `Invitation`, an authenticated mint on the existing `/api/v1/org` group, and an anonymous consume that takes the token in its **body**. Consuming revokes the target's `ed_` tokens, leaves MFA armed, and issues no session.

**Tech Stack:** ASP.NET Core 10 minimal APIs · EF Core / PostgreSQL 16 · xunit + Testcontainers · React 19 + react-router · Playwright.

Design: [`docs/superpowers/specs/2026-09-08-password-reset-design.md`](../specs/2026-09-08-password-reset-design.md) · RFC: [#49](https://github.com/AptsNY/easydocs/issues/49)

---

## Before You Start

Read the spec first — it records *why* two gates exist that look like over-caution, and both are easy to "simplify" back into security holes:

1. **An Admin may reset Members only, never an Owner or a peer Admin.** The gate you would copy from `Invite` (`caller.Role is not (OrgRole.Owner or OrgRole.Admin)`) is the wrong one. `UpdateRole` and `Remove` are Owner-only for strictly less dangerous actions.
2. **The consume token goes in the request body, never the path.** `RateLimits.Auth` partitions on `$"{ctx.Request.Path}|{ClientKey(ctx)}"` (`src/EasyDocs.Api/Common/RateLimits.cs:86`). A token in the path gives every guessed token a fresh 1000-token bucket.

Conventions this repo enforces, which CI will fail you on:

- **Warnings are errors** (`TreatWarningsAsErrors` in `Directory.Build.props`).
- **No test may skip.** CI greps for `Skipped: [1-9]` and fails.
- **Every commit signed off** — `git commit -s`.
- Errors are always `problem+json` via `Problem.Of(status, title, detail)`.
- Never add navigation properties; all FKs are `Restrict`.

Work on branch `feat/password-reset` (already created, spec already committed).

## File Structure

**Create:**

| File | Responsibility |
|---|---|
| `src/EasyDocs.Api/Domain/PasswordReset.cs` | The entity. Seven properties, no logic. |
| `src/EasyDocs.Api/Auth/PasswordResetEndpoints.cs` | Both endpoints and their gates. The only new server file with behaviour. |
| `tests/EasyDocs.Api.Tests/PasswordResetTests.cs` | Every test in this plan. |
| `web/src/routes/PasswordReset.tsx` | The public set-a-password page. |
| `web/e2e/password-reset.spec.ts` | The one end-to-end loop. |

**Modify:**

| File | Change |
|---|---|
| `src/EasyDocs.Api/Data/EasyDocsDbContext.cs` | `DbSet` + entity config (unique index on `TokenHash`, Restrict FKs) |
| `src/EasyDocs.Api/Program.cs` | One `app.MapPasswordResetEndpoints();` line |
| `web/src/App.tsx` | One public route, outside `RequireAuth` |
| `web/src/components/MembersPanel.tsx` | The per-member control |
| `web/src/routes/Login.tsx` | One line of copy |
| `web/src/api.ts` | Two client calls |
| `SECURITY.md` | Three documented gaps |
| `CHANGELOG.md` | `[Unreleased] → Added` |
| `docs-site/docs/user-guide.md` | How an admin issues a reset |
| `docs-site/docs/api/openapi/v1.json` | Regenerated snapshot |

Endpoint logic lives in one new file rather than being appended to `OrgEndpoints.cs` (195 lines, already covering org CRUD and the member roster) or `AuthEndpoints.cs` (212 lines). The two new handlers are one feature and belong together, even though their routes sit under two different prefixes.

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

Add the `DbSet` beside the others:

```csharp
public DbSet<PasswordReset> PasswordResets => Set<PasswordReset>();
```

And the configuration, next to the `Invitation` block in `OnModelCreating`:

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

Expected: a new pair of files under `src/EasyDocs.Api/Data/Migrations/`.

- [ ] **Step 4: Read the generated migration before trusting it**

Open the `Up` method. It must contain exactly one `CreateTable` for `PasswordResets` and one unique `CreateIndex` on `TokenHash`. If it contains **any** alteration to another table, stop — something else drifted and it must not ride along in this migration.

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
- Create: `src/EasyDocs.Api/Auth/PasswordResetEndpoints.cs`
- Modify: `src/EasyDocs.Api/Program.cs`
- Test: `tests/EasyDocs.Api.Tests/PasswordResetTests.cs`

The gates are the whole point of this task, so they get tested first and together.

- [ ] **Step 1: Write the failing authorization tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;

// POST /api/v1/org/members/{uid}/password-reset — admin-issued reset links (spec 2026-09-08).
public class PasswordResetTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public PasswordResetTests(ApiFactory f) => _f = f;

    private record MintDto(string Token, string Url, DateTimeOffset ExpiresAt);

    private static Task<HttpResponseMessage> MintAsync(HttpClient c, Guid uid) =>
        c.PostAsync($"/api/v1/org/members/{uid}/password-reset", null);

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
    public async Task Owner_may_reset_anyone_in_the_org()
    {
        var owner = await _f.RegisterAsync();
        var admin = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Admin);

        var res = await MintAsync(owner.Client, admin.UserId);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var dto = (await res.Content.ReadFromJsonAsync<MintDto>())!;
        Assert.NotEmpty(dto.Token);
        Assert.Equal($"/password-reset/{dto.Token}", dto.Url); // relative, like ShareEndpoints
        Assert.True(dto.ExpiresAt > DateTimeOffset.UtcNow);
    }

    // 404 not 403: a non-member must not learn whether the account exists (SwitchOrg's rule).
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
Expected: all four FAIL with 404 (the route does not exist yet).

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
        app.MapPost("/api/v1/org/members/{uid:guid}/password-reset", Mint)
            .RequireAuthorization().WithTags("Org").RequireRateLimiting(RateLimits.TokenMint);
    }

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

        // A reset hands over the whole account, not access to one org, so it is confined to accounts
        // that live in exactly one org. Re-checked at consume — the target can join another org inside
        // the token's one-hour life.
        if (await db.OrgMembers.CountAsync(m => m.UserId == uid, ct) > 1)
            return Problem.Of(409, "Multiple organizations",
                "This account belongs to more than one organization, so it cannot be reset from here.");

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

- [ ] **Step 5: Commit**

```bash
git add src/EasyDocs.Api/Auth/PasswordResetEndpoints.cs src/EasyDocs.Api/Program.cs tests/EasyDocs.Api.Tests/PasswordResetTests.cs
git commit -s -m "feat(auth): mint admin-issued password reset links"
```

---

## Task 3: The remaining mint gates

**Files:**
- Modify: `src/EasyDocs.Api/Auth/PasswordResetEndpoints.cs` (already written in Task 2 — these tests prove it)
- Test: `tests/EasyDocs.Api.Tests/PasswordResetTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task Multi_org_target_is_409()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        // Put the target in a second org, which is exactly what the gate refuses to reset across.
        var other = await _f.RegisterAsync();
        await _f.AddOrgMemberAsync(other.OrgId, target.UserId, OrgRole.Member);

        var res = await MintAsync(owner.Client, target.UserId);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Sso_only_target_is_409()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        await _f.ClearPasswordHashAsync(target.UserId);

        Assert.Equal(HttpStatusCode.Conflict, (await MintAsync(owner.Client, target.UserId)).StatusCode);
    }

    [Fact]
    public async Task Issuing_a_second_link_invalidates_the_first()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);

        var first = (await (await MintAsync(owner.Client, target.UserId)).Content.ReadFromJsonAsync<MintDto>())!;
        _ = await MintAsync(owner.Client, target.UserId);

        Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(first.Token, "brand-new-password")).StatusCode);
    }
```

- [ ] **Step 2: Add the two test helpers**

These belong in `TestAuth.cs` beside `SeedOrgUserAsync`, because they are the same kind of direct-to-database seeding:

```csharp
    // Adds an existing user to a second org. Registration always creates a NEW org, so this is the
    // only way to build the multi-org account the password-reset gate refuses.
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
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null));
    }
```

- [ ] **Step 3: Run**

Run: `dotnet test --filter PasswordResetTests -v minimal`
Expected: the two 409 tests PASS (Task 2's handler already implements them). `Issuing_a_second_link_invalidates_the_first` FAILS to compile until Task 4 adds `CompleteAsync` — that is expected; write it now and let Task 4 turn it green.

- [ ] **Step 4: Commit**

```bash
git add tests/EasyDocs.Api.Tests/
git commit -s -m "test(auth): mint gates for multi-org and SSO-only targets"
```

---

## Task 4: The consume endpoint

**Files:**
- Modify: `src/EasyDocs.Api/Auth/PasswordResetEndpoints.cs`
- Test: `tests/EasyDocs.Api.Tests/PasswordResetTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    private record CompleteRequest(string Token, string Password);

    private Task<HttpResponseMessage> CompleteAsync(string token, string password) =>
        _f.CreateClient().PostAsJsonAsync("/api/v1/auth/password-reset:complete",
            new CompleteRequest(token, password));

    [Fact]
    public async Task Consume_sets_a_working_password()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        var dto = (await (await MintAsync(owner.Client, target.UserId)).Content.ReadFromJsonAsync<MintDto>())!;

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

        var used = (await (await MintAsync(owner.Client, target.UserId)).Content.ReadFromJsonAsync<MintDto>())!.Token;
        await CompleteAsync(used, "first-password-set");

        var expired = (await (await MintAsync(owner.Client, target.UserId)).Content.ReadFromJsonAsync<MintDto>())!.Token;
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

    [Fact]
    public async Task Short_password_is_400()
    {
        var owner = await _f.RegisterAsync();
        var target = await _f.SeedOrgUserAsync(owner.OrgId, OrgRole.Member);
        var dto = (await (await MintAsync(owner.Client, target.UserId)).Content.ReadFromJsonAsync<MintDto>())!;

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

        var dto = (await (await MintAsync(owner.Client, target.UserId)).Content.ReadFromJsonAsync<MintDto>())!;
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

        var dto = (await (await MintAsync(owner.Client, target.UserId)).Content.ReadFromJsonAsync<MintDto>())!;
        await CompleteAsync(dto.Token, "a-brand-new-password");

        var login = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new { email = target.Email, password = "a-brand-new-password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("mfaRequired", await login.Content.ReadAsStringAsync());
    }

    // Regression guard for the rate limiter. RateLimits.Auth partitions on request path, so if the
    // token ever moves back into the path each guess gets a fresh bucket and this fails.
    [Fact]
    public async Task Bogus_tokens_share_one_rate_limit_bucket()
    {
        var client = _f.CreateClient();
        var seen = new HashSet<string>();
        for (var i = 0; i < 3; i++)
        {
            var res = await client.PostAsJsonAsync("/api/v1/auth/password-reset:complete",
                new CompleteRequest($"bogus-token-{i}", "a-valid-length-pw"));
            seen.Add(res.RequestMessage!.RequestUri!.AbsolutePath);
        }
        Assert.Single(seen); // one path => one partition => one bucket
    }
```

Add the two remaining helpers to `TestAuth.cs`:

```csharp
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

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test --filter PasswordResetTests -v minimal`
Expected: the seven new tests FAIL with 404 — the consume route does not exist.

- [ ] **Step 3: Write the consume handler**

Add to `PasswordResetEndpoints.MapPasswordResetEndpoints`:

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

        // One detail string for every failure mode. Unknown, expired, already-used and
        // now-multi-org must be indistinguishable, or the uniform 404 is not uniform.
        var notFound = Problem.Of(404, "Not found", "Reset link not found or expired.");

        var now = DateTimeOffset.UtcNow;
        var hash = MemberEndpoints.HashToken(req.Token ?? "");
        var reset = await db.PasswordResets.FirstOrDefaultAsync(
            p => p.TokenHash == hash && p.UsedAt == null && p.ExpiresAt > now, ct);
        if (reset is null) return notFound;

        // Re-checked here, not only at mint: the target can accept an invitation to a second org
        // inside the token's one-hour life, which is exactly what the gate exists to prevent.
        if (await db.OrgMembers.CountAsync(m => m.UserId == reset.UserId, ct) > 1) return notFound;

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
Expected: every test PASSES, including `Issuing_a_second_link_invalidates_the_first` from Task 3.

- [ ] **Step 5: Run the full backend suite for regressions**

Run: `dotnet test -c Release`
Expected: all pass, `Skipped: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/EasyDocs.Api/Auth/PasswordResetEndpoints.cs tests/EasyDocs.Api.Tests/
git commit -s -m "feat(auth): consume a reset link, revoking the target's ed_ tokens"
```

---

## Task 5: The web UI

**Files:**
- Create: `web/src/routes/PasswordReset.tsx`
- Modify: `web/src/App.tsx`, `web/src/api.ts`, `web/src/components/MembersPanel.tsx`, `web/src/routes/Login.tsx`

- [ ] **Step 1: Add the two API calls to `api.ts`**

Follow the existing exported-function style in that file:

```ts
export const mintPasswordReset = (userId: string) =>
  api.post<{ token: string; url: string; expiresAt: string }>(
    `/api/v1/org/members/${userId}/password-reset`, {})

export const completePasswordReset = (token: string, password: string) =>
  api.post('/api/v1/auth/password-reset:complete', { token, password })
```

- [ ] **Step 2: Write the public page**

`web/src/routes/PasswordReset.tsx` — read the token from the path, post it in the body, send them to `/login` on success. Mirror `ShareLanding.tsx` for the "public page, no app chrome" treatment.

- [ ] **Step 3: Register the route outside the auth guard**

In `App.tsx`, beside the `/s/:token` route and **above** the `RequireAuth` element:

```tsx
{/* Public on purpose: the holder is locked out by definition, so this sits outside RequireAuth. */}
<Route path="/password-reset/:token" element={<PasswordReset />} />
```

- [ ] **Step 4: Add the Members panel control**

In `MembersPanel.tsx`, a "Reset password" button per member. Render it only when the viewer may actually use it — Owners always, Admins only against a `Member` — so the UI does not offer an action the API will 403. On success show the link once with a copy button, reusing the pattern in `Settings.tsx` for a freshly minted PAT.

- [ ] **Step 5: Add the login-screen copy**

In `Login.tsx`, below the form:

```tsx
<p className="hint">
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

This is the only test exercising a route registered *outside* `RequireAuth`, which is a classic place for a later refactor to silently re-gate the page.

- [ ] **Step 1: Write the spec**

One test, the whole loop: an Owner registers, seeds a member, issues a reset from the Members panel, copies the link, signs out, opens the link, sets a new password, and signs in with it. Follow `web/e2e/fixtures.ts` for registration helpers and `invitations.spec.ts` for the closest existing shape.

- [ ] **Step 2: Run it against the built image**

```bash
docker compose -f deploy/compose/docker-compose.yml up --build -d
npm --prefix web run e2e -- password-reset
```
Expected: PASS. The suite runs against the shipped container, not the Vite dev server — that is deliberate (ADR-12).

- [ ] **Step 3: Commit**

```bash
git add web/e2e/password-reset.spec.ts
git commit -s -m "test(e2e): password reset end to end"
```

---

## Task 7: Docs and the OpenAPI snapshot

**Files:**
- Modify: `SECURITY.md`, `CHANGELOG.md`, `docs-site/docs/user-guide.md`, `docs-site/docs/api/openapi/v1.json`

- [ ] **Step 1: SECURITY.md — three gaps**

Under *Functional limitations*: a sole Owner or any multi-org account cannot be recovered this way; and a signed-in member still cannot change a password they know.
Under *Security-relevant*: a reset revokes `ed_` tokens but not sessions, so a stolen cookie survives up to seven days — the same limitation ADR-9 already records for sign-out.

- [ ] **Step 2: CHANGELOG.md — `[Unreleased] → Added`**

Match the register of the surrounding entries: what it does and why it is shaped that way, not a feature bullet.

- [ ] **Step 3: user-guide.md**

How an Owner issues a reset, that the link is one hour and single-use, and that they must deliver it themselves because easydocs sends no email.

- [ ] **Step 4: Regenerate the checked-in OpenAPI snapshot**

Nothing in CI regenerates this, so it silently drifts:

```bash
docker compose -f deploy/compose/docker-compose.yml up -d
curl -s http://localhost:8080/openapi/v1.json | python3 -m json.tool > docs-site/docs/api/openapi/v1.json
```

Verify both new paths appear:

```bash
grep -c 'password-reset' docs-site/docs/api/openapi/v1.json
```
Expected: at least `2`.

- [ ] **Step 5: Commit**

```bash
git add SECURITY.md CHANGELOG.md docs-site/
git commit -s -m "docs: password reset, and the three gaps it leaves"
```

---

## Done When

- [ ] `dotnet test -c Release` passes with `Skipped: 0`
- [ ] `dotnet build -c Release` produces zero warnings
- [ ] `npm --prefix web run build` and `run lint` are clean
- [ ] `password-reset.spec.ts` passes against the built container image
- [ ] An Admin gets 403 resetting an Owner, and 200 resetting a Member
- [ ] The three 404 bodies are byte-identical
- [ ] A consumed reset kills the target's `ed_` tokens and leaves MFA armed
- [ ] `docs-site/docs/api/openapi/v1.json` contains both new paths
- [ ] Every commit is signed off, and the PR references #49
