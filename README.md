# easydocs

Open-source, self-hostable version control for Word documents — Git-style history for `.docx` without asking anyone to learn Git. A lean, balanced take on SimulDocs, with a first-class public API.

**What it does:** every save becomes an immutable, numbered version; concurrent edits branch instead of overwriting and merge in one click; deep redline diffs even when Track Changes was never on; minor/major publishing with `X.Y.Z` numbers, PDF, and approvals; isolated client copies with push-back review; share links, revert, folders, members.

**Two front doors:** a polished web UI for everyone, and a documented REST API for developers.

## Status

Early development. Milestone **M0** (skeleton) is in progress: auth, folders, and `.docx` upload → version `0.0.1`, running on a single ASP.NET Core app + Postgres via `docker compose up`. See `docs/superpowers/specs/` for the design and `docs/superpowers/plans/` for the build plan.

## Run it

```bash
cp deploy/compose/.env.example deploy/compose/.env   # then edit secrets
docker compose -f deploy/compose/docker-compose.yml up --build
# http://localhost:8080/health
```

## Tech

ASP.NET Core (.NET 10) · PostgreSQL 16 · EF Core · React (Vite) · Collabora Online (in-browser editing, later milestone) · content-addressed filesystem blobs. One process, `docker compose up`.

## License & contributing

- **Server:** GNU **AGPL-3.0** (see [LICENSE](LICENSE)).
- **Future API clients / SDKs** (`packages/*`): **MIT** (zero-friction embedding).
- Contributions are under the **Developer Certificate of Origin (DCO)** — sign off every commit with `git commit -s`. No CLA. See [CONTRIBUTING.md](CONTRIBUTING.md).
