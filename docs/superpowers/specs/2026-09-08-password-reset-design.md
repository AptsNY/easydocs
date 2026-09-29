# Password reset — design

Status: accepted, 2026-09-08.

An organization Owner mints a single-use, expiring link for a member; the member opens it while
signed out and sets a new password. Structurally an `Invitation`, and it reuses that flow's token
machinery unchanged.

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
- Two endpoints: an authenticated mint, an anonymous consume.
- A per-member control in the org Members panel, and a public page at `/password-reset/:token`.
- Revocation of the target's `ed_` personal access tokens when a reset is consumed.
- Regenerating the checked-in `docs-site/docs/api/openapi/v1.json` snapshot, which the docs site's
  swagger-ui serves and which nothing in CI regenerates.

Out:

- **SMTP and any self-service "Forgot password?" flow.** Rejected below; it is a new runtime
  dependency and needs its own agreement under GOVERNANCE.md.
- **Session invalidation.** Session JWTs are not revocable (ADR-9) and stay valid for up to seven
  days after a reset. Closing that needs the denylist ADR-9 already names as the upgrade path, which
  puts a database read on the hottest request in the app. Documented, not built.
- **Changing a password you already know.** `PasswordHash` is written only by `Register` today, and
  this design does not change that. A signed-in member who simply wants to rotate their password
  still has to ask an Owner. That is a consequence of this design, so it is documented alongside the
  other gaps rather than left implicit.
- **Recovery for a sole Owner.** Someone who is the only Owner of their org has nobody who can mint
  them a link, and someone active on a second team is refused by the cross-org gate. See
  *Deliberate gaps*.

## Decisions

### `POST /api/v1/org/members/{uid:guid}/password-reset`

Joins the existing `app.MapGroup("/api/v1/org").RequireAuthorization().WithTags("Org")` group, so it
inherits authentication and the `org` claim requirement. Carries
`RequireRateLimiting(RateLimits.TokenMint)` — that policy already partitions on the caller's `sub`,
which is the right key for an authenticated mint.

Gates, in order:

| Condition | Status | Why this code |
|---|---|---|
| Caller is not Owner or Admin | 403 | No business here at all |
| Caller is Admin and target is Owner or Admin | 403 | See below — this is the escalation gate |
| Target is not a member of the caller's org | 404 | `SwitchOrg`'s rule: a non-member must not learn whether the account exists |
| Target is active on another team | 409 | The cross-org rule below — the detail names it explicitly |
| Target has no `PasswordHash` (SSO-only) | 409 | There is no password to reset; a link would be a dead end |

**An Admin may reset Members only.** The obvious gate to copy is `Invite`'s
`caller.Role is not (OrgRole.Owner or OrgRole.Admin)`, and copying it would be wrong: `Invite` is the
*weakest* org action, and this is the sharpest one in the codebase. `UpdateRole` and `Remove` are both
`caller.Role != OrgRole.Owner`, carrying the comment *"changing someone else's org role is the
sharpest of these actions."* A password reset is strictly sharper than a role change — it is full
account takeover, after which an Admin signs in as the Owner and promotes themselves. MFA does not
close that, because `TotpEnabledAt` is nullable and opt-in. So Owners may reset anyone; Admins may
reset Members, which is the common case and the reason to let Admins do this at all.

**"Active on another team" means a second org that has someone else in it.** The obvious rule —
refuse any target belonging to more than one org — is unusable here, and the reason is worth stating
because it is not obvious from the schema. `Register` *always* creates an org, and
`InvitationEndpoints.Accept` requires a session, so an invited colleague must register (acquiring a
personal org) before they can accept. Every invited member therefore belongs to at least two orgs,
and a naive multi-org gate would refuse the entire population this feature exists to serve, admitting
only founding Owners who were never invited anywhere. Counting *other orgs that have more than one
member* skips the vestigial personal org registration hands out and still refuses anyone genuinely
active on a second team. For the same reason, "holds Owner or Admin elsewhere" does not work either:
everyone is Owner of their own personal org.

What this gives up, deliberately: resetting a colleague also grants access to their personal org.
That org holds only their own documents, and the reset already hands over their account, so the
marginal loss is nil. What it keeps: no Admin of one team can take over an account that is active on
another team.

This is the same hazard as the same-org rule above, in its cross-org form. Both close it structurally
rather than by policy.

On success the endpoint marks any outstanding unused reset for that user as used, inserts the new row,
writes a `password_reset.issued` audit event, and returns `{ token, url, expiresAt }`. The raw token
appears in that response and nowhere else, exactly like a minted PAT or a share link.

`url` is **relative** — `"/password-reset/{token}"` — matching `ShareEndpoints`, which returns
`url = $"/s/{token}"`. Synthesizing an absolute URL would mean trusting `Host` or the forwarded
headers behind the reverse proxy the README documents as the TLS terminator: a config footgun and a
Host-header injection surface, on a link an admin is about to paste into a chat window.

### `POST /api/v1/auth/password-reset:complete`

Anonymous — deliberately **not** `RequireAuthorization()`. This is the one real difference from
invitation-accept, which requires a session because the token proves *someone* was invited while the
session proves *who is claiming it*. A locked-out user has no session, so here the token is the sole
credential. That is what forces the short expiry, the single use, and the uniform 404 below.

Body is `{ token, password }`; password minimum 12 characters, the same rule `Register` enforces.

**The token goes in the body, not the path, and that is a security requirement rather than a style
preference.** `RateLimits.Auth` partitions on `$"{ctx.Request.Path}|{ClientKey(ctx)}"`. A route shaped
`/api/v1/auth/password-reset/{token}:complete` therefore gives *every guessed token its own partition
and its own fresh bucket*, leaving the endpoint effectively unthrottled — the exact opposite of what
carrying the policy implies. It would also break the invariant `RateLimits.cs` states two lines above
that key: *"Only two endpoints carry this policy, so the key space stays bounded — no
partition-per-URL growth."* A fixed path restores per-IP metering and keeps the secret out of proxy
access logs. (`/s/{token}` is not a counter-example — `AnonShare` keys on `ClientKey(ctx)` alone.
`/api/v1/invitations/{token}:accept` is not either — it carries no rate-limit policy.)

The SPA page keeps the token in *its* path (`/password-reset/:token`, the link the admin sends) and
posts it in the body. Only the credential-accepting endpoint needs the fixed path.

Unknown, expired, and already-used tokens all return an identical **404**, following
invitation-accept's "a probe learns nothing about which tokens exist." Identical means the response
bodies are byte-equal, not merely three 404 statuses — `Problem.Of` takes a free-text detail, and
three helpfully-worded details would rebuild the oracle the uniform status exists to remove.

The cross-org check from the mint is **re-evaluated here**, not only at issue time. The token lives an
hour, and within that hour the target can accept an invitation to a second team, which would let an
outstanding token reset an account the gate would now refuse. It returns the same **404** as every
other failure here, not the mint's 409 — at this endpoint the caller is anonymous, and a distinct
status would tell an unauthenticated holder something about the account behind the token.

On success, in a single `SaveChangesAsync` (the repo idiom — `// single SaveChanges = one transaction`
in `AuthEndpoints`):

1. `PasswordHash` is set through `IPasswordHasher`, so the stored format is whatever `Hash` currently
   emits and no caller needs to know it.
2. `UsedAt` is stamped.
3. Every live `ed_` token for that user gets `RevokedAt = now`. The column already exists; no schema
   change is needed. These rows are loaded and stamped through the change tracker rather than with
   `ExecuteUpdateAsync`, which would run as its own statement outside the save.
4. A `password_reset.consumed` audit event is written.

Two things it deliberately does **not** do:

- **It does not touch `TotpEnabledAt`.** MFA survives the reset and still challenges on the next
  login, so an admin-issued reset is not an MFA bypass. A user who has also lost their authenticator
  uses the recovery codes issued at MFA setup — that is what they are for.
- **It does not issue a session cookie.** The response sends the user to `/login`. Signing them in
  would turn the link itself into a session and would skip the MFA challenge that the previous point
  preserved.

Two concurrent posts of the same token both read `UsedAt == null` and both succeed. Accepted: it is
the same link and the same person, the last write wins, and both writes set a password the holder
chose. Making it atomic would mean an `ExecuteUpdateAsync … WHERE UsedAt == null` that breaks the
single-save above for no gain.

### `PasswordResets`

Mirrors `Invitation`, minus the parts a reset does not need.

| Column | Notes |
|---|---|
| `Id`, `UserId`, `OrgId`, `CreatedAt` | `OrgId` scopes the audit event |
| `TokenHash` | SHA-256 hex from the existing `MemberEndpoints.HashToken`; unique index, as on `Invitations`, `ShareLinks` and `ApiTokens` |
| `ExpiresAt` | Non-nullable, one hour |
| `UsedAt` | Nullable; single use |

The raw token is `Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24))` — the same mint
`MemberEndpoints` already uses for invitations.

There is no `IssuedBy`. `Invitation` carries one, but nothing in this flow reads it, and the
`password_reset.issued` audit event already records the actor as its subject.

`ExpiresAt` is non-nullable where `Invitation.ExpiresAt` is nullable: an invitation that never expires
is a defensible convenience, a password reset that never expires is a permanent skeleton key. One hour
is short because the admin relays the link out of band and then it should die.

There is no `RevokedAt`. Issuing a new link stamps `UsedAt` on the outstanding one, which is the only
revocation this flow needs; a separate column would be a second way to express the same state. The
cost is that `UsedAt` means "consumed *or* superseded" — worth it, because re-issuing is what an admin
does when the first link went to the wrong chat window.

All foreign keys are `Restrict`, per the house rule in `EasyDocsDbContext`.

A separate table rather than a reused `Invitations` row is what keeps a reset token from being POSTed
to `/api/v1/invitations/{token}:accept` to buy org membership: both mints draw from the same CSPRNG
and hash with the same function, so shared storage would make the two token types interchangeable.

### Web UI

Three touches, each reusing a pattern already on screen:

- **Members panel** — a per-member "Reset password" control, rendered for Owners, and for Admins only
  against Members. The resulting link is shown once with a copy button, the same treatment
  `Settings.tsx` already gives a freshly minted PAT ("shown once and cannot be recovered").
- **`/password-reset/:token`** — a public route registered outside `RequireAuth`, next to `/s/:token`.
  New password plus confirmation; on success it directs to `/login`.
- **Login screen** — a static line: "Forgot your password? Ask an organization owner or admin for a
  reset link." Not a button, because there is nothing self-service to click. Saying so is better than
  leaving people hunting for a control that does not exist, which is the report that prompted this work.

### Deliberate gaps

All three go in SECURITY.md, which already separates these categories:

- *Functional limitations* — a sole Owner has nobody to mint them a link, and anyone active on a
  second team is refused by the cross-org gate; for those accounts the routes are SSO or an operator
  writing to the database. And a signed-in member cannot change a password they already know.
- *Security-relevant* — a reset revokes `ed_` tokens but not sessions, so a stolen session cookie
  keeps working for up to seven days. This is the same limitation ADR-9 already records for sign-out.

## Tests

`tests/EasyDocs.Api.Tests/PasswordResetTests.cs`:

- A Member calling the mint gets 403.
- An Admin resetting an Owner gets 403; an Admin resetting another Admin gets 403; an Admin resetting
  a Member succeeds; an Owner resetting an Owner succeeds. *(The escalation gate, which is the reason
  this endpoint is not simply Owner/Admin like `Invite`.)*
- A target in another org gets 404, not 403.
- A target active on a second team (an org with another member in it) gets 409 at mint, while a target
  whose only other org is their solo personal one succeeds. *(The second half is the test that fails
  if anyone "simplifies" the gate back to counting orgs.)*
- A token issued before the target joined a second team returns **404** at consume, and the target's
  old password still works.
- A target with a null `PasswordHash` gets 409.
- Consuming an unknown, an expired, and an already-consumed token each return 404 with **byte-equal
  response bodies**.
- Two consumes of two *different* bogus tokens from one client draw from the same rate-limit bucket.
  *(This is the test that fails if the token ever moves back into the path.)*
- A password under 12 characters gets 400.
- After a successful consume, `POST /auth/login` with the new password returns 200.
- After a successful consume, a previously working `ed_` token gets 401. *(The PAT revocation is the
  half of this feature with no visible UI, so it needs the explicit test.)*
- An account with MFA armed still gets `mfaRequired` after a reset.
- Issuing a second link invalidates the first.

No `E12_Security.cs` additions. The consume endpoint is anonymous and org-less — it resolves purely by
token hash, so an "outsider" executes the identical code path and the assertion would be vacuous — and
the mint's cross-org case is already covered above.

`web/e2e/password-reset.spec.ts` drives the whole loop against the built image: an Admin issues a
link from the Members panel, signs out, opens the link, sets a password, and signs in with it. It is
the most expensive test here and it stays, because it is the only thing exercising a route registered
*outside* `RequireAuth` — a classic place for a later refactor to silently re-gate the page.

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
Admin take over an Owner's account inside their own org, and lets an Admin of one team take over an
account active on another. Rejected in favour of the two gates above, which remove both paths instead
of documenting them.

**Refusing any target who belongs to more than one org.** The first draft of this design, and wrong:
registration always creates an org, so every invited member is multi-org and the gate would refuse
everyone except founding Owners — a feature whose happy path is unreachable. Counting only other orgs
with more than one member keeps the guarantee that matters and costs the vestigial personal org,
which the reset hands over anyway.

**Also invalidating sessions.** Would make a reset mean "lock everyone else out," which is what people
assume it means. Rejected as out of proportion: the JWT path is stateless today, and a
`SessionsValidFrom` check would put a database read on every authenticated request and amend ADR-9.
Revoking `ed_` tokens closes the credential that has no fixed expiry; the seven-day one is documented.

**Signing the user in on consume.** Friendlier by one click, at the cost of turning a link that was
relayed through a chat message into a live session and skipping the MFA challenge. Rejected.

**An operator CLI instead of an in-app flow.** Solves the locked-out-owner case and nothing else —
every future member with a forgotten password would need someone with shell access. Rejected as the
primary flow, though it remains what the gaps above fall back to.
