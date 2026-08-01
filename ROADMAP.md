# Roadmap

What's planned after v1.0.0, in the order the project currently intends to build it. Everything here
is already documented as a deliberate v1 exclusion — in the
[design spec's non-goals](docs/superpowers/specs/2026-07-24-easydocs-v1-design.md), in
[SECURITY.md's known limitations](SECURITY.md#known-v1-limitations-not-vulnerabilities), and in the
[self-hosting guide](docs-site/docs/self-hosting.md). This page is the one place that gathers them.

Dates are not promised. Scope changes go through the RFC process in
[GOVERNANCE.md](GOVERNANCE.md#making-changes) — schema changes, public-API changes, and new runtime
dependencies need agreement in an issue first.

## v1.1

**Identity & access**

- **OIDC/SSO** — sign in through an identity provider instead of (or alongside) local email + password.
- **MFA** for local accounts.
- **Configurable trusted proxies** — `ForwardedHeadersOptions.KnownProxies` bound from configuration,
  so rate limiting behind a reverse proxy doesn't rely on network isolation alone.

**Editing & desktop**

- **Desktop "Open in Word"** — WebDAV + the `ms-word:` protocol handler, for people who want the real
  Word instead of the browser editor.
- **ONLYOFFICE as an alternative editor** — Collabora stays the default; the editor becomes a choice.

**Finding things**

- **Full-text content search** — today search covers document, folder, and version *names* only.
- **Graphical revision graph** — the DAG rendered as a graph instead of an indented list.
- **Tile thumbnails** on the dashboard.

**Storage & operations**

- **S3-compatible blob backend** — an env-configurable swap for the filesystem volume.
- **Blob garbage collection** — today nothing is ever deleted from the blob store.
- **Durable job queue** — diff and PDF workers survive a restart instead of dropping queued jobs.

**Integrations**

- **Cloud export/import pickers** — Dropbox, OneDrive, Google Drive.
- **Antivirus scanning on upload** — for installs whose threat model includes malicious members.
- **Threaded tasks/comments** on documents.

## Later / undecided

- **MIT-licensed API client SDKs** under `packages/*` — the licensing boundary is already decided
  (see the [README](README.md#license--contributing)); the code is not written.
- **A public demo instance.**

## How to influence this

Open an issue. If a v1.1 item matters to your install, saying so (and how you'd use it) is exactly the
signal that decides ordering. If you want to build one of these, start with an issue too — most of them
touch the schema or the public API, which need agreement before code.
