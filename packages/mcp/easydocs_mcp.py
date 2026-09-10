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

# The eighteen tools. Keys are "METHOD path" exactly as they appear in the OpenAPI document. The
# document has no operationIds (minimal APIs emit none without .WithName), so names live here — the
# table in the spec and this dict are the same thing.
NAMES = {
    "GET /api/v1/documents": "list_documents",
    "GET /api/v1/documents/{id}": "get_document",
    "GET /api/v1/documents/{id}/versions": "list_versions",
    "GET /api/v1/versions/{vid}": "get_version",
    "GET /api/v1/versions/{vid}/text": "get_version_text",
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
    # fail loudly rather than expose an auto-slugged name. (The test's exact-set assertion is the
    # real guard — this raise is only the nearer error message.)
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

    # /openapi/v1.json is public, so fetching it proves the URL but not the token. Probe /me first:
    # a mistyped token fails HERE with a 401, not on the first tool call after the client has already
    # reported "connected".
    me = httpx.get(f"{base}/api/v1/me", headers=headers, timeout=30).raise_for_status().json()
    spec = httpx.get(f"{base}/openapi/v1.json", headers=headers, timeout=30).raise_for_status().json()
    # 60 s, not httpx's 5 s: compare_versions?format=html renders a redline on demand.
    client = httpx.AsyncClient(base_url=base, headers=headers, timeout=60)

    # stdout is the protocol stream from here on; anything we say goes to stderr.
    print(f"easydocs MCP: {len(NAMES)} read-only tools against {base} as {me['email']}", file=sys.stderr)
    build(spec, client).run(show_banner=False)


if __name__ == "__main__":
    main()
