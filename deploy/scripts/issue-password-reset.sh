#!/usr/bin/env bash
# Break-glass password reset for an operator with database access.
#
# easydocs' normal reset is admin-issued: an org owner mints a link from Settings → Members. Some
# accounts cannot be reached that way, by design —
#
#   * the SOLE OWNER of an organization, because there is nobody else who could issue the link;
#   * anyone active on a second team, whom the cross-org gate refuses;
#   * an install where every owner has locked themselves out at once.
#
# This script is the answer to those. It writes a genuine row into "PasswordResets" and prints the
# link, so recovery then runs through exactly the same endpoint everyone else uses: single use, one
# hour, revokes the account's ed_ tokens, leaves MFA armed. The only thing it bypasses is the check
# on WHO may issue a link — precisely the check an operator holding the database credentials is
# already past.
#
# It deliberately does NOT write a password hash. Nothing here needs to know how passwords are
# hashed, so this cannot drift out of step with Argon2idPasswordHasher.
#
# Usage:
#   deploy/scripts/issue-password-reset.sh someone@example.com
#
# Environment:
#   DB_CONTAINER   Postgres container name             (default: compose-postgres-1)
#   POSTGRES_USER  database user                       (default: easydocs)
#   POSTGRES_DB    database name                       (default: easydocs)
#   BASE_URL       public origin, for the printed link (default: http://localhost:8080)
set -euo pipefail

EMAIL="${1:-}"
if [ -z "$EMAIL" ]; then
  echo "usage: $(basename "$0") <email>" >&2
  exit 64
fi

DB_CONTAINER="${DB_CONTAINER:-compose-postgres-1}"
POSTGRES_USER="${POSTGRES_USER:-easydocs}"
POSTGRES_DB="${POSTGRES_DB:-easydocs}"
BASE_URL="${BASE_URL:-http://localhost:8080}"
BASE_URL="${BASE_URL%/}"

if ! docker inspect "$DB_CONTAINER" >/dev/null 2>&1; then
  echo "no container named '$DB_CONTAINER' — set DB_CONTAINER to your Postgres container." >&2
  exit 69
fi

# :'em' and :'hash' are psql's own quoting, so an address containing a quote cannot alter the SQL.
psql() { docker exec -i "$DB_CONTAINER" psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
           -qtA -v ON_ERROR_STOP=1 -v em="$EMAIL" -v hash="${HASH:-}" "$@"; }

STATUS=$(psql <<'SQL'
SELECT CASE
         WHEN u."Id" IS NULL           THEN 'no-user'
         WHEN m."UserId" IS NULL       THEN 'no-org'
         WHEN u."PasswordHash" IS NULL THEN 'sso'
         ELSE 'ok'
       END
FROM (SELECT 1) AS _
LEFT JOIN "Users" u ON u."Email" = :'em'
LEFT JOIN LATERAL (SELECT "UserId" FROM "OrgMembers" WHERE "UserId" = u."Id" LIMIT 1) m ON true;
SQL
)

case "$STATUS" in
  no-user)
    echo "no account with the email '$EMAIL'." >&2; exit 1 ;;
  no-org)
    # Login itself cannot cope with this account either — it resolves the session's org from the
    # membership list and there is none — so a reset link would not get anyone in.
    echo "'$EMAIL' belongs to no organization; a reset link would not make it usable." >&2; exit 1 ;;
  sso)
    echo "note: '$EMAIL' has no password today (it signs in through SSO)." >&2
    echo "      Completing this link gives it a local password as well." >&2 ;;
  ok) ;;
  *)
    echo "unexpected lookup result: '$STATUS'" >&2; exit 1 ;;
esac

# Base64url of 24 random bytes, matching the mint in PasswordResetEndpoints.
TOKEN=$(openssl rand -base64 24 | tr '+/' '-_' | tr -d '=')
# MemberEndpoints.HashToken: uppercase hex SHA-256 of the token's UTF-8 bytes.
HASH=$(printf '%s' "$TOKEN" | openssl dgst -sha256 -r | cut -d' ' -f1 | tr '[:lower:]' '[:upper:]')

# One transaction: supersede whatever was outstanding, then insert — exactly what Mint does, so an
# operator-issued link and an admin-issued one are the same object. The org is the account's oldest
# membership, the same one Login binds a session to.
INSERTED=$(psql <<'SQL'
BEGIN;

UPDATE "PasswordResets" SET "UsedAt" = now()
WHERE "UsedAt" IS NULL
  AND "UserId" = (SELECT "Id" FROM "Users" WHERE "Email" = :'em');

INSERT INTO "PasswordResets" ("Id", "UserId", "OrgId", "TokenHash", "ExpiresAt", "UsedAt", "CreatedAt")
SELECT gen_random_uuid(), u."Id", m."OrgId", :'hash', now() + interval '1 hour', NULL, now()
FROM "Users" u
JOIN "OrgMembers" m ON m."UserId" = u."Id"
WHERE u."Email" = :'em'
ORDER BY m."CreatedAt"
LIMIT 1
RETURNING "Id";

COMMIT;
SQL
)

if [ -z "$INSERTED" ]; then
  echo "no reset row was created — nothing was changed." >&2
  exit 1
fi

echo "Reset link for $EMAIL — valid for one hour, usable once:"
echo
echo "  $BASE_URL/password-reset/$TOKEN"
echo
echo "Opening it sets a new password, and revokes that account's ed_ API tokens."
echo "It does NOT disable two-factor authentication: if the account has MFA armed, signing in still"
echo "needs a code from the authenticator or one of the recovery codes."
