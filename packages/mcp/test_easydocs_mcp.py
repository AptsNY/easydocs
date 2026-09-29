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
    "whoami", "get_org", "list_my_orgs", "get_version_text",
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
