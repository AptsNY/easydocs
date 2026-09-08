# Password reset — design

Status: accepted, 2026-09-08.

An organization Owner or Admin mints a single-use, expiring link for a member; the member opens it
while signed out and sets a new password. Structurally an `Invitation`, and it reuses that flow's
token machinery unchanged.

## Problem

easydocs has no password reset. The auth surface is `register`, `login`, `login/mfa`, `logout`,
`switch-org` and `oidc/*` — nothing else. A member who forgets their password has no route back into
their account, and an operator's only recourse is to write a hash into the `Users` table by hand.

This is not an oversight that can be closed by adding the conventional "Forgot password?" button,
because **the stack has no mailer**. There is no SMTP configuration and no mail container; the compose
stack is app + PostgreSQL + Collabora. Invitations already work around this by being links the inviter
copies and delivers out of band. Password reset takes the same route, for the same reason.

## Scope

In:

- A new `PasswordResets` table, one migration.
- Two endpoints: an authenticated mint for Owners/Admins, an anonymous consume.
- A per-member control in the org Members panel, and a public page at `/password-reset/:token`.
- Revocation of the target's `ed_` personal access tokens when a reset is consumed.

Out:

- **SMTP and any self-service "Forgot password?" flow.** Rejected below; it is a new runtime
  dependency and needs its own agreement under GOVERNANCE.md.
- **Session invalidation.** Session JWTs are not revocable (ADR-9) and stay valid for up to seven
  days after a reset. Closing that needs the denylist ADR-9 already names as the upgrade path, which
  puts a database read on the hottest request in the app. Documented, not built.
- **Recovery for a sole Owner.** Someone who is the only Owner of their org, or who belongs to more
  than one org, cannot be helped by this flow. See *Deliberate gaps*.

## Decisions

### `POST /api/v1/org/members/{uid:guid}/password-reset`

Joins the existing `app.MapGroup("/api/v1/org").RequireAuthorization().WithTags("Org")` group, so it
inherits authentication and the `org` claim requirement. Carries
`RequireRateLimiting(RateLimits.TokenMint)` — that policy already partitions on the caller's `sub`,
which is the right key for an authenticated mint.

Gates, in order:

| Condition | Status | Why this code |
|---|---|---|
| Caller is not Owner or Admin | 403 | Same `caller.Role is not (OrgRole.Owner or OrgRole.Admin)` test `Invite` uses |
| Target is not a member of the caller's org | 404 | `SwitchOrg`'s rule: a non-member must not learn whether the account exists |
| Target belongs to more than one org | 409 | The deliberate limitation below — the detail names it explicitly |
| Target has no `PasswordHash` (SSO-only) | 409 | There is no password to reset; a link would be a dead end |

On success it marks any outstanding unused reset for that user as used, inserts the new row, writes a
`password_reset.issued` audit event, and returns `{ token, url, expiresAt }`. The raw token appears in
that response and nowhere else, exactly like a minted PAT or a share link.

The multi-org 409 is the security-load-bearing check. A password reset hands over the whole account,
not access to one org, so without it an Admin of one org could take over an account that holds Owner
rights in another. Restricting the target to single-org accounts closes that path structurally rather
than by policy.

### `POST /api/v1/auth/password-reset/{token}:complete`

Anonymous — deliberately **not** `RequireAuthorization()`. This is the one real difference from
invitation-accept, which requires a session because the token proves *someone* was invited while the
session proves *who is claiming it*. A locked-out user has no session, so here the token is the sole
credential. That is what forces the short expiry, the single use, and the uniform 404 below.

Carries `RequireRateLimiting(RateLimits.Auth)` — an anonymous endpoint that accepts a secret is a
credential-guessing surface and belongs on the same budget as login.

Body is `{ password }`, minimum 12 characters, the same rule `Register` enforces.

Unknown, expired, and already-used tokens all return an identical **404**, following
invitation-accept's "a probe learns nothing about which tokens exist."

On success, inside one transaction:

1. `PasswordHash` is set through `IPasswordHasher`, so the stored format is whatever `Hash` currently
   emits and no caller needs to know it.
2. `UsedAt` is stamped.
3. Every live `ed_` token for that user gets `RevokedAt = now`. The column already exists; no schema
   change is needed for this.
4. A `password_reset.consumed` audit event is written.

Two things it deliberately does **not** do:

- **It does not touch `TotpEnabledAt`.** MFA survives the reset and still challenges on the next
  login, so an admin-issued reset is not an MFA bypass. A user who has also lost their authenticator
  uses the recovery codes issued at MFA setup — that is what they are for.
- **It does not issue a session cookie.** The response sends the user to `/login`. Signing them in
  would turn the link itself into a session and would skip the MFA challenge that step 1 preserved.

### `PasswordResets`

Mirrors `Invitation`, minus the parts a reset does not need.

| Column | Notes |
|---|---|
| `Id`, `UserId`, `OrgId`, `IssuedBy`, `CreatedAt` | `OrgId` scopes the audit event |
| `TokenHash` | SHA-256 hex from the existing `MemberEndpoints.HashToken`; unique index, as on `Invitations`, `ShareLinks` and `ApiTokens` |
| `ExpiresAt` | Non-nullable, one hour |
| `UsedAt` | Nullable; single use |

The raw token is `Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24))` — the same mint
`MemberEndpoints` already uses for invitations.

`ExpiresAt` is non-nullable where `Invitation.ExpiresAt` is nullable: an invitation that never expires
is a defensible convenience, a password reset that never expires is a permanent skeleton key. One hour
is short because the admin relays the link out of band and then it should die.

There is no `RevokedAt`. Issuing a new link marks the outstanding one used, which is the only
revocation this flow needs; a separate column would be a second way to express the same state.

All foreign keys are `Restrict`, per the house rule in `EasyDocsDbContext`.

### Web UI

Three touches, each reusing a pattern already on screen:

- **Members panel** — a per-member "Reset password" control, rendered only for Owners and Admins.
  The resulting link is shown once with a copy button, the same treatment `Settings.tsx` already gives
  a freshly minted PAT ("shown once and cannot be recovered").
- **`/password-reset/:token`** — a public route registered outside `RequireAuth`, next to `/s/:token`.
  New password plus confirmation; on success it directs to `/login`.
- **Login screen** — a static line: "Forgot your password? Ask an organization owner or admin for a
  reset link." Not a button, because there is nothing self-service to click. Saying so is better than
  leaving people hunting for a control that does not exist, which is the report that prompted this work.

### Deliberate gaps

Both go in SECURITY.md, which already separates these two categories:

- *Functional limitations* — a sole Owner, or any user who belongs to more than one org, cannot be
  recovered through this flow. For those accounts the routes are SSO, or an operator writing to the
  database directly.
- *Security-relevant* — a reset revokes `ed_` tokens but not sessions, so a stolen session cookie
  keeps working for up to seven days. This is the same limitation ADR-9 already records for sign-out.

## Tests

`tests/EasyDocs.Api.Tests/PasswordResetTests.cs`:

- A Member calling the mint gets 403; an Owner and an Admin both succeed.
- A target in another org gets 404, not 403.
- A target belonging to two orgs gets 409.
- A target with a null `PasswordHash` gets 409.
- Consuming an unknown, an expired, and an already-consumed token each return 404, and the three
  responses are indistinguishable.
- A password under 12 characters gets 400.
- After a successful consume, `POST /auth/login` with the new password returns 200.
- After a successful consume, a previously working `ed_` token gets 401. *(The PAT revocation is the
  half of this feature with no visible UI, so it needs the explicit test.)*
- An account with MFA armed still gets `mfaRequired` after a reset, and `TotpEnabledAt` is unchanged.
- Issuing a second link invalidates the first.

`E12_Security.cs` gains cross-org guards for both new routes, following the pattern in
`Every_version_and_document_scoped_route_is_404_for_another_org`.

`web/e2e/password-reset.spec.ts` drives the whole loop against the built image: an Admin issues a
link from the Members panel, signs out, opens the link, sets a password, and signs in with it.

CI forbids skips, so none of these may be conditional on environment.

## Rejected alternatives

**SMTP and a self-service "Forgot password?" link.** The conventional design, and the only one that is
genuinely self-service. Rejected for now on two counts: it is a new runtime dependency, which
GOVERNANCE.md puts behind its own agreement, and it does not actually solve the case that prompted
this work — an install with no SMTP configured leaves the locked-out user exactly where they started.
If it is built later, the natural shape is the one OIDC already uses: the login screen grows the
control only when the configuration keys are present. This design does not foreclose that; the
`PasswordResets` table is the same table a mailed link would use.

**Any org Admin may reset any member.** Simpler and matches how most products behave, but lets an
Admin of one org take over an account holding Owner rights in another. Rejected in favour of the
single-org restriction, which removes the path instead of documenting it.

**Also invalidating sessions.** Would make a reset mean "lock everyone else out," which is what people
assume it means. Rejected as out of proportion: the JWT path is stateless today, and a
`SessionsValidFrom` check would put a database read on every authenticated request and amend ADR-9.
Revoking `ed_` tokens closes the credential that has no fixed expiry; the seven-day one is documented.

**Signing the user in on consume.** Friendlier by one click, at the cost of turning a link that was
relayed through a chat message into a live session and skipping the MFA challenge. Rejected.

**An operator CLI instead of an in-app flow.** Solves the locked-out-owner case and nothing else —
every future member with a forgotten password would need someone with shell access. Rejected as the
primary flow, though it remains what the two gaps above fall back to.
