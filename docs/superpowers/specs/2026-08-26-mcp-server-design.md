# An MCP server for easydocs — design

**Date:** 2026-08-26
**Status:** Approved, ready for implementation planning
**Touches:** new `packages/mcp/` (Python, MIT); `.github/workflows/ci.yml` (`docs-build` job);
`README.md` (API + licence sections); `CONTRIBUTING.md`; `docs/architecture-decisions.md` (ADR-13);
`docs-site/docs/automation-recipes.md` (one link); `CHANGELOG.md`. **No change to `src/`** unless the
naming fork below forces one.

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

| # | Rule | Effect |
|---|---|---|
| 1 | `GET /api/v1/versions/{vid}/download` → **EXCLUDE** | binary; useless to a model, large. Must precede rule 2: it carries the `Documents` tag |
| 2 | `GET` + tag ∈ {Documents, Folders, Audit, Members, Approvals, Publishing, Merging, Copies} → **TOOL** | the read surface |
| 3 | `GET /api/v1/me`, `/api/v1/org`, `/api/v1/orgs` → **TOOL** | "who am I, which org" |
| 4 | everything else → **EXCLUDE** | all writes; Auth, MFA, SSO, Tokens, Sharing, Editing, Events (the SSE stream — tagged `Events`, so it lands here), WOPI, WebDAV, `/s/`, `/health`, and `GET /api/v1/org/members` (org roster — not needed to work on documents) |

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

### Tool names: rename in the adapter; touch `src/` only if FastMCP cannot

The served document has **no `operationId` on any operation** — minimal APIs only emit one when the
endpoint has a name. Left alone, FastMCP names tools by a slug of method + path
(`get_api_v1_documents_id_versions`), which is exactly what the MCP naming guidance warns against.

The names in the table live in **one dict in `easydocs_mcp.py`**, applied through FastMCP's own
renaming hook (`mcp_names`, or `mcp_component_fn` setting `component.name`, whichever the pinned
3.x release supports without an `operationId`). Zero `src/` diff, no snapshot regeneration, and the
table and the code are the same thing.

Fork, decided by the plan's first task (a scratch script against the pinned release): if FastMCP's
renaming turns out to *require* an `operationId` to key on, the honest fix is `.WithName("<snake_case>")`
on the seventeen endpoints — additive OpenAPI metadata, no behaviour change, no GOVERNANCE issue — plus
a snapshot regeneration. Not the default, because nobody generates a client from the document today and
`packages/` did not exist until now.

`snake_case` either way: FastMCP slugifies names, and the guidance is letters, digits, underscores.

### Package and install

```
packages/mcp/
  easydocs_mcp.py        # the whole server; PEP 723 inline metadata declares fastmcp>=3.4,<4
  test_easydocs_mcp.py   # builds the server from the committed snapshot; asserts the tool set
  README.md              # per-client setup
  LICENSE                # MIT
```

One script, no `pyproject.toml`, no publish. PEP 723 (`# /// script` … `dependencies = [...]`) lets
`uv` resolve the dependency from the file itself, so the install line is:

```bash
claude mcp add easydocs \
  -e EASYDOCS_URL=https://docs.example.com -e EASYDOCS_TOKEN=ed_... \
  -- uv run https://raw.githubusercontent.com/AptsNY/easydocs/main/packages/mcp/easydocs_mcp.py
```

Cursor, Codex and Gemini CLI take the same `command` / `args` / `env` triple in their JSON config;
the README shows each. Claude Desktop is the same recipe (it is a stdio client). A `pyproject.toml`
appears the day this is published to PyPI, not before.

`packages/mcp` is **MIT**. The README's licence section, CONTRIBUTING, and ADR-13 in
`docs/architecture-decisions.md` all reserve `packages/*` for MIT client code and say the directory
does not exist yet; all three flip to say it now does and what is in it. Everything else in the
repository stays AGPL-3.0.

The FastMCP API this design leans on (`from_openapi`, `route_maps`, `RouteMap(methods, pattern,
tags, mcp_type)`, `MCPType`, the renaming hook) was read from the 3.x docs, not exercised. The plan's
first task pins `fastmcp` to the current 3.x release and confirms each in a scratch script; it also
confirms that a route missing from an older install's spec yields a missing *tool*, not a failed
server (see Testing).

### The client, and its two non-negotiable lines

- `httpx.AsyncClient(base_url=EASYDOCS_URL.rstrip("/"), headers={"Authorization": f"Bearer {token}"}, timeout=60)`.
  `rstrip("/")` because a trailing slash on "your install URL" is the perennial support ticket.
  `timeout=60` because `compare_versions?format=html` renders a redline on demand and httpx's 5 s
  default would make the most useful tool the flakiest.

### Failure behaviour

- Missing `EASYDOCS_URL` / `EASYDOCS_TOKEN`: `os.environ[...]` raises with the variable named and the
  process exits non-zero before the transport starts. That is the fail-fast rule (ADR-11) for free;
  it needs no wrapper and no test.
- Never print to stdout after the transport starts — a stdio server that logs to stdout corrupts the
  protocol stream; anything the script says goes to stderr.
- Spec fetch fails (wrong URL, install down): the `httpx` error is reported on stderr and the
  process exits. An API error during a tool call (403, 404, 400) is surfaced to the model as the
  tool's error result carrying the API's own `problem+json` `detail`, which is already written for
  humans.
- Tokens are read from the environment only. Never a command-line flag (visible in `ps`), never a
  config file this package owns.

## Testing

**One test file, `packages/mcp/test_easydocs_mcp.py`**, two facts:

1. Load `docs-site/docs/api/openapi/v1.json` (the committed snapshot, kept honest by the existing
   `Openapi_snapshot_in_docs_site_matches_the_served_document`), build the server with a dummy
   client, and assert the tool name set is **exactly** the seventeen above. A write endpoint leaking
   into the allowlist, a name changing, or a new read route arriving un-named all fail here with the
   offending name printed — which is why there is no separate C# guard.
2. Assert every generated tool's underlying route method is `GET`. This makes "read-only is a
   property of the tool set" true by construction rather than by inspection: a future `POST`
   re-tagged `Documents` cannot slip through rule 2's method filter unnoticed.

**CI**: appended to the existing `docs-build` job (it already sets up Python): `pip install
"fastmcp>=3.4,<4" pytest && pytest packages/mcp`. No new job, no new action.

Not tested here: a live call through a real MCP client — this package is a client of the shipped
container, and its contract with the container is the OpenAPI snapshot, already pinned. Version
skew is by design tolerated: a user's older install whose spec lacks a route simply lacks that tool.

## Documentation

- `packages/mcp/README.md`: what it is, the seventeen tools, per-client setup (Claude Code, Cursor,
  Codex, Gemini CLI, Claude Desktop), the read-only / token-scope caveat, how to mint an `ed_` token
  (Settings → API tokens). This is the one place the instructions live.
- `docs-site/docs/automation-recipes.md`: one paragraph and a link to that README. A dedicated
  docs-site page is written the first time the README proves insufficient for a user, not before.
- `README.md`: one paragraph under API; the licence section updated.
- `CHANGELOG.md` under `[Unreleased] / Added`.

## Follow-ups this design deliberately leaves open

- A `GET /api/v1/versions/{vid}/text` endpoint (plain text via the existing `DocxText.Extract`) —
  the single most useful read an agent lacks. Public-API change, so it goes through an issue.
- Write tools, behind an explicit opt-in (`EASYDOCS_MCP_ALLOW_WRITES=1`) once the read surface has
  been used in anger.
- A hosted `/mcp` inside the app with OAuth, for claude.ai connectors — the in-process C# route above.
