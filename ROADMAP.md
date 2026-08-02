# Roadmap

What's planned next, and what recent releases delivered. Dates are not promised. Scope changes go
through the process in [GOVERNANCE.md](GOVERNANCE.md#making-changes) — schema changes, public-API
changes, and new runtime dependencies need agreement in an issue first.

## Delivered in v1.1

Everything the v1.1 milestone tracked, each a deliberate v1 exclusion at the time:

- **OIDC/SSO sign-in** (#9) and **MFA for local accounts** (#10)
- **Desktop "Open in Word"** via WebDAV + `ms-word:` (#11)
- **Full-text content search** (#12) and the **graphical revision graph** (#13)
- **S3-compatible blob backend** (#14), **blob garbage collection** (#15), and the
  **durable job queue** (#16)
- **Configurable trusted proxies** (#17)

## Next / undecided

- **Cloud export/import pickers** — Dropbox, OneDrive, Google Drive.
- **ONLYOFFICE as an alternative editor** — Collabora stays the default.
- **Tile thumbnails** on the dashboard.
- **Antivirus scanning on upload** — for installs whose threat model includes malicious members.
- **Org-wide MFA enforcement** — MFA is per-account opt-in today.
- **Threaded tasks/comments** on documents.
- **WebAuthn/passkeys** as a second (or first) factor.
- **MIT-licensed API client SDKs** under `packages/*` — the licensing boundary is already decided
  (see the [README](README.md#license)); the code is not written.
- **A public demo instance.**

## How to influence this

Open an issue. If an item here matters to your install, saying so (and how you'd use it) is exactly
the signal that decides ordering. If you want to build one, start with an issue too — most touch the
schema or the public API, which need agreement before code.
