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
org management, and the pair `TokenEndpoints.VisibleAsync` already treats as owning service tokens.

| Route | Behaviour |
|---|---|
| `POST /service-accounts` `{name}` | 400 on blank name. Creates the user + org membership. Audit `service_account.created`. 201 `{userId, name, email}` |
| `GET /service-accounts` | This org's service accounts: `{userId, name, email, liveTokens, lastUsedAt, createdAt}`. `liveTokens` counts not revoked and not expired. |
| `POST /service-accounts/{uid}/tokens` `{name}` | 404 if `uid` is not a service account in this org. Mints an `ed_` token owned by `uid`, org = this org. Audit `token.created` (actor = the caller). Rate-limited with `RateLimits.TokenMint`. 201 `{id, token}` — raw value returned once. |
| `DELETE /service-accounts/{uid}` | 404 as above. Revokes all its live tokens, removes its `OrgMembers` and `DocumentMembers` rows in this org. The `Users` row stays — audit rows and version authorship reference it (`ON DELETE RESTRICT`). Audit `service_account.removed`. 204. |

Existing routes that change:

- `GET /org/members` and `GET /documents/{id}/members` add `isService` to each row.
- `POST /documents/{id}/members {email, role}` needs no change: the service account is already an
  org member, so it takes the direct-grant branch.

## Refusals — a service account cannot act as a person

A service user can hold no session: it has no password, and its `.invalid` email matches no IdP.
But its `ed_` token authenticates it on every `RequireAuthorization()` route, so the person-only
routes refuse `IsService` callers explicitly (403, one shared helper):

- `POST /api/v1/tokens` — it must not mint its own tokens
- `POST /api/v1/invitations/{token}:accept`
- MFA setup / enable / disable

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
  documents it was added to; each refusal above; remove revokes tokens (next call 401).
- The decisive test: the creator's password reset does **not** affect a service-account token.
- e2e: `reset-kills-integration-token.spec.ts` gains a companion — create a service account in
  Settings, add it to a document, mint its token, reset the creator's password, the token still 200s.

## Out of scope

Scopes (stored, enforced nowhere yet — unchanged). Expiry controls in the UI (the API field exists).
A service account in more than one org. Converting an existing person account into a service account.
