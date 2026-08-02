# Security policy

easydocs is self-hosted software that holds an organization's documents. Take reports seriously, and
expect this file to be honest about what v1 does not do.

## Reporting a vulnerability

**Use GitHub private vulnerability reporting:**
<https://github.com/Robertzu43/easydocs/security/advisories/new>

That opens a private advisory visible only to you and the maintainer. It is the preferred channel
because it needs no shared secret, keeps the discussion attached to the repository, and can become a
published advisory with a CVE when a fix ships.

If you cannot use GitHub, mail **`SECURITY-CONTACT-PLACEHOLDER@example.invalid`**
(<!-- MAINTAINER: replace with a real address before v1.0.0, or delete this paragraph and keep GitHub as the only channel -->).

**Please do not** open a public issue, a pull request, or a discussion thread for a suspected
vulnerability. Do not include real customer documents in a report — a minimal synthetic `.docx` is
always enough.

Helpful reports include: the version or commit, how you deployed (compose image, `dotnet run`, a
reverse proxy in front), whether the install is public or on a LAN, and the smallest sequence of
requests that demonstrates the problem.

**What to expect:** an acknowledgement within 5 working days, and an assessment within 10. easydocs
has one maintainer and no paid security team, so there is no faster SLA on offer and pretending
otherwise would be dishonest. There is no bug bounty. Reporters are credited in the advisory and the
changelog unless they ask not to be.

Please allow **90 days** before public disclosure, or less by agreement once a fix is out.

## Supported versions

| Version | Supported |
|---|---|
| `main` | Yes — fixes land here first |
| pre-1.0 (no tag yet) | n/a — `v1.0.0` has not been released |

There is no released version of easydocs yet. Until `v1.0.0` is tagged, `main` is the only supported
line and the only place fixes land.

After `v1.0.0`, the intent is to support **the latest minor release** and to ship security fixes as
patch releases on it. easydocs does not backport to older minors: it is a single-container deployment
with automatic migrations, so upgrading is `docker compose pull && docker compose up -d` after a
backup — see [Upgrading](docs-site/docs/self-hosting.md#upgrading). If a fix requires a breaking
change, the advisory will say so.

## What is hardened

Every item below is implemented in `main` today.

- **Passwords: Argon2id** at OWASP-recommended cost (m=19456 KiB, t=2, p=1), stored in standard **PHC
  string format** — `$argon2id$v=19$m=…,t=…,p=…$salt$hash`. Verification re-derives with the
  parameters read out of the stored digest, never with the compile-time constants, so raising the cost
  later does not invalidate existing hashes. Comparison is constant-time; an unparseable digest fails
  closed rather than throwing. (`src/EasyDocs.Api/Auth/Argon2idPasswordHasher.cs`)
- **Capability tokens are never stored in plaintext.** `ed_` personal access tokens (256-bit), share
  link tokens (128-bit) and invitation tokens (192-bit) are all generated from
  `RandomNumberGenerator` and persisted **only** as a SHA-256 hash, under a unique index. The raw
  value is returned exactly once, at creation. A SHA-256 lookup by equality is safe here precisely
  because the pre-image is a full-entropy CSPRNG secret — there is no low-entropy input for a timing
  oracle to walk.
- **WOPI access tokens are short-TTL stateless JWTs** (30 minutes) carrying `typ=wopi`, and are
  **never stored**. The `typ` claim firewalls them in both directions: a session JWT cannot authorize
  WOPI, and a WOPI token cannot authorize the application.
- **One per-document authorization chokepoint, with no org-role fallback.** Every document-scoped
  endpoint routes through `DocumentAuthorization` (`src/EasyDocs.Api/Auth/DocumentAuthorization.cs`),
  which resolves the caller's role from `document_members` alone. **Org role grants no implicit
  document access** — an org Owner who is not a member of a document cannot read it. A document in
  another org returns `404`, not `403`, so the surface does not leak existence. Copies do not inherit
  the original's membership.
- **A PAT can never exceed its owner.** `ed_` tokens authenticate as the user who minted them and
  carry that user's roles; they do not widen access. The token list is scoped so a member sees only
  their own tokens — names, scopes and last-used times of a colleague's tokens are not enumerable, by
  a member or by an org Owner.
- **Append-only audit** on mutations and on public share-link **views**, including the anonymous
  reader's IP address (see the caveat under Known limitations). Rows are inserted in the same
  transaction as the change they describe, and nothing in the API updates or deletes them.
- **`Jwt:Secret` fails fast at boot** if it is missing or shorter than 32 bytes, rather than at the
  first login. HS256 requires a 256-bit key, and a short one is a silently weak signature.
- **Rate limiting on the anonymous and credential surfaces** — the public share viewer, the public
  download, `auth/login` and `auth/register`, plus per-user limiting on PAT creation. Per-endpoint,
  not global, so static assets are never throttled. See
  [Rate limiting](docs-site/docs/self-hosting.md#rate-limiting) for the tunables and for an honest
  statement of what a per-IP limit does and does not stop.
- **Session cookies** are `httpOnly`, `Secure`, `SameSite=Lax`.
- **Errors are RFC 7807** everywhere, including body-binding failures, so a malformed request does not
  leak an exception dump.

## Known v1 limitations (not vulnerabilities)

These are deliberate v1 ceilings, verified against the code. They are documented here so an operator
can make an informed decision — **please do not report them as vulnerabilities.** They are split
because the distinction matters: the first group can change your risk posture or your exposure, the
second group only changes what the product does.

### Security-relevant — read these before you expose easydocs

**The `Secure` cookie / plain-HTTP footgun.** The session cookie is `Secure`, so a browser silently
discards it over plain HTTP on a LAN IP or an internal hostname: the user appears to log in and then
isn't, with no error. `http://localhost` works only because browsers treat localhost as a secure
context. **HTTPS is required in practice, not optional** — terminate TLS at a reverse proxy. Dropping
`Secure` is not the answer; it would hand every session cookie to anyone on the network. See
[TLS is mandatory in practice](docs-site/docs/self-hosting.md#tls-is-mandatory-in-practice).

**WOPI access tokens can be logged.** The WOPI `access_token` travels in the query string, as the WOPI
protocol requires, and ASP.NET Core's request logging would print the full URL. The only thing
suppressing that is `"Microsoft.AspNetCore": "Warning"` in `appsettings.json`. Raising it to
`Information` — e.g. `Logging__LogLevel__Microsoft.AspNetCore=Information`, which compose passes
straight through — **prints live WOPI tokens to stdout**, where they reach your log aggregator. There
is no code-level redaction. The tokens are 30-minute capabilities scoped to one edit session, so the
blast radius is bounded, but do not raise that log level in production. See
[Never raise the ASP.NET Core log level in production](docs-site/docs/self-hosting.md#never-raise-the-aspnet-core-log-level-in-production).

**Rate limits are per-IP and collapse behind a proxy.** Without
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, every request appears to come from the proxy, so the whole
installation shares one budget. It fails **closed**, not open — the limits get stricter, nobody gets
extra allowance — and the `ip` field in share-view audit rows records the proxy instead of the
visitor. This is deliberately not on by default: trusting `X-Forwarded-For` unconditionally would let
anyone bypass the limiter with a header. Note that enabling the switch also clears the framework's
loopback-only trust list. Since v1.1 you can narrow that trust back to named proxies with
`ForwardedHeaders__KnownProxies__N` / `ForwardedHeaders__KnownNetworks__N` (unparseable values, or
setting them with the switch off, abort boot rather than being silently ignored); keep the network
isolation as defense-in-depth regardless. Read
[Forwarded headers behind a proxy](docs-site/docs/self-hosting.md#forwarded-headers-behind-a-proxy)
in full before enabling it.

**A migration caveat for anyone who has run from `main`.** The `ShareLinks.Token` and
`Invitations.Token` columns held plaintext tokens until the 2026-07-27 and 2026-07-28 migrations, which
rename `Token` → `TokenHash` **with no data transform**. Rows written by the older build therefore sit
in a hash column holding a plaintext value, and will no longer resolve. This is irrelevant to a fresh
install. **If you have run from `main` since M3: rotate your share links and re-issue any outstanding
invitations after upgrading.**

**No antivirus scanning on upload.** v1 trusts `.docx` files uploaded by authenticated organization
members (spec §11); there is no ClamAV or equivalent in the ingest path. Files are stored
content-addressed and served back with the document MIME type, never executed. If your threat model
includes malicious members, scan the blob store out of band.

**MFA is TOTP, opt-in, per account.** Since v1.1 a user can enable an authenticator-app second factor
(RFC 6238) plus ten single-use recovery codes from Settings; recovery codes are stored hashed. There
is no WebAuthn and no way for an admin to *require* MFA org-wide yet — if you need enforced MFA for
everyone, an authenticating reverse proxy in front of easydocs is still the tool.

**Public downloads are not audited, only views.** `GET /s/{token}` writes a `share_link.viewed` audit
row and increments the view count; `GET /s/{token}/download` writes nothing. In a browser the SPA
always loads the landing page first, so a human recipient is recorded — but a client fetching the
download URL directly leaves no audit trail. The share link itself, its creator and its expiry are
still recorded at creation.

**Push branches carry their fork point in `RootVersionId`.** There is a foreign key to
`document_versions`, but nothing constrains the target to a version in the *receiving* document's
history. A malformed push branch is a data-integrity risk, not an access-control one — the push review
path itself is authorized normally.

### Functional limitations — these do not change your risk

**Share landing without a built SPA.** A browser navigating to `/s/{token}` is served
`wwwroot/index.html`. If the SPA has not been built, the request falls through to the JSON
representation instead. The shipped container always builds the SPA, so this affects only `dotnet run`
without a prior `npm run build`. No data is exposed that the token holder is not already entitled to.

**One organization per session.** A session carries exactly one org, chosen as the user's oldest
membership; accepting an invitation rebinds the session to the inviting org. There is no org switcher
in v1. This is a navigation limit, not an access-control one — authorization is scoped to the org in
the session, and no cross-org data is reachable.

**The diff queue is in-memory.** Change summaries are computed by an in-process worker fed by an
unbounded channel. A restart drops queued jobs; they are recomputed on demand, so no history is lost.
There is no durable broker.

**Moves and formatting changes are always reported as zero.** The comparison engine classifies
insertions and deletions only. A move is reported as a deletion plus an insertion, and formatting-only
edits are not counted. The UI does not display move or format-change counts, so a zero is never
presented as "no moves occurred".

**Collabora discovery refreshes at most once a day**, on the first request after the stored timestamp
expires, with no scheduled job. If Collabora's discovery document changes, editing may fail until the
next refresh; restarting easydocs forces one.

## Hardening checklist for operators

The short version. [`docs-site/docs/self-hosting.md`](docs-site/docs/self-hosting.md) is the long one.

1. Terminate **TLS** at a reverse proxy. Non-negotiable — see the `Secure` cookie note above.
2. Set a real **`Jwt__Secret`** (≥ 32 bytes, from a CSPRNG) and **`POSTGRES_PASSWORD`**. Both ship as
   placeholders in `.env.example`.
3. Leave **`Microsoft.AspNetCore` logging at `Warning`.**
4. Behind a proxy, set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` **and** bind the app's port to the
   proxy only (`127.0.0.1:8080:8080`, or no `ports:` at all on a shared Docker network).
5. Do not expose **Collabora** (9980) or **Postgres** (5432) beyond the Docker network.
6. **Back up the database and the blob volume together**, and take a backup before every upgrade —
   the new container migrates your schema on startup.
7. Give share links an **expiry** at creation, and revoke them when the recipient is done.
8. If your threat model includes malicious members, add out-of-band AV scanning and an
   authenticating proxy for MFA.
