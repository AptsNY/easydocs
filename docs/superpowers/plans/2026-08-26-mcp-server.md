# MCP Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `packages/mcp/easydocs_mcp.py` — a read-only MCP server generated from easydocs' own OpenAPI document, run locally per person with their `ed_` token — plus its test, README, licence flip and CI hook.

**Architecture:** One PEP 723 Python script calls `FastMCP.from_openapi()` on `${EASYDOCS_URL}/openapi/v1.json` with an ordered `route_maps` allowlist (deny-all last) and a `mcp_component_fn` that renames the seventeen surviving GET routes from a dict. One pytest file builds the same server from the committed OpenAPI snapshot and asserts the exact tool set and that every tool is a GET. **No change to `src/`** — the naming fork in the spec was resolved by a scratch run: FastMCP 3.4.7's `mcp_component_fn` receives `route.method`/`route.path` and a settable `component.name` even when `operationId` is null.

**Tech Stack:** Python ≥3.10, `fastmcp>=3.4,<4` (verified 3.4.7), `httpx` (transitive), `uv` for running, `pytest` for the test. Spec: `docs/superpowers/specs/2026-08-26-mcp-server-design.md`.

**Branch:** `feat/mcp-server` (already created; spec commits are on it). Every commit `git commit -s` (DCO). CI must stay green: `docs-build` gains the pytest step.

---

## File structure

| Path | Responsibility |
|---|---|
| `packages/mcp/easydocs_mcp.py` | **Create.** The entire server: env → client → spec → `from_openapi` → `run()`. Exposes `build(spec, client)` so the test builds the same server without env or network. |
| `packages/mcp/test_easydocs_mcp.py` | **Create.** Two facts: exact tool-name set; every tool is a GET. Reads the committed snapshot. |
| `packages/mcp/README.md` | **Create.** The one place setup instructions live. |
| `packages/mcp/LICENSE` | **Create.** MIT. |
| `.github/workflows/ci.yml` | **Modify** `docs-build`: install fastmcp+pytest, run `pytest packages/mcp`. |
| `README.md` | **Modify** §API (one bullet) and §License (table row + sentence). |
| `CONTRIBUTING.md` | **Modify** licensing bullets (lines 43–45). |
| `docs/architecture-decisions.md` | **Modify** ADR-13 lines 217–218. |
| `docs-site/docs/automation-recipes.md` | **Modify** `## Other useful calls`: one paragraph + link. |
| `CHANGELOG.md` | **Modify** `[Unreleased] / Added`. |
| `docs/superpowers/specs/2026-08-26-mcp-server-design.md` | **Modify** the naming section: record that the fork is resolved. |

Verified facts the code relies on (from the scratch run, keep for reference):
- `from fastmcp.server.providers.openapi import RouteMap, MCPType`
- `RouteMap(*, methods='*'|list[str], pattern='.*'|str, tags=set(), mcp_type, mcp_tags=set())`
- `FastMCP.from_openapi(openapi_spec, client=None, name=..., route_maps=None, route_map_fn=None, mcp_component_fn=None, mcp_names=None, tags=None, validate_output=True, **settings)`
- `mcp_component_fn(route, component)`: `route.method` (e.g. `"GET"`), `route.path` (e.g. `"/api/v1/documents/{id}"`); setting `component.name` sticks.
- `await mcp.list_tools()` → `list[OpenAPITool]`, each with `.name` and `._route` (`.method`, `.path`).
- Our allowlist matched exactly 17 routes against `docs-site/docs/api/openapi/v1.json`.

---

### Task 1: The server script

**Files:**
- Create: `packages/mcp/easydocs_mcp.py`
- Create: `packages/mcp/test_easydocs_mcp.py`

- [ ] **Step 1: Write the failing test**

```python
# packages/mcp/test_easydocs_mcp.py
"""Builds the MCP server from the committed OpenAPI snapshot and pins its tool surface.

The snapshot is kept identical to the served /openapi/v1.json by the C# test
Openapi_snapshot_in_docs_site_matches_the_served_document, so passing here means passing against
the real API document. Both facts here are the read-only guarantee as executable checks.
"""
import asyncio
import json
from pathlib import Path

import httpx

import easydocs_mcp

SNAPSHOT = Path(__file__).resolve().parents[2] / "docs-site" / "docs" / "api" / "openapi" / "v1.json"

EXPECTED = {
    "list_documents", "get_document", "list_versions", "get_version", "compare_versions",
    "list_audit_events", "list_members", "list_publications", "preview_merge", "list_copies",
    "list_push_requests", "list_my_approvals", "list_version_approvals", "list_folders",
    "whoami", "get_org", "list_my_orgs",
}


def _tools():
    spec = json.loads(SNAPSHOT.read_text())
    server = easydocs_mcp.build(spec, httpx.AsyncClient(base_url="http://example.invalid"))
    return asyncio.run(server.list_tools())


def test_tool_set_is_exactly_the_read_surface():
    names = {t.name for t in _tools()}
    assert names == EXPECTED, f"unexpected: {names - EXPECTED}; missing: {EXPECTED - names}"


def test_every_tool_is_a_get():
    # Read-only is a property of the tool set. A future POST re-tagged "Documents" must not slip
    # past the method filter unnoticed.
    not_get = {t.name: t._route.method for t in _tools() if t._route.method != "GET"}
    assert not_get == {}, not_get
```

- [ ] **Step 2: Run it to verify it fails**

Run from the repo root:
```bash
uv run --with "fastmcp>=3.4,<4" --with pytest pytest packages/mcp -q
```
Expected: `ModuleNotFoundError: No module named 'easydocs_mcp'` (collection error).

- [ ] **Step 3: Write the server**

```python
# packages/mcp/easydocs_mcp.py
# /// script
# requires-python = ">=3.10"
# dependencies = ["fastmcp>=3.4,<4"]
# ///
"""easydocs as MCP tools — the read side of the API, generated from its own OpenAPI document.

Runs on YOUR machine as YOU:
    EASYDOCS_URL=https://docs.example.com EASYDOCS_TOKEN=ed_... uv run easydocs_mcp.py

Read-only is a property of this tool set, not of the token: an ed_ token carries its owner's full
document role, so keep it as private as a password. See README.md.
"""
import os
import sys

import httpx
from fastmcp import FastMCP
from fastmcp.server.providers.openapi import MCPType, RouteMap

# The seventeen tools. Keys are "METHOD path" exactly as they appear in the OpenAPI document. The
# document has no operationIds (minimal APIs emit none without .WithName), so names live here — the
# table in the spec and this dict are the same thing.
NAMES = {
    "GET /api/v1/documents": "list_documents",
    "GET /api/v1/documents/{id}": "get_document",
    "GET /api/v1/documents/{id}/versions": "list_versions",
    "GET /api/v1/versions/{vid}": "get_version",
    "GET /api/v1/documents/{id}/compare": "compare_versions",
    "GET /api/v1/documents/{id}/audit": "list_audit_events",
    "GET /api/v1/documents/{id}/members": "list_members",
    "GET /api/v1/documents/{id}/publications": "list_publications",
    "GET /api/v1/documents/{id}/merges/preview": "preview_merge",
    "GET /api/v1/documents/{id}/copies": "list_copies",
    "GET /api/v1/documents/{id}/push-requests": "list_push_requests",
    "GET /api/v1/approvals": "list_my_approvals",
    "GET /api/v1/versions/{vid}/approvals": "list_version_approvals",
    "GET /api/v1/folders": "list_folders",
    "GET /api/v1/me": "whoami",
    "GET /api/v1/org": "get_org",
    "GET /api/v1/orgs": "list_my_orgs",
}

READ_TAGS = ["Documents", "Folders", "Audit", "Members", "Approvals", "Publishing", "Merging", "Copies"]

# Evaluated in order, first match wins. The trailing catch-all is the point: a new endpoint is
# excluded until someone adds it here on purpose.
ROUTE_MAPS = [
    # Binary docx/pdf — useless to a model and large. Carries the Documents tag, so it must go first.
    RouteMap(methods=["GET"], pattern=r"^/api/v1/versions/\{vid\}/download$", mcp_type=MCPType.EXCLUDE),
    *[RouteMap(methods=["GET"], tags={t}, mcp_type=MCPType.TOOL) for t in READ_TAGS],
    RouteMap(methods=["GET"], pattern=r"^/api/v1/(me|org|orgs)$", mcp_type=MCPType.TOOL),
    # Everything else: all writes, Auth/MFA/SSO/Tokens, Sharing, Editing, the SSE stream, WOPI,
    # WebDAV, /s/, /health, and GET /api/v1/org/members.
    RouteMap(pattern=r".*", mcp_type=MCPType.EXCLUDE),
]


def _rename(route, component):
    key = f"{route.method} {route.path}"
    # A route that passed the allowlist but has no name here is a mistake in one of the two lists;
    # fail loudly rather than expose an auto-slugged name.
    if key not in NAMES:
        raise KeyError(f"{key} passed the allowlist but has no entry in NAMES")
    component.name = NAMES[key]


def build(spec: dict, client: httpx.AsyncClient) -> FastMCP:
    return FastMCP.from_openapi(
        openapi_spec=spec, client=client, name="easydocs",
        route_maps=ROUTE_MAPS, mcp_component_fn=_rename,
    )


def main() -> None:
    # os.environ[...] raises KeyError naming the variable — that is the fail-fast we want.
    base = os.environ["EASYDOCS_URL"].rstrip("/")  # trailing slash: the perennial "set your URL" ticket
    token = os.environ["EASYDOCS_TOKEN"]
    headers = {"Authorization": f"Bearer {token}"}

    spec = httpx.get(f"{base}/openapi/v1.json", headers=headers, timeout=30).raise_for_status().json()
    # 60 s, not httpx's 5 s: compare_versions?format=html renders a redline on demand.
    client = httpx.AsyncClient(base_url=base, headers=headers, timeout=60)

    # stdout is the protocol stream from here on; anything we say goes to stderr.
    print(f"easydocs MCP: {len(NAMES)} read-only tools against {base}", file=sys.stderr)
    build(spec, client).run()


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
uv run --with "fastmcp>=3.4,<4" --with pytest pytest packages/mcp -q
```
Expected: `2 passed`.

- [ ] **Step 5: Prove the fail-fast and the script form**

```bash
uv run packages/mcp/easydocs_mcp.py; echo "exit=$?"
```
Expected: a `KeyError: 'EASYDOCS_URL'` traceback on stderr and `exit=1`. (Optional, if a local
stack is up: `EASYDOCS_URL=http://localhost:8080 EASYDOCS_TOKEN=ed_… uv run packages/mcp/easydocs_mcp.py`
prints `easydocs MCP: 17 read-only tools against http://localhost:8080` to stderr and waits on stdin — Ctrl-C.)

- [ ] **Step 6: Commit**

```bash
git add packages/mcp/easydocs_mcp.py packages/mcp/test_easydocs_mcp.py
git commit -s -m "feat(mcp): read-only MCP server generated from the OpenAPI document"
```

---

### Task 2: Licence and README for the package

**Files:**
- Create: `packages/mcp/LICENSE`
- Create: `packages/mcp/README.md`

- [ ] **Step 1: Write the MIT licence**

```text
MIT License

Copyright (c) 2026 easydocs contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

- [ ] **Step 2: Write the README**

````markdown
# easydocs MCP server

Use your easydocs install from Claude Code, Cursor, Codex, Gemini CLI, Claude Desktop — any
[MCP](https://modelcontextprotocol.io) client. **Read-only:** browse documents, history, redlines,
audit trails and approvals. It cannot upload, publish, merge, revert or delete.

One Python file. It fetches your install's own `/openapi/v1.json` at start-up and turns the read
endpoints into tools, so it never drifts from the API it talks to. Runs on **your** machine as
**you**. MIT-licensed (the rest of easydocs is AGPL-3.0).

## Setup

You need [`uv`](https://docs.astral.sh/uv/) and a personal access token: in easydocs, **Settings →
API tokens → New token**. Copy it once; it is shown once.

**Claude Code**

```bash
claude mcp add easydocs \
  -e EASYDOCS_URL=https://docs.example.com \
  -e EASYDOCS_TOKEN=ed_... \
  -- uv run https://raw.githubusercontent.com/AptsNY/easydocs/main/packages/mcp/easydocs_mcp.py
```

**Cursor** (`~/.cursor/mcp.json`), **Claude Desktop** (`claude_desktop_config.json`), **Gemini CLI**
(`~/.gemini/settings.json`), **Codex** (`~/.codex/config.toml`, same three fields) — all take the
same command / args / env:

```json
{
  "mcpServers": {
    "easydocs": {
      "command": "uv",
      "args": ["run", "https://raw.githubusercontent.com/AptsNY/easydocs/main/packages/mcp/easydocs_mcp.py"],
      "env": {
        "EASYDOCS_URL": "https://docs.example.com",
        "EASYDOCS_TOKEN": "ed_..."
      }
    }
  }
}
```

Prefer a pinned copy? Clone the repo and point `args` at `packages/mcp/easydocs_mcp.py` on disk.

Then ask: *"What changed in the lease agreement between 0.0.4 and 0.1.0?"* or *"Which of my
documents have approvals waiting on me?"*

## Tools

| Tool | What it answers |
|---|---|
| `list_documents` | documents you can see — by folder, name/content search (`q`), sort, paging |
| `get_document` | one document's name, folder, org |
| `list_versions` | the history: numbers, authors, branches, change summaries |
| `get_version` | one version's details |
| `compare_versions` | redline between two versions — counts, or the HTML with `format=html` |
| `list_audit_events` | who did what, when, on a document |
| `list_members` | who has which role on a document |
| `list_publications` | published versions and their PDFs |
| `preview_merge` | the three-way review of a pending branch merge |
| `list_copies`, `list_push_requests` | client copies and their push-backs |
| `list_my_approvals`, `list_version_approvals` | approvals asked of you; approvals on a version |
| `list_folders` | the folder tree |
| `whoami`, `get_org`, `list_my_orgs` | who you are, which organization this token is bound to |

Every call reaches the API as you: a document you are not a member of is a 403 or 404, exactly as
from `curl`.

## The one thing to know about tokens

**Read-only is a property of this tool set, not of the token.** An `ed_` token carries the full
document role of the person who minted it; nothing stops the *same token* being used with `curl` to
publish or delete. Keep it as private as a password, set it only through the environment (never a
command-line flag — those show up in `ps`), and revoke it under **Settings → API tokens** if it leaks.

## Troubleshooting

- **`KeyError: 'EASYDOCS_URL'`** (or `_TOKEN`) — the env var is missing from the client config.
- **`401` at start-up** — the token is wrong, revoked or expired.
- **A tool is missing** — your install is older than this script and lacks that endpoint. Nothing else
  breaks; upgrade the install to get the tool.
- **Never log to stdout** if you modify the script: stdout *is* the protocol stream. Use stderr.
- Client-side logs: Claude Desktop writes `~/Library/Logs/Claude/mcp-server-easydocs.log`; Claude
  Code: `claude mcp list` / `/mcp`.
````

- [ ] **Step 3: Commit**

```bash
git add packages/mcp/LICENSE packages/mcp/README.md
git commit -s -m "docs(mcp): README and MIT licence for packages/mcp"
```

---

### Task 3: CI

**Files:**
- Modify: `.github/workflows/ci.yml` (the `docs-build` job, after `Install MkDocs Material`)

- [ ] **Step 1: Add the test step**

Insert after the `Install MkDocs Material` step (currently lines 177–178) and before `Build the docs site`:

```yaml
      # packages/mcp is a Python client of the API, and this job already has Python. Its test pins
      # the read-only tool surface against the committed OpenAPI snapshot (which the .NET job pins
      # against the served document), so a write endpoint cannot leak into the MCP tool set unnoticed.
      - name: Test the MCP server package
        run: |
          pip install "fastmcp>=3.4,<4" pytest
          pytest packages/mcp -q
```

- [ ] **Step 2: Validate the YAML locally**

```bash
python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml')); print('ok')" 2>/dev/null \
  || uv run --with pyyaml python -c "import yaml; yaml.safe_load(open('.github/workflows/ci.yml')); print('ok')"
```
Expected: `ok`.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml
git commit -s -m "ci: run the MCP package tests in docs-build"
```

---

### Task 4: Flip the "packages/ does not exist yet" wording everywhere

**Files:**
- Modify: `README.md:141-152` (§API) and `README.md:199-213` (§License)
- Modify: `CONTRIBUTING.md:43-45`
- Modify: `docs/architecture-decisions.md:211-223` (ADR-13)
- Modify: `docs-site/docs/automation-recipes.md:252` (`## Other useful calls`)
- Modify: `CHANGELOG.md` under `## [Unreleased]` / `### Added`

- [ ] **Step 1: README §API — add one bullet** after the `Live updates` bullet (line 150):

```markdown
- **MCP:** a read-only [MCP server](packages/mcp/) for Claude Code, Cursor and other AI coding agents,
  generated from the OpenAPI document — install with one command, runs on your machine as you.
```

- [ ] **Step 2: README §License — replace the table row and the sentence**

Replace:
```markdown
| `packages/*` — future API client SDKs | **MIT**, when written. The directory does not exist yet. |
```
with:
```markdown
| `packages/*` — API clients | **MIT.** Today: [`packages/mcp`](packages/mcp/), the MCP server. |
```
Replace the opening line `**Everything in this repository today is AGPL-3.0** ([LICENSE](LICENSE)) — server, SPA, tests, deploy
files, docs.` with:
```markdown
**Everything in this repository is AGPL-3.0** ([LICENSE](LICENSE)) — server, SPA, tests, deploy
files, docs — except `packages/*`, which is MIT.
```
Replace `| Everything in this repo | **AGPL-3.0** — the whole repository right now |` with
`| Everything outside `packages/*` | **AGPL-3.0** |`.
Replace the sentence `so future SDKs will live
under `packages/*` with their own MIT `LICENSE`. **Until that directory exists, assume AGPL-3.0 for
anything you take from here.**` with:
```markdown
so clients live
under `packages/*` with their own MIT `LICENSE`. **Anything outside that directory is AGPL-3.0.**
```

- [ ] **Step 3: CONTRIBUTING.md — replace lines 43–45**

```markdown
- **Everything in this repository is AGPL-3.0, except `packages/*`.**
- `packages/*` holds API clients and is **MIT** — today that is `packages/mcp`, the MCP server. Each
  package carries its own `LICENSE`.
```

- [ ] **Step 4: ADR-13 — update the decision paragraph**

Replace the sentence spanning lines 217–218 (`... a directory boundary you can point at. Until that
directory exists, nothing here is MIT.`) with:
```markdown
`packages/*` with their own MIT license — a directory boundary you can point at. The first occupant
is `packages/mcp`, the MCP server (2026-08).
```
Update the heading `## ADR-13: AGPL server, MIT SDKs (when they exist)` to `## ADR-13: AGPL server, MIT clients under packages/*`.
Update the Consequences sentence `and will be MIT-smooth once SDKs exist.` to `and is MIT where the client code lives.`

- [ ] **Step 5: automation-recipes.md — one paragraph under `## Other useful calls`**

Insert immediately after the `## Other useful calls` heading (line 252), before the code block:

```markdown
Prefer talking to an AI agent instead of `curl`? The [MCP server](https://github.com/AptsNY/easydocs/tree/main/packages/mcp)
exposes the read side of this API — every `GET` below — as tools for Claude Code, Cursor and friends,
authenticated with the same `ed_` token.
```

- [ ] **Step 6: CHANGELOG.md — new entry at the top of `[Unreleased] / Added`**

```markdown
- **easydocs can be used from AI coding agents.** `packages/mcp/easydocs_mcp.py` is a read-only
  [MCP](https://modelcontextprotocol.io) server generated from the install's own `/openapi/v1.json`:
  seventeen tools covering documents, history, redlines, audit trails, approvals and folders, for
  Claude Code, Cursor, Codex, Gemini CLI and Claude Desktop. It runs on the user's machine with their
  own `ed_` token, so every call is authorized exactly as their `curl` would be. Nothing on the
  server changed. `packages/*` is now real and MIT-licensed, as the licence section always said it
  would be.
```

- [ ] **Step 7: Build the docs site strictly (mkdocs is what CI runs)**

```bash
uv run --with mkdocs-material --with mkdocs-swagger-ui-tag --directory docs-site mkdocs build --strict
```
Expected: exits 0, no `WARNING` lines. (The recipes link is absolute to GitHub, so it is not a nav-validated internal link.)

- [ ] **Step 8: Commit**

```bash
git add README.md CONTRIBUTING.md docs/architecture-decisions.md docs-site/docs/automation-recipes.md CHANGELOG.md
git commit -s -m "docs: packages/mcp exists — flip the licence wording and link the MCP server"
```

---

### Task 5: Record the resolved fork in the spec and finish

**Files:**
- Modify: `docs/superpowers/specs/2026-08-26-mcp-server-design.md` (section "Tool names: rename in the adapter…")

- [ ] **Step 1: Replace the "Fork, decided by the plan's first task" paragraph** with:

```markdown
Resolved 2026-08-26 before implementation: a scratch run against `fastmcp` 3.4.7 confirmed
`mcp_component_fn` receives `route.method` / `route.path` and a settable `component.name` with
`operationId` null, and the allowlist matched exactly seventeen routes. No `src/` change was needed.
```

- [ ] **Step 2: Run the whole verification once more**

```bash
uv run --with "fastmcp>=3.4,<4" --with pytest pytest packages/mcp -q      # 2 passed
git status --short                                                          # only the spec edit
```

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/specs/2026-08-26-mcp-server-design.md
git commit -s -m "docs(spec): record that the MCP naming fork resolved to zero src/ changes"
```

- [ ] **Step 4: Hand off** — use superpowers:finishing-a-development-branch: push `feat/mcp-server`, open a PR against `main` titled `feat: read-only MCP server generated from the OpenAPI document`, body summarising the spec and linking it; end the body with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

---

## Not in this plan (by design — see spec "Follow-ups")

`GET /versions/{vid}/text`; write tools behind an opt-in; a hosted `/mcp` with OAuth; a `pyproject.toml`/PyPI publish; a docs-site page beyond the recipes link.
