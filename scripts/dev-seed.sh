#!/usr/bin/env bash
# Seeds the local dev BTCPay (http://localhost:14142) with an admin account and a store.
# Credentials are generated into .dev.env (gitignored). Safe to re-run.
set -euo pipefail
cd "$(dirname "$0")/.."

URL=http://localhost:14142
if [ ! -f .dev.env ]; then
  printf 'DEV_ADMIN_EMAIL=admin@blake2b.localhost\nDEV_ADMIN_PASSWORD=%s\n' "$(openssl rand -hex 16)" > .dev.env
  chmod 600 .dev.env
fi
source .dev.env
AUTH="$DEV_ADMIN_EMAIL:$DEV_ADMIN_PASSWORD"

# The first user created on a fresh server becomes its admin, no authentication needed.
if ! curl -sf -u "$AUTH" "$URL/api/v1/users/me" > /dev/null; then
  curl -sf -X POST "$URL/api/v1/users" -H 'Content-Type: application/json' \
    -d "{\"email\":\"$DEV_ADMIN_EMAIL\",\"password\":\"$DEV_ADMIN_PASSWORD\",\"isAdministrator\":true}" > /dev/null
  echo "Created admin $DEV_ADMIN_EMAIL (password in .dev.env)"
fi

if [ -z "${DEV_STORE_ID:-}" ] || ! curl -sf -u "$AUTH" "$URL/api/v1/stores/$DEV_STORE_ID" > /dev/null; then
  DEV_STORE_ID=$(curl -sf -u "$AUTH" -X POST "$URL/api/v1/stores" -H 'Content-Type: application/json' \
    -d '{"name":"Dev Store","defaultCurrency":"USD"}' | python3 -c 'import sys,json; print(json.load(sys.stdin)["id"])')
  grep -v '^DEV_STORE_ID=' .dev.env > .dev.env.tmp && mv .dev.env.tmp .dev.env
  echo "DEV_STORE_ID=$DEV_STORE_ID" >> .dev.env
  chmod 600 .dev.env
  echo "Created store $DEV_STORE_ID"
fi
echo "Store: $URL/stores/$DEV_STORE_ID"
