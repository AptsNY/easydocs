# Service accounts — design

*Status: approved 2026-09-24. RFC: AptsNY/easydocs#60.*

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

A **service account** is an ordinary `Users` row that can never sign in. An org Owner or Admin
creates it and becomes its **manager**. It joins documents like a person (capped at Editor), and only
its manager can mint its tokens.

Why the manager alone mints: whoever can mint a token holds the account's access, including documents
other people added it to. "Org membership grants no implicit document access"
(`DocumentAuthorization.cs`) must keep holding, so minting cannot follow org role. A document owner
who adds a service account sees who manages it and is knowingly trusting that person, the same way
they trust any member they add. Revoking a token or deleting the account grants nothing, so org
Owners and Admins can also do those, as an emergency brake.

Rejected alternative: user-less tokens (`ApiToken.UserId = null` + `ServiceName`, as the v1 schema
sketched). Every authorization path — `DocumentAuthorization`, `CurrentUser.UserId`, audit actors,
document membership — is keyed on a user id; a non-user principal needs a parallel path through all
of them. A user row with no way in reuses all of it unchanged.

## Data

One migration: `Users.ManagedBy uuid NULL`, FK → `Users.Id` `ON DELETE RESTRICT`. Non-null means
"service account"; `IsService` below is shorthand for `ManagedBy IS NOT NULL`.

A service account is a `Users` row with:

| Column | Value |
|---|---|
| `ManagedBy` | the creating user's id |
| `PasswordHash` | `null` — local login's existing `PasswordHash is null` check already 401s it |
| `Email` | generated: `svc-<slug(name)>-<first 8 hex of id>@service.invalid` (unique index already exists; `.invalid` is reserved by RFC 2606, so no identity provider can assert it) |
| `DisplayName` | the name given at creation |

plus one `OrgMembers` row, role `Member`, in the creating org. No personal org is created.
`ApiToken.UserId` is the service user's id; nothing about tokens changes shape.

## API

All under the existing `/api/v1/org` group (session or `ed_` token, `org` claim required). Gates:
create needs org role Owner or Admin (the gate `OrgEndpoints` uses for org management); list shows
Owner/Admin every account and anyone else the accounts they manage; mint needs the caller to be the
account's manager; delete needs manager **or** Owner/Admin. A manager is always an org member (see
`DELETE /org/members` below), whatever their role.

| Route | Behaviour |
|---|---|
| `POST /service-accounts` `{name}` | 400 on blank name. Creates the user + org membership. Audit `service_account.created`. 201 `{userId, name, email}` |
| `GET /service-accounts` | This org's service accounts (queried from `Users.ManagedBy` joined to this org's `OrgMembers`): `{userId, name, email, managedBy: {userId, displayName}, liveTokens, lastUsedAt, createdAt}`. `liveTokens` counts not revoked and not expired. |
| `POST /service-accounts/{uid}/tokens` `{name, expiresAt?}` | 404 if `uid` is not a service account in this org; 403 unless the caller is its manager and still a member of this org. Mints an `ed_` token owned by `uid`, org = this org. Audit `token.created` (actor = the caller). Rate-limited with `RateLimits.TokenMint`. 201 `{id, token}` — raw value returned once. |
| `DELETE /service-accounts/{uid}` | 404 as above. First, every document the account is **sole Owner** of (`POST /documents`, `:import` and `/copies` make their caller sole Owner, and an ingesting integration will create documents) gets its **manager** added as Owner — no new access, since the manager could already reach them by minting a token. Then revokes all its live tokens and removes its `OrgMembers` and `DocumentMembers` rows in this org. The `Users` row stays — audit rows and version authorship reference it (`ON DELETE RESTRICT`). Audit `service_account.removed` with the handed-over document ids. 204. |

Existing routes that change:

- `GET /org/members` and `GET /documents/{id}/members` add `managedBy: {userId, displayName} | null`
  to each row (null = a person).
- `POST /documents/{id}/members {email, role}` takes the existing direct-grant branch (the service
  account is an org member), but **caps the role at Editor**: `Owner` for a service account is 400.
  `PATCH /documents/{id}/members/{uid}` applies the same cap. Keeps a service account from being handed
  ownership of someone else's document: ownership arises only from its own creations, which
  `DELETE /service-accounts` hands to its manager.
- `DELETE /org/members/{uid}` on a service account is 409 "Use DELETE /service-accounts/{uid}".
  On a person who **manages** a service account in this org it is also 409 ("delete their service
  accounts first"), like the last-owner guard — otherwise an account nobody can mint for, whose
  documents nobody can reach, could never be cleaned up.
  Plain removal deletes only the `OrgMembers` row; `DocumentAuthorization` never reads `OrgMembers`,
  so the tokens would keep full document access while dropping out of every list.
- `TokenEndpoints.VisibleAsync`: `manages` today covers only `UserId == null` tokens, which no code
  path creates. It widens to tokens whose owner is an `IsService` member of this org, so an
  Owner/Admin can list and revoke a single leaked service token through the existing
  `GET/DELETE /api/v1/tokens` without deleting the account. List rows gain
  `serviceAccount: {userId, name} | null`, so a leaked token is identifiable among them. (Mint is not widened — that stays on
  `POST /service-accounts/{uid}/tokens`, manager only.) The stale comment is updated.
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
`PersonOnly()` (`.AddEndpointFilter`), that loads the caller's `Users.ManagedBy` — applied per route
so the hot document/version paths pay no extra read. Applied to:

- `POST /api/v1/auth/switch-org` — mints a session
- `POST /api/v1/invitations/{token}:accept` — mints a session
- `GET/POST/DELETE /api/v1/tokens` — its manager mints its tokens and the manager or an Owner/Admin
  lists and revokes them; the account never manages its own
- every `/api/v1/account/mfa*` route
- `POST /versions/{vid}/share-links` — an integration has no need to mint anonymous links
- the invitation branch of `POST /documents/{id}/members` (unknown or out-of-org email) — it returns
  a raw invitation token, and an integration sole-owning a document it created must not mint them.
  Adding existing org members to its own documents stays allowed.

And as a **target**:

- password reset mint: already 409 `No password` (no change, covered by a test)
- OIDC `complete`: refuse to sign in a user with `IsService` (defence in depth behind `.invalid`)
- `PATCH /org/members/{uid}` role change: 409 — a service account is always `Member`; its reach is
  its document roles

## UI

Settings gains **Service accounts**, visible to Owner/Admin and to anyone managing one: a create box (name), and a row per
account — name, email (copyable, for adding it to documents), live tokens, last used, **New token**,
**Remove**. A new token is shown once, like personal tokens. **New token** shows only for the account's manager.
Member lists render service accounts as `<name> (service, managed by <manager>)`, including in the
add-member result, and hide the password-reset and role controls for them.

## Testing

- `ServiceAccountTests` (xUnit, Testcontainers): create/list/mint/remove happy paths; Member caller
  who manages nothing gets 403 on create and mint, and an empty list; cross-org `uid` is 404; the token authenticates and reaches exactly the
  documents it was added to; remove revokes tokens (next call 401).
- One test per refusal and per changed route above — including: switch-org with a service token is
  403 and sets no cookie; Owner grant is 400; `DELETE /org/members` is 409; an Owner can list and
  revoke one service token via `/api/v1/tokens`; a service approver is 400; a person with a service
  account in their personal org is still resettable from a team org; an org Owner who is not the
  manager gets 403 minting but can revoke and delete; deleting an account that solely owns a document
  it created makes the manager that document's Owner; removing a manager from the org is 409; a
  service token's out-of-org member add is 403.
- The decisive test: the creator's password reset does **not** affect a service-account token.
- e2e: `reset-kills-integration-token.spec.ts` gains a companion — create a service account in
  Settings, add it to a document, mint its token, reset the creator's password, the token still 200s.

## Also in this change (GOVERNANCE.md)

An RFC issue before code; spec §10.1, the committed OpenAPI document and `CHANGELOG.md` updated in the
same PR. The SPA's approver picker hides service accounts, matching the 400.

## Out of scope

Scopes (stored, enforced nowhere yet — unchanged). Expiry controls in the UI (the mint API accepts
`expiresAt`). Transferring management to another person.
A service account in more than one org. Converting an existing person account into a service account.
