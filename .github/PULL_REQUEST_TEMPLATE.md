<!--
Thanks for contributing to easydocs.

The single most common first-PR failure is a missing DCO sign-off. Check that first — see below.
-->

## What and why

<!-- What changes, and what problem it solves. Link the issue: "Closes #123". -->

## How to verify

<!-- The commands or steps a reviewer runs to see it work. A failing-then-passing test counts. -->

## Checklist

- [ ] **Every commit is signed off** — `git commit -s`. A CI job (`dco-check`) rejects any commit
      without a `Signed-off-by: Name <email>` line, and it checks *every* commit in the PR, not just
      the last one. Forgot on one commit? `git commit --amend -s --no-edit`. On several?
      `git rebase --signoff main` then force-push. easydocs uses the
      [DCO](https://github.com/AptsNY/easydocs/blob/main/CONTRIBUTING.md#developer-certificate-of-origin-dco),
      not a CLA — you keep your copyright.
- [ ] **`dotnet build easydocs.slnx` produces zero warnings.** `TreatWarningsAsErrors` is on, so a
      warning is a build failure, not a nag.
- [ ] **`dotnet test` passes** (needs Docker running — the integration tests use Testcontainers for a
      throwaway Postgres). Only the two `soffice`-guarded PDF tests may skip locally; CI installs
      LibreOffice and skips zero. **No conformance criterion (E1–E12) may skip, ever** — a silently
      skipped criterion reads as coverage that does not exist.
- [ ] **New behaviour has a test**, and a bug fix has a regression test that fails without the fix.
- [ ] Web UI change? `npm --prefix web run build` succeeds, `npm --prefix web run lint` is clean, and
      `npm --prefix web run e2e` passes against the built app.
- [ ] Docs updated if this changes behaviour an operator or an API consumer can see — the README, the
      relevant page under `docs-site/docs/`, and `CHANGELOG.md` under `[Unreleased]`.
- [ ] I read [CONTRIBUTING.md](https://github.com/AptsNY/easydocs/blob/main/CONTRIBUTING.md) and
      agree to the
      [Code of Conduct](https://github.com/AptsNY/easydocs/blob/main/CODE_OF_CONDUCT.md).

## Scope

- [ ] This PR adds or changes a **database migration**. <!-- Migrations run automatically on container
      start, so a merged migration rewrites every self-hoster's database on their next pull. Needs
      agreement in an issue first — GOVERNANCE.md#making-changes. -->
- [ ] This PR adds or changes a **public API endpoint**. <!-- Spec §10.1 is the authoritative endpoint
      set. Update it in the same PR so the spec and the code do not drift, and say so here. -->
- [ ] This PR changes a **security property** listed in
      [SECURITY.md](https://github.com/AptsNY/easydocs/blob/main/SECURITY.md#what-is-hardened).
      <!-- Say which, and why. -->
- [ ] None of the above.

<!--
Found a security vulnerability? Do not send a PR — the diff discloses it publicly. Report it privately:
https://github.com/AptsNY/easydocs/security/advisories/new
-->
