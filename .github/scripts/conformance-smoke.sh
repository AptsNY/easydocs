#!/usr/bin/env bash
# Drives the SHIPPED compose artifact (not the in-process test host) through the reference automation
# flow with nothing but an `ed_` PAT and curl — the same gate as ReferenceAutomationFlow, but against
# the real container: real Postgres, real bundled LibreOffice, real Collabora discovery.
#
# This is the half of spec §12.3 the xUnit suite cannot cover: it proves the image we publish boots and
# serves, and that `soffice` inside it actually renders a PDF.
set -euo pipefail

BASE="${BASE:-http://localhost:8080}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

say() { printf '\n=== %s\n' "$1"; }
fail() { printf '\nFAILED: %s\n' "$1" >&2; exit 1; }

# A minimal but genuinely valid .docx — LibreOffice must be able to open it, so a fake byte blob
# will not do. Same shape as tests/EasyDocs.Api.Tests/Fixtures/DocxFixtures.cs.
python3 - "$WORK/base.docx" <<'PY'
import sys, zipfile
path = sys.argv[1]
CT = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
<Default Extension="xml" ContentType="application/xml"/>
<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
</Types>"""
RELS = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>"""
DOC = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
<w:p><w:r><w:t>Alpha</w:t></w:r></w:p>
<w:p><w:r><w:t>Bravo</w:t></w:r></w:p>
<w:p><w:r><w:t>Charlie</w:t></w:r></w:p>
</w:body></w:document>"""
with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
    z.writestr("[Content_Types].xml", CT)
    z.writestr("_rels/.rels", RELS)
    z.writestr("word/document.xml", DOC)
PY

say "health"
curl -fsS "$BASE/health" | grep -q '"ok"' || fail "/health did not report ok"

say "OpenAPI 3.1 document is served"
curl -fsS "$BASE/openapi/v1.json" -o "$WORK/openapi.json"
python3 - "$WORK/openapi.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
assert d.get("openapi", "").startswith("3.1"), f"expected OpenAPI 3.1, got {d.get('openapi')!r}"
paths = d.get("paths", {})
for required in ["/api/v1/documents", "/api/v1/tokens", "/api/v1/documents/{id}/members", "/api/v1/documents/{id}/audit",
                 # The copies/push line of §10.1 — added in M4, which completes the v1 endpoint set.
                 "/api/v1/versions/{vid}/copies", "/api/v1/documents/{id}/copies",
                 "/api/v1/documents/{id}/pushes", "/api/v1/documents/{id}/push-requests",
                 "/api/v1/push-requests/{id}:accept", "/api/v1/push-requests/{id}:reject"]:
    assert required in paths, f"missing {required} in the published document"
schemes = d.get("components", {}).get("securitySchemes", {})
assert "Bearer" in schemes, "the ed_ Bearer security scheme is not declared"
# "webdav" left this set in v1.1: /versions/{vid}/webdav-sessions is the desktop "Open in Word"
# feature (issue #11), a deliberate part of the surface now. The rest stay banned.
banned = {"exports", "cloud-connections", "tasks", "realtime"}
for p in paths:
    for b in banned:
        assert b not in p, f"dropped surface leaked into the API: {p}"
print(f"  {len(paths)} paths, security schemes: {list(schemes)}")
PY

say "self-contained /docs (no external CDN)"
curl -fsS "$BASE/docs/index.html" -o "$WORK/docs.html"
grep -qi 'swagger\|scalar\|openapi' "$WORK/docs.html" || fail "/docs did not render a docs UI"
if grep -Eqi 'src="https?://|href="https?://' "$WORK/docs.html"; then
  fail "/docs references an external origin — spec §3 requires self-contained assets"
fi

say "register + mint an ed_ PAT"
EMAIL="smoke-$(date +%s)-$RANDOM@example.com"
curl -fsS -X POST "$BASE/api/v1/auth/register" \
  -H 'Content-Type: application/json' \
  -c "$WORK/cookies" \
  -d "{\"email\":\"$EMAIL\",\"displayName\":\"Smoke\",\"password\":\"pw-at-least-12\",\"orgName\":\"Smoke $RANDOM\"}" \
  -o "$WORK/register.json"

PAT="$(curl -fsS -X POST "$BASE/api/v1/tokens" -b "$WORK/cookies" \
  -H 'Content-Type: application/json' -d '{"name":"smoke","scopes":[]}' \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')"
case "$PAT" in ed_*) ;; *) fail "minted token is not an ed_ token" ;; esac
AUTH=(-H "Authorization: Bearer $PAT")

# Everything below uses ONLY the PAT — no cookie jar.
say "create document"
DOC_ID="$(curl -fsS -X POST "$BASE/api/v1/documents" "${AUTH[@]}" \
  -H 'Content-Type: application/json' -d '{"name":"Smoke Lease"}' \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')"

say "upload .docx -> 0.0.1"
VID="$(curl -fsS -X POST "$BASE/api/v1/documents/$DOC_ID/versions" "${AUTH[@]}" \
  -F "file=@$WORK/base.docx;type=application/vnd.openxmlformats-officedocument.wordprocessingml.document" \
  | python3 -c 'import json,sys; d=json.load(sys.stdin); assert (d["major"],d["minor"],d["revision"])==(0,0,1), d; print(d["versionId"])')"

say "publish minor -> 0.1.0"
curl -fsS -X POST "$BASE/api/v1/versions/$VID/publish" "${AUTH[@]}" \
  -H 'Content-Type: application/json' -d '{"kind":"minor"}' \
  | python3 -c 'import json,sys; d=json.load(sys.stdin); assert (d["major"],d["minor"],d["revision"])==(0,1,0), d'

# The reason this script exists: the render shells out to the LibreOffice bundled in the image.
say "bundled LibreOffice renders a PDF"
for i in $(seq 1 60); do
  HAS_PDF="$(curl -fsS "$BASE/api/v1/versions/$VID" "${AUTH[@]}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["hasPdf"])')"
  [ "$HAS_PDF" = "True" ] && break
  sleep 2
done
[ "${HAS_PDF:-False}" = "True" ] || fail "no PDF after 120s — bundled LibreOffice did not render"

curl -fsS "$BASE/api/v1/versions/$VID/download?format=pdf" "${AUTH[@]}" -o "$WORK/out.pdf"
head -c 4 "$WORK/out.pdf" | grep -q '%PDF' || fail "downloaded PDF has no %PDF header"
printf '  rendered %s bytes\n' "$(wc -c < "$WORK/out.pdf" | tr -d ' ')"

say "share link, then read it with no credentials at all"
TOKEN="$(curl -fsS -X POST "$BASE/api/v1/versions/$VID/share-links" "${AUTH[@]}" \
  -H 'Content-Type: application/json' -d '{}' \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')"
curl -fsS "$BASE/s/$TOKEN" \
  | python3 -c 'import json,sys; d=json.load(sys.stdin); assert d["version"]=="0.1.0", d; print("  public view:", d["documentName"], d["version"])'
curl -fsS "$BASE/s/$TOKEN/download" -o "$WORK/shared.docx"
[ -s "$WORK/shared.docx" ] || fail "anonymous share download was empty"

say "the run is on the audit trail"
curl -fsS "$BASE/api/v1/documents/$DOC_ID/audit?limit=100" "${AUTH[@]}" \
  | python3 -c '
import json,sys
actions = {i["action"] for i in json.load(sys.stdin)["items"]}
need = {"document.created","version.created","version.published","share_link.created","share_link.viewed"}
missing = need - actions
assert not missing, f"missing audit actions: {sorted(missing)}"
print("  actions:", len(actions))'

say "Collabora discovery is reachable from the app container"
curl -fsS -X POST "$BASE/api/v1/versions/$VID/sessions" "${AUTH[@]}" \
  | python3 -c 'import json,sys; d=json.load(sys.stdin); assert "WOPISrc=" in d["editorUrl"], d; print("  editor url ok")'

printf '\nCompose-stack conformance smoke passed.\n'
