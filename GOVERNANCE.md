# Governance

How decisions get made in easydocs. Short, because the project is small and pretending otherwise would
be theatre.

## Current shape: benevolent-dictator, one maintainer

| Maintainer | Role |
|---|---|
| [`@Robertzu43`](https://github.com/Robertzu43) | Author, sole maintainer, final say |

That is the whole list. There is no steering committee, no voting, and no second reviewer — so a PR can
sit until the maintainer has time, and it is better to say so than to imply a rota that does not exist.

**Adding maintainers.** The maintainer adds them, and this table is the record. The bar is a track
record of merged, reviewed contributions and demonstrated judgement about what *not* to build. If a
second maintainer is ever added, this document gets rewritten to say how the two of them disagree in
public — a real process, not this paragraph.

**If the maintainer goes quiet.** easydocs is AGPL-3.0 and DCO-only, with no CLA and no copyright
assignment. Nobody needs permission to fork it, and the licence guarantees that stays true. That is the
succession plan, and it is deliberate.

## Roadmap and planning

Everything is public.

- **[Issues](https://github.com/AptsNY/easydocs/issues)** are the backlog. Bugs and feature
  requests both start there, with the templates in `.github/ISSUE_TEMPLATE/`.
- **[Projects](https://github.com/AptsNY/easydocs/projects)** is the board: what is queued, in
  progress, and shipped. If it is not on the board, it is not planned yet.
- **Design specs and milestone plans** live in [`docs/superpowers/`](docs/superpowers/) and are the
  substantive record. Each plan carries a *Deviations* section written after execution — what the plan
  got wrong about the code, and what was built instead. That is the honest history of the project and
  the best place to understand why something is the way it is.
- Deferred features are gathered in [ROADMAP.md](ROADMAP.md) and the
  [Not in v1](docs-site/docs/self-hosting.md#not-in-v1) section of the self-hosting guide (which
  also records what v1.1 delivered — SSO and MFA among it).

**The best way to get something built** is an issue that describes the problem, not the solution. A
proposal arriving as a large unsolicited PR is the slowest path — see
[Making changes](#making-changes).

## Making changes

Read [CONTRIBUTING.md](CONTRIBUTING.md) first; it covers the mechanics (DCO sign-off, branch naming,
tests, `TreatWarningsAsErrors`).

**Ordinary changes** — a bug fix, a test, a doc correction, a self-contained feature already agreed in
an issue — go straight to a pull request. One maintainer review, CI green, merge.

**Changes that need an RFC first** (open an issue and get agreement *before* writing code):

1. **Database schema changes.** Migrations run automatically on container start, so a merged migration
   rewrites every self-hoster's database the moment they pull the image. There is no down-migration
   path in practice. Schema changes are close to irreversible in the field.
2. **Public API changes.** Spec §10.1 calls itself *the authoritative endpoint set*, and the E1–E12
   conformance suite plus the published OpenAPI 3.1 document are contracts that third-party clients
   hold. Adding, removing, or changing the shape of an endpoint should be a decision, not a side
   effect of building a screen. (M4.5 did add endpoints beyond §10.1 — the web UI was unbuildable
   without them — and the resolution was to *update the spec in the same milestone* so the two stopped
   disagreeing. That is the pattern: the spec and the code are never allowed to drift silently.)
3. **New runtime dependencies**, new containers in the compose stack, or anything that changes what
   `docker compose up` needs. "One process, `docker compose up`" is a product promise.
4. **Anything that weakens a security property** listed in [SECURITY.md](SECURITY.md#what-is-hardened).

**An RFC is an issue, not a document.** State the problem, the proposed change, what breaks, and the
migration path for existing installs. Label it `rfc`. The maintainer responds in the thread, and the
decision — including a "no" and its reason — stays in the thread so it is findable later. Accepted RFCs
that are large enough get a plan under `docs/superpowers/plans/`.

## Versioning: the product's version is not a document's version

**easydocs versions documents. easydocs is also versioned. These two numbering schemes are unrelated,
and confusing them is the single most likely misreading of this project's release notes.**

| | Looks like | Set by | Rules |
|---|---|---|---|
| **A document version** | `0.0.1`, `0.1.0`, `2.0.0` | easydocs, per document | Spec §5 / R1–R8. Revision on save, minor on publish-minor, major on publish-major. Per-document counters. |
| **An easydocs release** | `v1.0.0`, `v1.1.0` | the maintainer, per release | [Semantic Versioning 2.0.0](https://semver.org/) over the product. |

A document at `2.0.0` says nothing about which easydocs release you are running, and upgrading easydocs
never renumbers a document. The two never interact.

For **easydocs releases**, semver applies to the surfaces users depend on:

- **MAJOR** — a breaking change to the public REST API (spec §10.1), to the `.env` contract, or to a
  documented on-disk format. Or a migration that a self-hoster must do work to survive.
- **MINOR** — new endpoints, new UI, new optional configuration. Backwards compatible.
- **PATCH** — fixes and security fixes, no contract change.

`v1.0.0` has **not** been released. Until it is, `main` is pre-release: usable and self-hostable, not
yet versioned. Every release is a signed, annotated git tag and an entry in
[CHANGELOG.md](CHANGELOG.md).

## Licensing and copyright

Decided in spec §14 and not up for renegotiation per-contribution:

- **The server is AGPL-3.0.** See [LICENSE](LICENSE) and the
  [licensing section of the README](README.md#license--contributing).
- **DCO, not a CLA.** Every commit is signed off with `git commit -s`, and CI rejects commits that are
  not. The full DCO 1.1 text is in [CONTRIBUTING.md](CONTRIBUTING.md).
- **Contributors keep their copyright.** There is no copyright assignment and no relicensing right,
  which is why dual-licensing or selling commercial exceptions is explicitly *not* pursued — that
  would have required a CLA, and a CLA changes the relationship between contributor and project. The
  intended funding model is donations and, possibly, an official paid hosted instance; both are fine
  under AGPL without asking anyone to sign anything.

## Conduct

Everyone participating in this project — issues, pull requests, discussions — is covered by the
[Code of Conduct](CODE_OF_CONDUCT.md). Enforcement is the maintainer's, through the private channel
named there.

## Security

Vulnerabilities do **not** go through the public issue tracker. See [SECURITY.md](SECURITY.md).
