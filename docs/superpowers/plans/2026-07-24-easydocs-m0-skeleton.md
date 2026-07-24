# easydocs M0 — Skeleton Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the easydocs skeleton — a single ASP.NET Core app + Postgres that boots via `docker compose up`, with email/password auth (register creates an org + owner), nestable folders, and local `.docx` upload producing version `0.0.1`. Exit gate: acceptance criteria **E1 (Folders)** and **E2 (Ingest)** green.

**Architecture:** One ASP.NET Core application (REST API now; it will also host the React SPA and WOPI host in later milestones). EF Core + PostgreSQL with migrations applied on startup. Blobs stored content-addressed on a filesystem volume. Auth is Argon2id password hashing + a JWT carried in an `httpOnly` cookie (web) or `Authorization: Bearer` (API). Integration tests use Testcontainers to spin up a throwaway Postgres in Docker.

**Tech Stack:** .NET 8 (LTS) · ASP.NET Core Minimal APIs · EF Core 8 + Npgsql · PostgreSQL 16 · Konscious.Security.Cryptography.Argon2 · Microsoft.AspNetCore.Authentication.JwtBearer · xUnit + Testcontainers.PostgreSql + `WebApplicationFactory` · Vite/React (minimal scaffold) · Docker Compose.

**Spec:** `docs/superpowers/specs/2026-07-24-easydocs-v1-design.md` (§2 scope, §4 data model, §5.1 numbering, §10 API, §11 auth, §12.1 E1–E2, §13 M0).

**Reference the spec for the full v1 data model.** M0 creates only the tables it needs (`organizations`, `users`, `org_members`, `folders`, `documents`, `document_members`, `blobs`, `versions`, `branches`); the rest arrive in later migrations. Domain enums/columns not exercised in M0 (e.g. `versions.source` values beyond `upload`) are still declared per spec so later milestones don't re-migrate.

---

## Prerequisites (do once, before Task 1)

- [ ] **Install the .NET 8 SDK.** macOS: `brew install dotnet@8` (or the official installer from dotnet.microsoft.com). Verify: `dotnet --version` prints `8.0.x`.
- [ ] Confirm Docker is running: `docker ps` succeeds (Testcontainers and compose both need it).
- [ ] Node ≥ 20 present (`node --version`) — used by the Vite scaffold in Task 11.

---

## File Structure (locked before tasks)

```
easydocs.sln
Directory.Build.props                     # shared: net8.0, nullable enable, treat-warnings
src/EasyDocs.Api/
  EasyDocs.Api.csproj
  Program.cs                              # builder, DI, middleware pipeline, migrate-on-startup
  appsettings.json  appsettings.Development.json
  Domain/                                 # POCO entities, one file per aggregate
    Organization.cs  User.cs  OrgMember.cs
    Folder.cs  Document.cs  DocumentMember.cs
    Branch.cs  Blob.cs  DocumentVersion.cs
    Enums.cs                              # OrgRole, DocRole, VersionSource, BranchKind
  Data/
    EasyDocsDbContext.cs
    Migrations/                           # EF-generated
  Auth/
    PasswordHasher.cs                     # Argon2id wrapper (interface + impl)
    JwtService.cs                         # issue/validate app JWT
    AuthEndpoints.cs                      # POST /auth/register, /auth/login
    CurrentUser.cs                        # accessor from HttpContext
    DocumentAuthorization.cs              # resolve_role(user, document)
  Folders/
    FolderEndpoints.cs
  Documents/
    DocumentEndpoints.cs                  # create, get, upload -> 0.0.1
  Storage/
    IBlobStore.cs  FileSystemBlobStore.cs # content-addressed, sha256 layout
  Common/
    Problem.cs                            # RFC-7807 helpers
tests/EasyDocs.Api.Tests/
  EasyDocs.Api.Tests.csproj
  PostgresFixture.cs                      # Testcontainers Postgres, shared per collection
  ApiFactory.cs                           # WebApplicationFactory wired to the test DB + temp blob dir
  PasswordHasherTests.cs
  BlobStoreTests.cs
  AuthTests.cs
  FolderTests.cs
  DocumentUploadTests.cs
web/                                      # Vite React SPA (minimal in M0)
  package.json  vite.config.ts  index.html  src/main.tsx  src/App.tsx
Dockerfile                                # multi-stage: node build web -> dotnet publish -> runtime
deploy/compose/docker-compose.yml         # easydocs + postgres (collabora added in M1)
deploy/compose/.env.example
.github/workflows/ci.yml
CONTRIBUTING.md                           # DCO sign-off instructions
LICENSE                                   # AGPL-3.0 (server); packages/* MIT arrives with first client
.dockerignore  .gitignore
```

**Decomposition rationale:** endpoints are grouped by responsibility (Auth/Folders/Documents), not by technical layer, so files that change together live together. Each endpoint file owns its request/response DTOs. `Program.cs` only wires DI + pipeline and maps the endpoint groups.

---

## Task 1: Solution scaffold + branch

**Files:**
- Create: `easydocs.sln`, `Directory.Build.props`, `src/EasyDocs.Api/EasyDocs.Api.csproj`, `tests/EasyDocs.Api.Tests/EasyDocs.Api.Tests.csproj`, `.gitignore`, `.dockerignore`

- [ ] **Step 1: Create the M0 working branch**

```bash
git checkout main && git checkout -b m0-skeleton
```

- [ ] **Step 2: Scaffold the solution and projects**

```bash
cd /Users/robertozuniga/Desktop/easydocs
dotnet new sln -n easydocs
dotnet new webapi -n EasyDocs.Api -o src/EasyDocs.Api --use-minimal-apis
dotnet new xunit -n EasyDocs.Api.Tests -o tests/EasyDocs.Api.Tests
dotnet sln add src/EasyDocs.Api/EasyDocs.Api.csproj tests/EasyDocs.Api.Tests/EasyDocs.Api.Tests.csproj
dotnet add tests/EasyDocs.Api.Tests reference src/EasyDocs.Api
dotnet new gitignore
# delete the webapi template's sample so the strict build stays clean:
rm -f src/EasyDocs.Api/WeatherForecast.cs
# (also remove any WeatherForecast endpoint/controller the template added in Program.cs)
```

- [ ] **Step 3: Add `Directory.Build.props`** (shared compiler settings)

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Verify it builds**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -s -m "chore: scaffold solution (api + tests)"
```
(`-s` adds the DCO `Signed-off-by` line — use it on every commit in this repo.)

---

## Task 2: Domain entities + DbContext + first migration + migrate-on-startup

**Files:**
- Create: `src/EasyDocs.Api/Domain/*.cs`, `src/EasyDocs.Api/Data/EasyDocsDbContext.cs`
- Modify: `src/EasyDocs.Api/Program.cs`, `src/EasyDocs.Api/EasyDocs.Api.csproj`
- Test: `tests/EasyDocs.Api.Tests/PostgresFixture.cs`, `ApiFactory.cs`, and a boot smoke test

- [ ] **Step 1: Add EF Core + Npgsql packages**

```bash
dotnet add src/EasyDocs.Api package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add src/EasyDocs.Api package Microsoft.EntityFrameworkCore.Design
dotnet add tests/EasyDocs.Api.Tests package Testcontainers.PostgreSql
dotnet add tests/EasyDocs.Api.Tests package Microsoft.AspNetCore.Mvc.Testing
dotnet tool install --global dotnet-ef
```

- [ ] **Step 2: Write the domain entities** (`Domain/Enums.cs` + one file per entity)

`Enums.cs`:
```csharp
namespace EasyDocs.Api.Domain;
public enum OrgRole { Owner, Admin, Member }
public enum DocRole { Owner, Editor, Viewer }
public enum VersionSource { Upload, EditWopi, Import, Merge, Revert, CopyPush }
public enum BranchKind { Main, Concurrent, IncomingPush }
```

`Document.cs` (representative — model the others per spec §4; use `Guid` PKs with DB-side `gen_random_uuid()` defaults, `DateTimeOffset` timestamps, and the version-counter columns that §5.1 makes authoritative):
```csharp
namespace EasyDocs.Api.Domain;
public class Document
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid? FolderId { get; set; }
    public string Name { get; set; } = "";
    public Guid? ParentDocumentId { get; set; }
    public Guid? ForkedFromVersionId { get; set; }
    public int VersionCounterMajor { get; set; }
    public int VersionCounterMinor { get; set; }
    public int VersionCounterRev { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
```
Create `Organization` (incl. `Slug`), `User` (`Email` CITEXT-unique, `DisplayName`, `PasswordHash?`), `OrgMember`, `Folder` (`ParentId?`, `DeletedAt?`), `DocumentMember`, `Branch` (`Ordinal`, `RootVersionId?`, `Kind`, `MergedIntoVersionId?`), `Blob` (`Sha256` PK, `SizeBytes`, `Mime`, `StorageKey`), `DocumentVersion` (`BranchId`, `SeqInBranch`, `ParentVersionId?`, `Major/Minor/Revision`, `Name?`, `Source`, `BlobSha256`, publish columns nullable, `CreatedBy`, `CreatedAt`).

- [ ] **Step 3: Write `EasyDocsDbContext`** with `DbSet<>`s and Fluent config for unique constraints (`users.email`, `organizations.slug`, `branches (document_id, ordinal)`, `versions (branch_id, seq_in_branch)`), the `citext` extension, and `gen_random_uuid()` defaults. Enable `HasPostgresExtension("citext")` and `HasPostgresExtension("pgcrypto")` in `OnModelCreating`.

- [ ] **Step 4: Wire config + migrate-on-startup in `Program.cs`**

```csharp
builder.Services.AddDbContext<EasyDocsDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));
// ... after build:
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>().Database.Migrate();
```
Add a trivial `app.MapGet("/health", () => Results.Ok(new { status = "ok" }));`.

- [ ] **Step 5: Create the initial migration**

```bash
dotnet ef migrations add InitialSchema -p src/EasyDocs.Api -o Data/Migrations
```
Expected: a migration file appears under `src/EasyDocs.Api/Data/Migrations/`.

- [ ] **Step 6: Write the test harness.** `ApiFactory` **owns** the Testcontainers Postgres and implements `IAsyncLifetime` so xUnit starts the container (async) *before* any client is created — this is critical, because the sync `CreateClient()` triggers `builder.Build()` → `Database.Migrate()`, which needs a live DB. Do **not** use a separate `PostgresFixture`.

```csharp
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg =
        new PostgreSqlBuilder().WithImage("postgres:16").Build();
    public string BlobRoot { get; } = Directory.CreateTempSubdirectory().FullName;

    public Task InitializeAsync() => _pg.StartAsync();          // awaited by xUnit before injection
    public new Task DisposeAsync() => _pg.DisposeAsync().AsTask();

    protected override void ConfigureWebHost(IWebHostBuilder b) =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["ConnectionStrings:Postgres"] = _pg.GetConnectionString(),
            ["BLOB_ROOT"] = BlobRoot,
            ["Jwt:Secret"] = "test-secret-at-least-32-bytes-long-xxxxx",
        }));
}
```

Boot smoke test:
```csharp
public class BootTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public BootTests(ApiFactory f) => _f = f;

    [Fact]
    public async Task Health_endpoint_returns_ok_and_migrations_applied()
        => (await _f.CreateClient().GetAsync("/health")).EnsureSuccessStatusCode();
}
```
(Make `Program` visible to tests: add `public partial class Program { }` at the end of `Program.cs`.)

> **Design-time migrations:** `dotnet ef migrations add` instantiates the context and runs the `UseNpgsql(...)` lambda but does **not** contact a DB for `add`. If your Npgsql version errors on a null connection string at design time, add a dev value to `appsettings.Development.json` (`ConnectionStrings:Postgres`) or an `IDesignTimeDbContextFactory<EasyDocsDbContext>`.

- [ ] **Step 7: Run the test — verify it passes** (this proves migrate-on-startup works against a real Postgres)

Run: `dotnet test --filter BootTests`
Expected: PASS (Testcontainers pulls postgres:16, app boots, `/health` returns 200).

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -s -m "feat: domain model, DbContext, initial migration, migrate-on-startup"
```

---

## Task 3: Argon2id password hasher

**Files:**
- Create: `src/EasyDocs.Api/Auth/PasswordHasher.cs`
- Test: `tests/EasyDocs.Api.Tests/PasswordHasherTests.cs`

- [ ] **Step 1: Add the package** — `dotnet add src/EasyDocs.Api package Konscious.Security.Cryptography.Argon2`

- [ ] **Step 2: Write the failing test**

```csharp
public class PasswordHasherTests
{
    private readonly IPasswordHasher _h = new Argon2idPasswordHasher();

    [Fact]
    public void Verify_true_for_correct_password_false_otherwise()
    {
        var hash = _h.Hash("correct horse battery staple");
        Assert.NotEqual("correct horse battery staple", hash); // never plaintext
        Assert.True(_h.Verify("correct horse battery staple", hash));
        Assert.False(_h.Verify("wrong password", hash));
    }

    [Fact]
    public void Two_hashes_of_same_password_differ() // random salt
        => Assert.NotEqual(_h.Hash("same"), _h.Hash("same"));
}
```

- [ ] **Step 3: Run — verify it fails** (`Argon2idPasswordHasher` doesn't exist). Run: `dotnet test --filter PasswordHasherTests` → FAIL (compile error).

- [ ] **Step 4: Implement** `IPasswordHasher` + `Argon2idPasswordHasher` — generate a 16-byte random salt, Argon2id with sane params (e.g. `MemorySize=19456`, `Iterations=2`, `DegreeOfParallelism=1`), encode `salt`+`hash` (store salt alongside hash, e.g. `{b64salt}.{b64hash}`), and verify by re-hashing with the parsed salt and constant-time comparing.

- [ ] **Step 5: Run — verify it passes.** Run: `dotnet test --filter PasswordHasherTests` → PASS.

- [ ] **Step 6: Commit** — `git add -A && git commit -s -m "feat: Argon2id password hasher"`

---

## Task 4: Register endpoint (creates user + org + owner)

**Files:**
- Create: `src/EasyDocs.Api/Auth/JwtService.cs`, `src/EasyDocs.Api/Auth/AuthEndpoints.cs`, `src/EasyDocs.Api/Common/Problem.cs`
- Modify: `Program.cs` (DI: `IPasswordHasher`, `JwtService`; `app.MapAuthEndpoints()`)
- Test: `tests/EasyDocs.Api.Tests/AuthTests.cs`

- [ ] **Step 1: Write the failing test** (spec §4 first-run seed: register makes user + org + `org_members(owner)`)

```csharp
[Fact]
public async Task Register_creates_user_org_and_owner_membership()
{
    var client = _f.CreateClient();
    var res = await client.PostAsJsonAsync("/api/v1/auth/register",
        new { email = "rob@example.com", displayName = "Rob", password = "pw-at-least-12", orgName = "Aces" });

    Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    // Assert against the DB via a scope: exactly one org, one user, one org_members row with role=Owner.
    using var scope = _f.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
    var user = await db.Users.SingleAsync(u => u.Email == "rob@example.com");
    Assert.NotNull(user.PasswordHash);                         // hash present, never plaintext
    var member = await db.OrgMembers.SingleAsync(m => m.UserId == user.Id);
    Assert.Equal(OrgRole.Owner, member.Role);
}

[Fact]
public async Task Register_duplicate_email_returns_409()
{
    var client = _f.CreateClient();
    var body = new { email = "dup@example.com", displayName = "D", password = "pw-at-least-12", orgName = "X" };
    await client.PostAsJsonAsync("/api/v1/auth/register", body);
    var res = await client.PostAsJsonAsync("/api/v1/auth/register", body);
    Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
}
```

- [ ] **Step 2: Run — verify it fails.** Run: `dotnet test --filter AuthTests` → FAIL (404, endpoint missing).

- [ ] **Step 3: Implement** `POST /api/v1/auth/register` in `AuthEndpoints.cs`: validate email/password (password ≥ 12 chars), reject duplicate email with `Problem(409)`, hash the password, in one transaction create `Organization` (slugify `orgName`, ensure unique), `User`, `OrgMember{Owner}`. Return `201` with the user + a `Set-Cookie: ed_session=<jwt>; HttpOnly; SameSite=Lax; Secure`. `JwtService.Issue(userId, orgId)` signs an HS256 JWT with a config secret.

- [ ] **Step 4: Run — verify it passes.** Run: `dotnet test --filter AuthTests` → PASS.

- [ ] **Step 5: Commit** — `git commit -s -am "feat: register endpoint creates user+org+owner, issues session JWT"`

---

## Task 5: Login endpoint + auth wiring

**Files:**
- Modify: `src/EasyDocs.Api/Auth/AuthEndpoints.cs`, `Program.cs`
- Create: `src/EasyDocs.Api/Auth/CurrentUser.cs`
- Test: `tests/EasyDocs.Api.Tests/AuthTests.cs` (add cases)

- [ ] **Step 1: Add JwtBearer + cookie-reading.** `dotnet add src/EasyDocs.Api package Microsoft.AspNetCore.Authentication.JwtBearer`. Configure `AddAuthentication().AddJwtBearer(...)` with the HS256 `Jwt:Secret` key. Because the issue side (`JwtService`) sets **no** issuer/audience, `TokenValidationParameters` must set `ValidateIssuer = false`, `ValidateAudience = false`, `ValidateIssuerSigningKey = true` (else every token is rejected). Claims: `sub` = userId, `org` = orgId. Add an `OnMessageReceived` event that falls back to the `ed_session` cookie when there's no `Authorization` header (native `EventSource`/browser path — spec §10.2). Register `AddAuthorization()`, `UseAuthentication()`, `UseAuthorization()`.

- [ ] **Step 2: Write the failing tests**

```csharp
[Fact] public async Task Login_with_correct_password_sets_session_cookie() { /* register, then POST /auth/login, assert 200 + Set-Cookie ed_session */ }
[Fact] public async Task Login_with_wrong_password_returns_401() { /* ... */ }
[Fact] public async Task Protected_endpoint_401_without_auth() { /* GET /api/v1/me without cookie -> 401 */ }
[Fact] public async Task Protected_endpoint_200_with_auth() { /* login, reuse credential -> GET /api/v1/me -> 200, returns current user */ }
```

> **Cookie note (avoids a false failure):** `WebApplicationFactory.CreateClient()` has **no** `CookieContainer`, and the cookie is issued `Secure` while the test host is `http` — so it will not auto-resend. In the follow-up-request tests, either (a) read the `Set-Cookie` value from the login response and set it manually as a `Cookie` header on the next request, or (b) capture the JWT from the login body and send `Authorization: Bearer <jwt>`. Prefer (b) for protected-endpoint tests; use (a) for the one test that specifically asserts the cookie path.

- [ ] **Step 3: Run — verify they fail.** Run: `dotnet test --filter AuthTests` → new tests FAIL.

- [ ] **Step 4: Implement** `POST /api/v1/auth/login` (verify password, set cookie), a `CurrentUser` accessor reading `sub`/`org` claims from `HttpContext.User`, and a `GET /api/v1/me` `.RequireAuthorization()` endpoint returning the current user. 

- [ ] **Step 5: Run — verify all pass.** Run: `dotnet test --filter AuthTests` → PASS.

- [ ] **Step 6: Commit** — `git commit -s -am "feat: login + JWT cookie/bearer auth, GET /me"`

---

## Task 6: Folders — CRUD, nesting, move, delete (E1)

**Files:**
- Create: `src/EasyDocs.Api/Folders/FolderEndpoints.cs`
- Modify: `Program.cs` (`app.MapFolderEndpoints()`)
- Test: `tests/EasyDocs.Api.Tests/FolderTests.cs`

> All folder endpoints are `.RequireAuthorization()` and scope every query to the caller's `OrgId`.

- [ ] **Step 1: Write the failing tests** (map directly to E1)

```csharp
[Fact] public async Task Can_nest_folders_at_least_three_levels() {
    // POST /folders {name:"Leases"} -> id A
    // POST /folders {name:"Templates", parentId:A} -> B
    // POST /folders {name:"2026", parentId:B} -> C
    // GET /folders?parentId=B contains C; each level resolvable
}
[Fact] public async Task Delete_folder_with_children_requires_mode_and_promote_moves_children_to_parent() {
    // DELETE /folders/B?mode=promote_children -> children reparent to B.parent; DELETE without mode on non-empty -> 400
}
[Fact] public async Task Delete_folder_mode_trash_soft_deletes() {
    // DELETE /folders/B?mode=trash -> B.DeletedAt set, excluded from GET
}
```

- [ ] **Step 2: Run — verify they fail.** Run: `dotnet test --filter FolderTests` → FAIL.

- [ ] **Step 3: Implement** `GET /api/v1/folders?parentId=`, `POST /api/v1/folders {name, parentId?}`, `PATCH /api/v1/folders/{id} {name?, parentId?}`, `DELETE /api/v1/folders/{id}?mode=trash|promote_children`. Soft delete via `DeletedAt`; `promote_children` reparents to the deleted folder's parent; deleting a non-empty folder with no `mode` → `Problem(400)`. Enforce the `UNIQUE(org_id, parent_id, name)` constraint → `409` on clash.

- [ ] **Step 4: Run — verify they pass.** Run: `dotnet test --filter FolderTests` → PASS. This closes the **folder-only** parts of E1 (nest, promote/trash); the "move a document between folders" part of E1 closes in **Task 8** (needs the Documents endpoints).

- [ ] **Step 5: Commit** — `git commit -s -am "feat: folders CRUD, nesting, promote/trash delete"`

---

## Task 7: Content-addressed blob store

**Files:**
- Create: `src/EasyDocs.Api/Storage/IBlobStore.cs`, `src/EasyDocs.Api/Storage/FileSystemBlobStore.cs`
- Modify: `Program.cs` (bind `BLOB_ROOT` config, register `IBlobStore`)
- Test: `tests/EasyDocs.Api.Tests/BlobStoreTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
public class BlobStoreTests
{
    [Fact]
    public async Task Put_returns_sha_and_stores_once_under_sharded_path()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var store = new FileSystemBlobStore(root);
        var bytes = Encoding.UTF8.GetBytes("hello docx");
        var r1 = await store.PutAsync(new MemoryStream(bytes));
        var r2 = await store.PutAsync(new MemoryStream(bytes)); // identical content
        Assert.Equal(r1.Sha256, r2.Sha256);                    // deterministic
        Assert.Equal(bytes.Length, r1.SizeBytes);
        // layout: root/{sha[0:2]}/{sha[2:4]}/{sha}
        var path = Path.Combine(root, r1.Sha256[..2], r1.Sha256[2..4], r1.Sha256);
        Assert.True(File.Exists(path));
        Assert.True(await store.ExistsAsync(r1.Sha256));
        using var read = await store.OpenReadAsync(r1.Sha256);
        Assert.Equal(bytes, ReadAll(read));
    }

    private static byte[] ReadAll(Stream s)
    { using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }
}
```

- [ ] **Step 2: Run — verify it fails.** Run: `dotnet test --filter BlobStoreTests` → FAIL.

- [ ] **Step 3: Implement** `IBlobStore` (`Task<(string Sha256,long SizeBytes)> PutAsync(Stream)`, `Task<bool> ExistsAsync(string)`, `Task<Stream> OpenReadAsync(string)`) and `FileSystemBlobStore`: stream to a temp file while computing SHA-256, then move into `root/{sha[0:2]}/{sha[2:4]}/{sha}`; if the target already exists, discard the temp (write-once). Return sha + size.

- [ ] **Step 4: Run — verify it passes.** Run: `dotnet test --filter BlobStoreTests` → PASS.

- [ ] **Step 5: Commit** — `git commit -s -am "feat: content-addressed filesystem blob store"`

---

## Task 8: Documents — create shell + multipart upload → version 0.0.1 (E2)

**Files:**
- Create: `src/EasyDocs.Api/Documents/DocumentEndpoints.cs`
- Modify: `Program.cs`
- Test: `tests/EasyDocs.Api.Tests/DocumentUploadTests.cs`

> Ingest is a **direct multipart upload to the app** (spec §10.3) — no pre-signed URLs.

- [ ] **Step 1: Write the failing tests** (map to E2)

```csharp
[Fact] public async Task Create_then_upload_produces_first_version_0_0_1() {
    // login; POST /documents {name, folderId?} -> docId
    // POST /documents/{docId}/versions (multipart .docx) -> 201
    // GET /documents/{docId}/versions -> single version with major=0 minor=0 revision=1, source=upload
    // assert document.VersionCounterRev == 1, a main branch exists, a blobs row exists
}
[Fact] public async Task Upload_creator_becomes_document_owner_member() {
    // after create, document_members has the creator as Owner
}
[Fact] public async Task Move_document_between_folders_preserves_document() {   // relocated from Task 6 (E1)
    // create folders A,B; create doc in A; PATCH /documents/{id} {folderId:B};
    // assert folderId==B, same doc id, its versions/members unchanged
}
```

- [ ] **Step 2: Run — verify they fail.** Run: `dotnet test --filter DocumentUploadTests` → FAIL.

- [ ] **Step 3: Implement**
  - `POST /api/v1/documents {name, folderId?}`: create `Document` (counters at 0.0.0), a `Branch{Ordinal=0, Kind=Main}`, and a `DocumentMember{creator, Owner}`, all in one transaction.
  - `POST /api/v1/documents/{id}/versions` (multipart): stream the file to `IBlobStore.PutAsync`, upsert the `Blob` row, then create the first `DocumentVersion` — under a per-document row lock (`SELECT … FOR UPDATE`; EF has no built-in, so `db.Database.ExecuteSqlRaw("SELECT id FROM documents WHERE id = {0} FOR UPDATE", id)` inside the transaction), read-and-increment `VersionCounterRev` (0→1) per spec §5.1, giving `0.0.1`, `Source=Upload`, `SeqInBranch=1` on the main branch. Return `201`. *(M0 has no concurrent upload path, so the lock is forward-insurance for M1's `commit_save` — keep it minimal.)*
  - `PATCH /api/v1/documents/{id} {name?, folderId?}`: rename / move between folders (org-scoped; target folder must be in the same org). This satisfies the E1 "move preserves document" case.
  - `GET /api/v1/documents/{id}/versions`: list versions (id, number, source, createdAt, createdBy).
  - All endpoints `.RequireAuthorization()`, org-scoped, and (for a specific doc) pass through the document-role check from Task 9 once it exists — for now inline an `OrgId` + membership check.

- [ ] **Step 4: Run — verify they pass.** Run: `dotnet test --filter DocumentUploadTests` → PASS. This closes **E2** (first version is exactly `0.0.1`) and the remaining **E1** move case.

- [ ] **Step 5: Commit** — `git commit -s -am "feat: document create + upload -> 0.0.1 + move between folders (E1/E2)"`

---

## Task 9: Document authorization chokepoint

**Files:**
- Create: `src/EasyDocs.Api/Auth/DocumentAuthorization.cs`
- Modify: `Documents/DocumentEndpoints.cs`, `Folders/FolderEndpoints.cs` (route doc access through it)
- Test: `tests/EasyDocs.Api.Tests/DocumentUploadTests.cs` (add authz cases)

> Spec §10/§11: one `resolve_role(user, document)` chokepoint over `document_members`; **org role grants no implicit document access**.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Non_member_cannot_read_document_returns_403() { /* user B (same org, not a member) GET /documents/{A's doc} -> 403 */ }
[Fact] public async Task Viewer_cannot_upload_returns_403() { /* member with Viewer role POSTs a version -> 403 */ }
[Fact] public async Task Editor_can_upload() { /* Editor member -> 201 */ }
```

- [ ] **Step 2: Run — verify they fail.** Run: `dotnet test --filter DocumentUploadTests` → new cases FAIL.

- [ ] **Step 3: Implement** `DocumentAuthorization.ResolveRoleAsync(userId, documentId) -> DocRole?` (null = no access), plus a helper `RequireDocRole(min)` used by document endpoints. Editing actions (upload) require `Editor`+; reads require `Viewer`+. No org-role fallback.

- [ ] **Step 4: Run — verify all pass.** Run: `dotnet test` (full suite) → PASS.

- [ ] **Step 5: Commit** — `git commit -s -am "feat: document authorization chokepoint (resolve_role)"`

---

## Task 10: Docker Compose + Dockerfile (compose boots)

**Files:**
- Create: `Dockerfile`, `deploy/compose/docker-compose.yml`, `deploy/compose/.env.example`, `.dockerignore`
- Modify: `appsettings.json` (read connection string + `BLOB_ROOT` + JWT secret from env)

- [ ] **Step 1: Write the `Dockerfile`** — **dotnet-only stages for now** (there is no `web/` until Task 11, so no node stage yet): stage 1 `mcr.microsoft.com/dotnet/sdk:8.0` runs `dotnet publish src/EasyDocs.Api -c Release -o /app/publish` (this produces an empty/placeholder `wwwroot` — fine, the SPA lands in Task 11); stage 2 `mcr.microsoft.com/dotnet/aspnet:8.0` runtime, `apt-get install -y libreoffice --no-install-recommends` (bundled now so later PDF work has it), `COPY --from=build /app/publish .`, `EXPOSE 8080`, `ENTRYPOINT ["dotnet","EasyDocs.Api.dll"]`. Task 11 prepends the node build stage and copies its output into `wwwroot`.

- [ ] **Step 2: Write `docker-compose.yml`** — services `postgres` (`postgres:16`, healthcheck, named volume) and `easydocs` (build `.`, `depends_on` postgres healthy, env from `.env`, port `8080:8080`, a `blobs` volume mounted at `BLOB_ROOT`). Collabora is intentionally **not** here yet — it arrives in M1.

- [ ] **Step 3: Write `.env.example`** — `POSTGRES_PASSWORD`, `ConnectionStrings__Postgres`, `BLOB_ROOT=/data/blobs`, `Jwt__Secret`, `PUBLIC_BASE_URL`.

- [ ] **Step 4: Boot it and verify** (manual E-gate step)

```bash
cp deploy/compose/.env.example deploy/compose/.env   # fill secrets
docker compose -f deploy/compose/docker-compose.yml up --build -d
curl -fsS http://localhost:8080/health   # -> {"status":"ok"}
docker compose -f deploy/compose/docker-compose.yml down
```
Expected: `/health` returns 200 (migrations ran on startup against the compose Postgres).

- [ ] **Step 5: Commit** — `git commit -s -am "feat: docker compose (easydocs + postgres), multi-stage Dockerfile"`

---

## Task 11: Minimal React SPA + static serving + SPA fallback

**Files:**
- Create: `web/` (Vite React scaffold), modify `Program.cs` (static files + fallback), `Dockerfile` (wire the build)

- [ ] **Step 1: Scaffold** — `npm create vite@latest web -- --template react-ts`, then a minimal `App.tsx` that calls `/health` and renders the status (proves same-origin API + SPA coexist). Set Vite `build.outDir` to `../src/EasyDocs.Api/wwwroot` (local build path). Then update the **Dockerfile** to prepend a node stage and feed the runtime `wwwroot`:
  - Stage `web` (`node:20`): `COPY web/ ./web`, `npm --prefix web ci`, `npm --prefix web run build` → emits to `web/dist` (in Docker, set `build.outDir` via `--outDir web/dist` or a Docker-only env so it doesn't depend on the sibling path).
  - In the dotnet **build** stage, before `dotnet publish`: `COPY --from=web /web/dist ./src/EasyDocs.Api/wwwroot` so publish includes the SPA. (Alternatively `COPY --from=web` into the runtime stage's content root — pick one and state it.)

- [ ] **Step 2: Wire serving in `Program.cs`** — `app.UseDefaultFiles(); app.UseStaticFiles();` and a fallback `app.MapFallbackToFile("index.html")` that does **not** capture `/api`, `/wopi`, `/s`, `/health` (map those before the fallback).

- [ ] **Step 3: Write a fallback test**

```csharp
[Fact] public async Task Unknown_client_route_serves_index_html_not_404() {
    var res = await _f.CreateClient().GetAsync("/some/spa/route");
    Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    Assert.Contains("text/html", res.Content.Headers.ContentType!.ToString());
}
[Fact] public async Task Unknown_api_route_is_404_not_index() {
    var res = await _f.CreateClient().GetAsync("/api/v1/does-not-exist");
    Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
}
```
(Ensure a placeholder `wwwroot/index.html` exists for the test run, or build `web/` first.)

- [ ] **Step 4: Run — verify pass.** Run: `dotnet test --filter Fallback` → PASS.

- [ ] **Step 5: Commit** — `git commit -s -am "feat: minimal React SPA + static serving with API-safe fallback"`

---

## Task 12: CI + DCO + license split

**Files:**
- Create: `.github/workflows/ci.yml`, `CONTRIBUTING.md`
- Modify: `LICENSE` (AGPL-3.0 for the server)

- [ ] **Step 1: Write `ci.yml`** — on push/PR: checkout, setup-dotnet 8, setup-node 20, `dotnet build`, `dotnet test` (Testcontainers uses the runner's Docker), `npm --prefix web ci && npm --prefix web run build`. Add a DCO check step (e.g. the `Signed-off-by` presence check).

- [ ] **Step 2: Replace `LICENSE`** with the full **AGPL-3.0** text (server license per spec §14). Add a short note that future `packages/*` API clients are MIT.

- [ ] **Step 3: Write `CONTRIBUTING.md`** — DCO explanation, require `git commit -s`, how to run tests (`dotnet test`), how to run locally (`docker compose up`).

- [ ] **Step 4: Verify CI locally** — `dotnet test` from a clean checkout passes; `npm --prefix web run build` succeeds.

- [ ] **Step 5: Commit + open PR**

```bash
git add -A && git commit -s -m "chore: CI pipeline, AGPL-3.0 license, DCO contributing guide"
git push -u origin m0-skeleton
gh pr create --fill --base main
```

---

## M0 Done — Exit Checklist

- [ ] `docker compose up` boots; `/health` → 200; migrations applied automatically.
- [ ] **E1**: folders nest ≥ 3 levels; move a document between folders preserves it; deleting a non-empty folder requires `mode` and `promote_children` reparents; `trash` soft-deletes. (`FolderTests` green.)
- [ ] **E2**: local `.docx` upload; first version is exactly `0.0.1`; blob stored content-addressed once. (`DocumentUploadTests` green.)
- [ ] Register creates user + org + owner; login sets an `httpOnly` session cookie; protected endpoints 401 without it. (`AuthTests` green.)
- [ ] Document authorization enforced with no org-role fallback. (authz tests green.)
- [ ] Full suite green in CI; AGPL LICENSE + DCO `CONTRIBUTING.md` in place.

**Next:** write the **M1** plan (Collabora WOPI editing, `commit_save`, branch-on-stale, concurrent-branch merge, redline diff, eager numeric summary, numbering R1–R8) against this now-real skeleton.
```
