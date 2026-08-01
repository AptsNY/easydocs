# ponytail review — easydocs, 2026-08-01

An over-engineering pass over the whole repo at `main`. This review hunts one thing only: code that
should not exist. Correctness is not its job — that is covered by 346 xUnit tests and 90 Playwright
tests, all green.

**Verdict: the shipped source is genuinely lean.** No DI ceremony, no repository layer, no
event-sourcing, no factories, no config for constants. Most concrete-over-interface calls were made
correctly and documented in place. The bloat is concentrated in exactly two spots: an authorization
helper re-invented in seven files, and a test suite that ignores the helper file written for it.

**Total deletable: ~630 lines** — ~230 production/SPA, ~400 test — plus nine redundant database round
trips.

---

## Applied in this pass

Everything below was low-risk and is already in the tree.

| Fix | Rung | Effect |
|---|---|---|
| Session cookie attributes defined once, not copy-pasted three times | 2 | A `Delete` whose `Path`/flags drift from the `Append` is silently ignored by the browser. That class of bug is now unrepresentable. |
| `DocEvent` union derived from the `TYPES` array (`useSse.ts`) | 3 | Deletes 13 hand-maintained lines and the drift hazard behind them. |
| Three stale `ponytail:` comments deleted | — | Two claimed no org switcher exists; one claimed PAT auth was unwired. All three were describing code that now exists. A comment that lies is worse than no comment. |
| `! grep -q` release guard rewritten as an explicit `if` | — | Errexit does not apply to negated pipelines, so the guard could never fire. It was decorative. |

---

## Deferred to immediately after `v1.0.0`

These are real and worth doing. They are not worth doing *between now and a tag* on a green tree —
every one is a pure refactor whose only payoff is maintainability, and the release is the goal. Ranked
by value.

### 1. Seven hand-rolled copies of the authorization chokepoint — ~120 lines

`Auth/DocumentAuthorization.cs:45` already resolves role → 404/403/privilege → `IResult`, and 13 call
sites use it correctly. Seven other files re-implement it on top of the older `ResolveAsync`:

| File | Shape |
|---|---|
| `Documents/DocumentEndpoints.cs:364` | private `AuthorizeAsync` |
| `Publishing/PublishEndpoints.cs:70` | private copy — comment says *"Mirrors DocumentEndpoints.AuthorizeAsync"* |
| `Approvals/ApprovalEndpoints.cs:221` | private copy — *"Mirrors PublishEndpoints.AuthorizeAsync"* |
| `Versioning/VersionActionsEndpoints.cs:66` | private `AuthorizeEditAsync` |
| `Merging/MergeEndpoints.cs:21`, `Editing/EditingEndpoints.cs:22`, `Events/EventEndpoints.cs:16`, `Sharing/ShareEndpoints.cs:49,127` | inline switches |

**This is the top finding despite not being the largest.** A security chokepoint written eight times is
eight places to forget an `includeDeleted` rule or leak an existence check. It holds today — the E12
matrix was extended this pass and passes on every route — but it holds by coincidence of eight
independent implementations agreeing, which is exactly the property that decays.

Replace with `DocumentAuthorization.AuthorizeAsync(db, ctx, docId, Need.Read|Edit)`. Afterwards
`ResolveAsync` and the `AccessResult` enum have one honest caller left (`PushEndpoints.cs:67`, which
wants "has edit, without failing" — that is `(…).Failure is null`); delete both. Bonus:
`DocumentEndpoints.AuthorizeAsync` issues three queries where the shared one issues two, so nine
endpoints lose a round trip.

### 2. Test bootstrap duplicated across 17 files — ~400 lines

`tests/EasyDocs.Api.Tests/TestAuth.cs:15` says it out loud: *"The M0-M2 test files keep their own local
copies; not worth churning them."* That call has expired — 76 local definitions now shadow it:
`AuthedClientAsync` (×19), `Docx` (×12), `CreateDocAsync` (×11), `UploadAsync` (×8), plus `DocxMime`
(×14) and duplicate DTO records (×12). All already exist as extension methods on `TestAuth`. Largest
single deletion available; test-only, which is why it ranks below the security one.

### 3. `act()` written seven times in the SPA — ~45 lines

The identical 9-line `setError('') → await fn() → catch → reload` closure in `Dashboard`, `History`,
`Copies`, `Approvals`, `Settings`, `MembersPanel`, `ActionsMenu`. One `useAct(reload)` hook beside
`problemText` in `api.ts` replaces six. `ActionsMenu`'s variant only adds an `andThen` flag — fold it
in as an optional callback.

### 4. Three copies of `sha256hex(token)`, and a class wrapping two one-liners — ~15 lines

`Auth/ApiTokenService.cs:19`, `Sharing/ShareEndpoints.cs:41`, `Documents/MemberEndpoints.cs:32` are
byte-identical. `ApiTokenService` is a DI-registered class whose whole body is that line plus a 2-line
`Mint()`. Collapse to `static class Tokens { Hash, Mint }` and drop the `AddSingleton`.

### 5. Two speculative interfaces with one implementation each — ~16 lines

`IBlobStore` (only `FileSystemBlobStore`) and `IPasswordHasher` (only `Argon2idPasswordHasher`). Neither
has a test fake — tests resolve the real one from DI or instantiate the concrete class. The rest of the
codebase already made the opposite call correctly and documented it (`WmlComparerDiffService`,
`EventBus`, `LibreOfficePdfRenderer`, `PublishService` are all concrete). An S3 backend later is a
one-line DI swap either way. Also: `IBlobStore.ExistsAsync` has no production caller — its only
reference is a test that exists to test it.

### 6. Smaller, still real

- `Diffing/WmlComparerDiffService.cs:128` and `Merging/WmlComparerMergeService.cs:91` — byte-identical
  `ReadBytesAsync`. ~7 lines.
- `Versioning/Numbering.cs:22` — `Manual` throws three exceptions and returns a tuple its only caller
  discards. It is a non-negative check wearing a costume. ~8 lines.
- The R8 download path written twice (`DocumentEndpoints.cs:77`, `ShareEndpoints.cs:213`) — the exact
  seam that produced the `.docx.docx` and wrong-content-type bugs already fixed twice in the log. ~8
  lines.
- `Diffing/WmlComparerDiffService.cs:114` hand-rolled `Escape` → `WebUtility.HtmlEncode`. 2 lines.
- `api.put` in `web/src/api.ts` — zero callers. 1 line.

---

## Checked and correctly left alone

Roughly thirty other `ponytail:` comments were audited against the code they describe. They are still
correctly deferred and honestly sized: the `Channel<T>` queues, the in-memory folder-tree walk, the
native `<dialog>` modal, the SSE refetch-everything, the magic-byte sniff, the hand-maintained TS
types, the no-light-dismiss actions menu. Several of them argue *against* an abstraction, which is the
right instinct and the reason this codebase reviews as well as it does.

Deliberately not changed: `TryParseRole` duplicated across two files for two different enums (one
generic would save four lines and cost readability), and `ActionsMenu`'s unused `'own'` rung (dead
flexibility that costs one word in a union).
