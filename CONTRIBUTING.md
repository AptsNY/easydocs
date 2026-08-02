# Contributing to easydocs

Thanks for helping build easydocs. This guide covers the essentials.

## Code of Conduct

Participating here — issues, pull requests, reviews — means agreeing to the
[Code of Conduct](CODE_OF_CONDUCT.md) (Contributor Covenant 2.1). Report a concern through the private
channel named in it.

**Found a security vulnerability? Do not open an issue or a PR** — a public diff discloses it. See
[SECURITY.md](SECURITY.md).

## Developer Certificate of Origin (DCO)

easydocs uses the **DCO**, not a CLA. You keep the copyright to your contributions; you certify that you have the right to submit them under the project's license.

**Every commit must be signed off:**

```bash
git commit -s -m "your message"
```

This appends a `Signed-off-by: Your Name <your@email>` line, which certifies the DCO below. Set your git identity first (`git config user.name` / `user.email`). CI rejects commits without a sign-off.

<details>
<summary>Developer Certificate of Origin 1.1 (full text)</summary>

By making a contribution to this project, I certify that:

**(a)** The contribution was created in whole or in part by me and I have the right to submit it under the open source license indicated in the file; or

**(b)** The contribution is based upon previous work that, to the best of my knowledge, is covered under an appropriate open source license and I have the right under that license to submit that work with modifications, whether created in whole or in part by me, under the same open source license (unless I am permitted to submit under a different license), as indicated in the file; or

**(c)** The contribution was provided directly to me by some other person who certified (a), (b) or (c) and I have not modified it.

**(d)** I understand and agree that this project and the contribution are public and that a record of the contribution (including all personal information I submit with it, including my sign-off) is maintained indefinitely and may be redistributed consistent with this project or the open source license(s) involved.

</details>

## Licensing

- **Everything in this repository today is AGPL-3.0.**
- Future API clients / SDKs under `packages/*` will be **MIT** — but there is no `packages/` directory
  yet, so nothing here is MIT-licensed at the moment.

By contributing, you agree your contribution is licensed under the license of the directory it lands in.
The full reasoning and the exact boundary are in the
[README's licensing section](README.md#license).

## Development

**Prerequisites:** .NET 10 SDK, Node 20+, Docker (running — the integration tests use [Testcontainers](https://testcontainers.com/) to spin up a throwaway Postgres).

**Run the app locally:**

```bash
cp deploy/compose/.env.example deploy/compose/.env   # then set real secrets
docker compose -f deploy/compose/docker-compose.yml up --build
# app on http://localhost:8080  (GET /health -> {"status":"ok"})
```

**Run the tests:**

```bash
dotnet test                 # backend (requires Docker for Testcontainers)
npm --prefix web ci
npm --prefix web run build  # frontend build
```

## Workflow

- Branch off `main` (e.g. `feat/…`, `fix/…`); never commit directly to `main`.
- Follow TDD where practical — a failing test, then the code that passes it.
- Keep the build clean: warnings are errors (`TreatWarningsAsErrors`).
- Open a PR against `main`; CI must be green and every commit signed off.
- Design/plan docs live in `docs/superpowers/`.
- **Schema changes, public-API changes, and new runtime dependencies need agreement in an issue first**
  — migrations run automatically on every self-hoster's database at container start, and spec §10.1 is
  the authoritative endpoint set. See [GOVERNANCE.md](GOVERNANCE.md#making-changes).
- Behaviour changes get a `CHANGELOG.md` entry under `[Unreleased]`.
