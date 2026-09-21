#!/usr/bin/env bash
# Post-deploy smoke for the Cloud Run deployment shape (2026-09-21): the app and Collabora are two
# Cloud Run services behind one Google load balancer, so the things that can silently break are the
# ones no in-process test sees -- forwarded scheme/host, the second public origin, the CSP that lets
# the editor iframe embed, and Postgres reachability through the Cloud SQL socket. Read-only and
# unauthenticated by default; set SMOKE_TOKEN to an `ed_` PAT to add one authenticated call.
# Every check here mirrors a flow that passed against production on 2026-09-21.
#
#   BASE=https://easydocs.aptsny.net COLLABORA=https://collabora.aptsny.net bash .github/scripts/smoke-prod.sh
set -euo pipefail

BASE="${BASE:-https://easydocs.aptsny.net}"
COLLABORA="${COLLABORA:-https://collabora.aptsny.net}"
APP_HOST="${BASE#*://}"; APP_HOST="${APP_HOST%%/*}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

say() { printf '\n=== %s\n' "$1"; }
fail() { printf '\nFAILED: %s\n' "$1" >&2; exit 1; }
code() { curl -s -o "${2:-/dev/null}" -w '%{http_code}' --max-time 30 "${@:3}" "$1"; }

say "readiness pings Postgres (the shallow /health cannot)"
[ "$(code "$BASE/health/ready" "$WORK/ready.json")" = 200 ] || fail "/health/ready is not 200"
grep -q '"ok"' "$WORK/ready.json" || fail "/health/ready did not report ok"

say "forwarded scheme+host are honoured behind the load balancer"
# ASP.NET builds the OpenAPI `servers` URL from Request.Scheme/Host, i.e. from X-Forwarded-Proto/Host
# once the forwarded-headers middleware trusts the peer. A wrong KnownNetworks shows up here as
# http://<internal host>/ -- and as broken OIDC redirect URIs in the app.
curl -fsS --max-time 30 "$BASE/openapi/v1.json" -o "$WORK/openapi.json"
python3 - "$WORK/openapi.json" "$BASE" <<'PY'
import json, sys
d = json.load(open(sys.argv[1])); base = sys.argv[2].rstrip("/")
servers = [s["url"].rstrip("/") for s in d.get("servers", [])]
assert servers == [base], f"servers={servers}, expected [{base!r}]"
print(f"  servers: {servers}  openapi {d.get('openapi')}")
PY

say "SPA shell and error paths"
[ "$(code "$BASE/login")" = 200 ] || fail "/login is not 200 (redirect loop or missing shell?)"
[ "$(code "$BASE/api/v1/me" "$WORK/me.json")" = 401 ] || fail "anonymous /api/v1/me is not 401"
[ "$(code "$BASE/api/v1/does-not-exist" "$WORK/404.json")" = 404 ] || fail "unknown API route is not 404"
grep -q '<html' "$WORK/404.json" && fail "unknown API route fell through to the SPA"
[ "$(code "$BASE/wopi/files/00000000-0000-0000-0000-000000000000")" = 401 ] || fail "WOPI without access_token is not 401"

say "Collabora is a separate public origin and hands browsers URLs on it"
curl -fsS --max-time 30 "$COLLABORA/hosting/discovery" -o "$WORK/discovery.xml"
URLSRC="$(python3 - "$WORK/discovery.xml" <<'PY'
import sys, xml.etree.ElementTree as ET
for a in ET.parse(sys.argv[1]).iter("action"):
    if a.get("ext") == "docx" and a.get("name") == "edit":
        print(a.get("urlsrc")); break
PY
)"
case "$URLSRC" in "$COLLABORA"/*'?') ;; *) fail "docx edit urlsrc is '$URLSRC', expected it on $COLLABORA ending in '?'" ;; esac
printf '  urlsrc: %s\n' "$URLSRC"

say "the editor page may be framed by the app origin (CSP frame-ancestors)"
curl -sS -D "$WORK/cool.h" -o /dev/null --max-time 30 "${URLSRC}WOPISrc=$(python3 -c 'import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1], safe=""))' "$BASE/wopi/files/00000000-0000-0000-0000-000000000000")"
grep -qi '^HTTP/[0-9.]* 200' "$WORK/cool.h" || fail "cool.html did not answer 200"
FA="$(grep -i '^content-security-policy:' "$WORK/cool.h" | tr -d '\r' | sed -n 's/.*frame-ancestors *\([^;]*\).*/\1/p')"
case " $FA " in *" $APP_HOST"*) ;; *) fail "frame-ancestors is '$FA' -- does not allow $APP_HOST, the editor iframe will be blocked" ;; esac
printf '  frame-ancestors: %s\n' "$FA"

if [ -n "${SMOKE_TOKEN:-}" ]; then
  say "one authenticated call with the PAT"
  [ "$(code "$BASE/api/v1/me" "$WORK/me.json" -H "Authorization: Bearer $SMOKE_TOKEN")" = 200 ] || fail "PAT /api/v1/me is not 200"
  python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print("  as", d["email"])' "$WORK/me.json"
fi

printf '\nCloud Run deployment smoke passed.\n'
