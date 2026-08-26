# An MCP server for easydocs — design

**Date:** 2026-08-26
**Status:** Approved, ready for implementation planning
**Touches:** new `packages/mcp/` (Python, MIT); `.WithName(...)` on the read endpoints in
`Documents/DocumentEndpoints.cs`, `Documents/AuditEndpoints.cs`, `Documents/MemberEndpoints.cs`,
`Folders/FolderEndpoints.cs`, `Approvals/ApprovalEndpoints.cs`, `Publishing/PublishEndpoints.cs`,
`Merging/MergeEndpoints.cs`, `Copies/CopyEndpoints.cs`, `Copies/PushEndpoints.cs`,
`Auth/AuthEndpoints.cs`, `Auth/OrgEndpoints.cs`; `tests/EasyDocs.Api.Tests/OpenApiTests.cs`; the
OpenAPI snapshot; `.github/workflows/ci.yml`; `README.md` (licence section); `docs-site/`;
`CHANGELOG.md`

## Problem

Coding agents (Claude Code, Cursor, Codex, Gemini CLI) speak the Model Context Protocol. easydocs
speaks REST. Someone who wants an agent to answer "what changed in the lease between 0.0.4 and
0.1.0?" today has to hand it `curl` and the OpenAPI document and hope. The API already does
everything the UI does (ADR-10), so the missing piece is not capability — it is a protocol adapter.

## Scope

**In:** an MCP server that exposes the *read* side of the easydocs API as tools; runs on the
person's own machine, authenticated as them with their own `ed_` token; installable with one
command from any HTTP-or-stdio MCP client.

**Out:** write tools (upload, publish, merge, revert, delete — deliberate, see Decisions); a hosted
multi-tenant MCP endpoint; OAuth / dynamic client registration (what claude.ai web connectors
need); a plain-text "read this document" tool (the API has no such endpoint — a candidate follow-up);
binary downloads.

## Decisions

### Generate the tools from `/openapi/v1.json` with FastMCP; do not hand-write them

[FastMCP](https://gofastmcp.com) (Python, 3.x) has `FastMCP.from_openapi(spec, client)`: it turns each
OpenAPI operation into an MCP tool, forwards calls to the API over an `httpx` client, and preserves
the operation's parameters, schemas and descriptions. Our OpenAPI document is generated from the
running code and cannot go stale (ADR-10), so the adapter cannot drift from the API either.

Considered and rejected for this phase:

- **In-process C# server** (`ModelContextProtocol.AspNetCore`, `app.MapMcp("/mcp")`, hand-written
  `[McpServerTool]` methods). Curated tools, no second process, token passthrough for free — but a
  new runtime dependency (GOVERNANCE: issue first), a new authenticated surface in the shipped
  image, and every tool is code to write and test. It remains the named upgrade path if agents turn
  out to need curated tools (FastMCP's own docs say auto-generated servers are a bootstrapping
  surface, not the end state).
- **Hosted FastMCP sidecar** (a fourth container). FastMCP's `httpx` client carries one fixed
  `Authorization` header, so a shared instance either acts as one user for everyone or needs
  per-request header forwarding. Neither is acceptable for a product whose authorization is
  per-document, per-person.

### Run locally, per person, over stdio

Each user runs the server on their own machine with two environment variables:

```
EASYDOCS_URL    = https://docs.example.com        # their install
EASYDOCS_TOKEN  = ed_...                          # their own personal access token
```

The server fetches `${EASYDOCS_URL}/openapi/v1.json` at start-up, builds the tools, and speaks
stdio to the client that launched it. Every call reaches the API as that person, so
`DocumentAuthorization` applies unchanged: a document they are not a member of is a 403 or 404
exactly as it would be from `curl`. No new authentication code anywhere.

Consequence stated plainly: **read-only is a property of the tool set, not of the token.** `ed_`
tokens have a `Scopes` column but the handler does not enforce it — a token used by this server has
the full role of its owner. The allowlist below is what keeps an agent from publishing or deleting;
the docs say so.

### Allowlist by tag and method; deny everything else

FastMCP `route_maps` are evaluated in order, first match wins. Ours:

| Rule | Effect |
|---|---|
| `GET` + tag ∈ {Documents, Folders, Audit, Members, Approvals, Publishing, Merging, Copies} → **TOOL** | the read surface |
| `GET /api/v1/me`, `/api/v1/org`, `/api/v1/orgs` → **TOOL** | "who am I, which org" |
| `GET /api/v1/versions/{vid}/download` → **EXCLUDE** | binary; useless to a model, large |
| `GET /api/v1/documents/{id}/events` → **EXCLUDE** | SSE stream; never returns |
| everything else → **EXCLUDE** | all writes; Auth, MFA, SSO, Tokens, Sharing, Editing, WOPI, WebDAV, `/s/`, `/health` |

Excluding by a trailing catch-all rather than listing writes means a **new endpoint is excluded by
default** until someone adds it here on purpose. The resulting tool set, with the names given in the
next decision:

| Tool | Endpoint |
|---|---|
| `list_documents` | `GET /api/v1/documents` (folder, `q` name+content search, sort, paging) |
| `get_document` | `GET /api/v1/documents/{id}` |
| `list_versions` | `GET /api/v1/documents/{id}/versions` |
| `get_version` | `GET /api/v1/versions/{vid}` |
| `compare_versions` | `GET /api/v1/documents/{id}/compare?from&to&format` |
| `list_audit_events` | `GET /api/v1/documents/{id}/audit` |
| `list_members` | `GET /api/v1/documents/{id}/members` |
| `list_publications` | `GET /api/v1/documents/{id}/publications` |
| `preview_merge` | `GET /api/v1/documents/{id}/merges/preview` |
| `list_copies` | `GET /api/v1/documents/{id}/copies` |
| `list_push_requests` | `GET /api/v1/documents/{id}/push-requests` |
| `list_my_approvals` | `GET /api/v1/approvals` |
| `list_version_approvals` | `GET /api/v1/versions/{vid}/approvals` |
| `list_folders` | `GET /api/v1/folders` |
| `whoami` | `GET /api/v1/me` |
| `get_org` | `GET /api/v1/org` |
| `list_my_orgs` | `GET /api/v1/orgs` |

Seventeen tools. `compare_versions` with `format=html` returns the redline HTML, which is the one
"document content" a model can read today.

### Name the read endpoints in the OpenAPI document

The served document has **no `operationId` on any operation** — minimal APIs only emit one when the
endpoint has a name. FastMCP falls back to a slug of method + path (`get_api_v1_documents_id_versions`),
which is exactly the kind of tool name the MCP best-practice guidance warns against. So each
allowlisted endpoint gains `.WithName("<snake_case>")` with the name from the table above.
`snake_case` because FastMCP slugifies operation ids and the MCP guidance says letters, digits and
underscores only.

This is the only change to `src/`. It is additive OpenAPI metadata: no behaviour change, no schema
change, no new dependency, so it needs no GOVERNANCE issue. It also improves `/docs` for every
human reader. The OpenAPI snapshot in `docs-site/` is regenerated as usual.

FastMCP's `mcp_names` override exists but is not used: putting the names in the API means anyone
generating a client from the document gets them too.

### Package and install

```
packages/mcp/
  pyproject.toml         # name easydocs-mcp, requires-python >=3.10, dependency fastmcp>=3.4,<4
  easydocs_mcp.py        # the server: read env, fetch spec, from_openapi(...), run()
  test_easydocs_mcp.py   # builds the server from the committed snapshot; asserts the tool set
  README.md              # per-client setup
  LICENSE                # MIT
```

`[project.scripts] easydocs-mcp = "easydocs_mcp:main"` so `uvx` can run it straight from the
repository — no PyPI publish, no build step:

```bash
claude mcp add easydocs \
  -e EASYDOCS_URL=https://docs.example.com -e EASYDOCS_TOKEN=ed_... \
  -- uvx --from "git+https://github.com/AptsNY/easydocs#subdirectory=packages/mcp" easydocs-mcp
```

Cursor, Codex and Gemini CLI take the same `command` / `args` / `env` triple in their JSON config;
the README shows each. Claude Desktop is the same recipe (it is a stdio client).

`packages/mcp` is **MIT**. The README's licence section and CONTRIBUTING already reserve `packages/*`
for MIT client code and say "no such directory exists yet"; both are updated to say it now does and
what is in it. Everything else in the repository stays AGPL-3.0.

### Failure behaviour

- Missing `EASYDOCS_URL` / `EASYDOCS_TOKEN`: exit non-zero at start-up with a one-line message
  naming the variable (the house "misconfiguration fails fast" rule, ADR-11). Never print to stdout
  after the transport starts — stdio servers that log to stdout corrupt the protocol stream; messages
  go to stderr.
- Spec fetch fails (wrong URL, install down, token rejected on a later call): the fetch error is
  reported on stderr and the process exits; an API error during a tool call is surfaced to the model
  as the tool's error result with the API's own `problem+json` `detail`, which is already written for
  humans.
- Tokens are read from the environment only. Never a command-line flag (visible in `ps`), never a
  config file this package owns.

## Testing

**C#** (`OpenApiTests`): one new fact asserting every operation in the served document that this
package allowlists carries an `operationId`, keyed off the table above. A future read endpoint added
without `.WithName` fails CI with the path named — the same "route added without metadata" guard the
existing facts provide.

**Python** (`packages/mcp/test_easydocs_mcp.py`): loads `docs-site/docs/api/openapi/v1.json` (the
committed snapshot, kept honest by `Openapi_snapshot_in_docs_site_matches_the_served_document`),
builds the server with a dummy client, and asserts the tool name set is **exactly** the seventeen
above. This is the read-only guarantee as an executable check: a write endpoint leaking into the
allowlist, or a name changing, fails the test. One more fact: start-up without the two environment
variables exits non-zero.

**CI**: a new `mcp` job in `ci.yml` — `astral-sh/setup-uv`, then `uv run --directory packages/mcp
pytest`. Python only; no Docker, no app boot.

Not tested here: a live end-to-end call through a real MCP client. The Playwright and conformance
suites are about the shipped container; this package is a client of it, and its contract with the
container is the OpenAPI snapshot, which is already pinned.

## Documentation

- `packages/mcp/README.md`: what it is, the seventeen tools, per-client setup, the read-only /
  token-scope caveat, how to mint an `ed_` token (Settings → API tokens).
- `docs-site/docs/ai-agents.md` ("Use easydocs from an AI agent"), linked from `automation-recipes.md`
  and the nav.
- `README.md`: a short paragraph under API, and the licence section updated.
- `CHANGELOG.md` under `[Unreleased] / Added`.

## Follow-ups this design deliberately leaves open

- A `GET /api/v1/versions/{vid}/text` endpoint (plain text via the existing `DocxText.Extract`) —
  the single most useful read an agent lacks. Public-API change, so it goes through an issue.
- Write tools, behind an explicit opt-in (`EASYDOCS_MCP_ALLOW_WRITES=1`) once the read surface has
  been used in anger.
- A hosted `/mcp` inside the app with OAuth, for claude.ai connectors — the in-process C# route above.
