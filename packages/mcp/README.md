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
(`~/.gemini/settings.json`) — all take the same command / args / env:

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

Codex's `~/.codex/config.toml` is TOML, same three fields:

```toml
[mcp_servers.easydocs]
command = "uv"
args = ["run", "https://raw.githubusercontent.com/AptsNY/easydocs/main/packages/mcp/easydocs_mcp.py"]
env = { EASYDOCS_URL = "https://docs.example.com", EASYDOCS_TOKEN = "ed_..." }
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
