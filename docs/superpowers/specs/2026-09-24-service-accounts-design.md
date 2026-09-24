# Service accounts — design

*Status: approved 2026-09-24. RFC: see the linked issue.*

## Problem

An integration authenticates with an `ed_` token, and every token today is owned by a **person**.
Anything that happens to that person's account happens to the integration. The concrete case:
completing a password reset revokes every token the account owns (`PasswordResetEndpoints.Complete`,
deliberately — a reset is a credential rotation). A reset of the person whose token an integration
used broke that integration silently until the next time it was used.

Mitigations already shipped: minting a reset link now reports the tokens it will revoke
(`revokesApiTokens`), and a deployment can alert on 401s to its integration. Both *detect*. This
design *removes* the coupling: an integration gets an identity of its own that no person can lose.

## Decision

A **service account** is an ordinary `Users` row that can never sign in. It is created by an org
Owner or Admin, joins documents exactly like a person, and its tokens are minted *for* it by an
Owner or Admin.

Rejected alternative: user-less tokens (`ApiToken.UserId = null` + `ServiceName`, as the v1 schema
sketched). Every authorization path — `DocumentAuthorization`, `CurrentUser.UserId`, audit actors,
document membership — is keyed on a user id; a non-user principal needs a parallel path through all
of them. A user row with no way in reuses all of it unchanged.

## Data

One migration: `Users.IsService boolean NOT NULL DEFAULT false`.

A service account is a `Users` row with:

| Column | Value |
|---|---|
| `IsService` | `true` |
| `PasswordHash` | `null` — local login's existing `PasswordHash is null` check already 401s it |
| `Email` | generated: `svc-<slug(name)>-<first 8 hex of id>@service.invalid` (unique index already exists; `.invalid` is reserved by RFC 2606, so no identity provider can assert it) |
| `DisplayName` | the name given at creation |

plus one `OrgMembers` row, role `Member`, in the creating org. No personal org is created.
`ApiToken.UserId` is the service user's id; nothing about tokens changes shape.

## API

All under the existing `/api/v1/org` group (session or `ed_` token, `org` claim required). All four
are gated to the caller's org role **Owner or Admin** — the gate `OrgEndpoints` already uses for
org management.

| Route | Behaviour |
|---|---|
| `POST /service-accounts` `{name}` | 400 on blank name. Creates the user + org membership. Audit `service_account.created`. 201 `{userId, name, email}` |
| `GET /service-accounts` | This org's service accounts (queried from `Users.IsService` joined to this org's `OrgMembers`): `{userId, name, email, liveTokens, lastUsedAt, createdAt}`. `liveTokens` counts not revoked and not expired. |
| `POST /service-accounts/{uid}/tokens` `{name}` | 404 if `uid` is not a service account in this org. Mints an `ed_` token owned by `uid`, org = this org. Audit `token.created` (actor = the caller). Rate-limited with `RateLimits.TokenMint`. 201 `{id, token}` — raw value returned once. |
| `DELETE /service-accounts/{uid}` | 404 as above. Revokes all its live tokens, removes its `OrgMembers` and `DocumentMembers` rows in this org. The `Users` row stays — audit rows and version authorship reference it (`ON DELETE RESTRICT`). Audit `service_account.removed`. 204. |

Existing routes that change:

- `GET /org/members` and `GET /documents/{id}/members` add `isService` to each row.
- `POST /documents/{id}/members {email, role}` takes the existing direct-grant branch (the service
  account is an org member), but **caps the role at Editor**: `Owner` for a service account is 400.
  `PATCH /documents/{id}/members/{uid}` applies the same cap. An Owner grant would let a token
  re-share the document and mint invitations, and a service account could become a document's last
  owner — which `DELETE /service-accounts` would then strip, orphaning the document.
- `DELETE /org/members/{uid}` on a service account is 409 "Use DELETE /service-accounts/{uid}".
  Plain removal deletes only the `OrgMembers` row; `DocumentAuthorization` never reads `OrgMembers`,
  so the tokens would keep full document access while dropping out of every list.
- `TokenEndpoints.VisibleAsync`: `manages` today covers only `UserId == null` tokens, which no code
  path creates. It widens to tokens whose owner is an `IsService` member of this org, so an
  Owner/Admin can list and revoke a single leaked service token through the existing
  `GET/DELETE /api/v1/tokens` without deleting the account. The stale comment is updated.
- `POST /versions/{vid}/approvals`: an `IsService` id in `approverIds` is 400. Respond authorizes on
  `ApproverId` alone, so a token holder could otherwise approve their own request.
- `PasswordResetEndpoints.OnAnotherTeamAsync` excludes `IsService` users from its per-org member
  count. Otherwise creating a service account in your personal org makes you "active on another
  team" and un-resettable from every other org.
- `POST /auth/register` refuses emails ending `@service.invalid`, so that domain means "service" only.

## Refusals — a service account cannot act as a person

A service user cannot *sign in*: it has no password, and its `.invalid` email matches no IdP. But
its `ed_` token authenticates it on every `RequireAuthorization()` route — and some of those mint a
session. `POST /auth/switch-org` writes a 7-day `ed_session` JWT for any member, which would turn a
service token into a credential that survives the token's revocation.

So person-only routes refuse `IsService` callers with 403. Mechanism: one endpoint filter,
`PersonOnly()` (`.AddEndpointFilter`), that loads the caller's `Users.IsService` — applied per route
so the hot document/version paths pay no extra read. Applied to:

- `POST /api/v1/auth/switch-org` — mints a session
- `POST /api/v1/invitations/{token}:accept` — mints a session
- `GET/POST/DELETE /api/v1/tokens` — its own tokens are managed by an Owner/Admin, never by itself
- every `/api/v1/account/mfa*` route
- `POST /versions/{vid}/share-links` — an integration has no need to mint anonymous links

And as a **target**:

- password reset mint: already 409 `No password` (no change, covered by a test)
- OIDC `complete`: refuse to sign in a user with `IsService` (defence in depth behind `.invalid`)
- `PATCH /org/members/{uid}` role change: 409 — a service account is always `Member`; its reach is
  its document roles

## UI

Settings gains **Service accounts**, visible to Owner/Admin: a create box (name), and a row per
account — name, email (copyable, for adding it to documents), live tokens, last used, **New token**,
**Remove**. A new token is shown once, like personal tokens. Member lists render service accounts
as `<name> (service)` and hide the password-reset and role controls for them.

## Testing

- `ServiceAccountTests` (xUnit, Testcontainers): create/list/mint/remove happy paths; Member caller
  gets 403 on all four; cross-org `uid` is 404; the token authenticates and reaches exactly the
  documents it was added to; remove revokes tokens (next call 401).
- One test per refusal and per changed route above — including: switch-org with a service token is
  403 and sets no cookie; Owner grant is 400; `DELETE /org/members` is 409; an Owner can list and
  revoke one service token via `/api/v1/tokens`; a service approver is 400; a person with a service
  account in their personal org is still resettable from a team org.
- The decisive test: the creator's password reset does **not** affect a service-account token.
- e2e: `reset-kills-integration-token.spec.ts` gains a companion — create a service account in
  Settings, add it to a document, mint its token, reset the creator's password, the token still 200s.

## Also in this change (GOVERNANCE.md)

An RFC issue before code; spec §10.1, the committed OpenAPI document and `CHANGELOG.md` updated in the
same PR. The SPA's approver picker hides service accounts, matching the 400.

## Out of scope

Scopes (stored, enforced nowhere yet — unchanged). Expiry controls in the UI (the API field exists).
A service account in more than one org. Converting an existing person account into a service account.
