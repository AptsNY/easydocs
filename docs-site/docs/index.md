# easydocs

**Git-style history for `.docx` — without asking anyone to learn Git.**

Open-source, self-hostable version control for Word documents. Every save becomes an immutable,
numbered version. Two people editing at once branch instead of overwriting each other, and merge in
one click. You get a real redline between any two versions even if nobody ever turned Track Changes
on.

Two front doors: a web UI for the people who just need to work on the document, and a documented REST
API for developers. One process, `docker compose up`.

## Where to go

<div class="grid cards" markdown>

- **[Getting started](getting-started.md)** — install with `docker compose up` and take one `.docx`
  from upload to a second version, in about ten minutes.

- **[Concepts](concepts.md)** — the mental model. `X.Y.Z` numbering, branches and merge, redline diff,
  publishing, approvals, copies, share links. Read this before you roll easydocs out to anyone else.

- **[Self-hosting](self-hosting.md)** — the operator's guide. Every `.env` variable, why TLS is
  mandatory in practice, reverse-proxy configs, rate-limit tuning, and backup/restore.

- **[Automation recipes](automation-recipes.md)** — drive the whole lifecycle over the REST API with
  an `ed_` personal access token.

</div>

## Not released yet

There is **no `v1.0.0` tag**. Milestones M0–M4.5 are complete — the REST API and the full web UI both
work end to end, and the v1 conformance profile (E1–E12) runs green in CI against the real container
image. M5, the release milestone, is in progress. Treat `main` as pre-release: usable, self-hostable,
not yet versioned.

## Not built in v1

Listed so you do not go looking for them. All are v1.1 or later.

- **No OIDC/SSO** and **no MFA.** Local email + password (Argon2id) or an `ed_` API token. Put an
  authenticating reverse proxy in front of easydocs if you need either.
- **No S3 blob backend.** Blobs live on a filesystem volume, content-addressed by sha256.
- **No desktop "Open in Word."** Editing is in the browser via Collabora. WebDAV + `ms-word:` is v1.1.
- **No antivirus scanning on upload.** v1 trusts `.docx` files from authenticated organization
  members.
- **No org switcher.** A session carries exactly one organization.
- **No graphical revision graph** (history is an indented list) and **no full-text content search**
  (names only).

## API reference

The API reference is served by the application itself, so it can never drift from the running build:

- **Interactive docs:** `/docs` on your install (self-contained, no external CDN)
- **OpenAPI 3.1 document:** `/openapi/v1.json`

These guides link to it rather than re-rendering it. One source of truth. See
[Automation recipes](automation-recipes.md) for worked examples, and for the one place the generated
document is currently misleading.

## License

The server is **AGPL-3.0**. Future API clients and SDKs under `packages/*` will be **MIT** — see
`LICENSING.md` in the repository. Contributions are under the **Developer Certificate of Origin**
(`git commit -s`), with no CLA.
