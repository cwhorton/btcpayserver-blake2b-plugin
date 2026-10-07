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
save() { grep -v "^$1=" .dev.env > .dev.env.tmp; echo "$1=$2" >> .dev.env.tmp; mv .dev.env.tmp .dev.env; chmod 600 .dev.env; }

# The first user created on a fresh server becomes its admin, no authentication needed.
# BTCPay accepts the password for Greenfield calls only in the account's first 5 minutes,
# so an API key is created right away for later script use.
if [ -z "${DEV_API_KEY:-}" ] || ! curl -sf -H "Authorization: token $DEV_API_KEY" "$URL/api/v1/users/me" > /dev/null; then
  curl -sf -X POST "$URL/api/v1/users" -H 'Content-Type: application/json' \
    -d "{\"email\":\"$DEV_ADMIN_EMAIL\",\"password\":\"$DEV_ADMIN_PASSWORD\",\"isAdministrator\":true}" > /dev/null \
    && echo "Created admin $DEV_ADMIN_EMAIL (password in .dev.env)"
  DEV_API_KEY=$(curl -sf -u "$DEV_ADMIN_EMAIL:$DEV_ADMIN_PASSWORD" -X POST "$URL/api/v1/api-keys" -H 'Content-Type: application/json' \
    -d '{"label":"dev-scripts","permissions":["unrestricted"]}' | python3 -c 'import sys,json; print(json.load(sys.stdin)["apiKey"])') \
    || { echo "Could not create an API key; create one under Account > API Keys and set DEV_API_KEY in .dev.env" >&2; exit 1; }
  save DEV_API_KEY "$DEV_API_KEY"
  DEV_STORE_ID=""
fi
AUTH_HEADER="Authorization: token $DEV_API_KEY"

if [ -z "${DEV_STORE_ID:-}" ] || ! curl -sf -H "$AUTH_HEADER" "$URL/api/v1/stores/$DEV_STORE_ID" > /dev/null; then
  DEV_STORE_ID=$(curl -sf -H "$AUTH_HEADER" -X POST "$URL/api/v1/stores" -H 'Content-Type: application/json' \
    -d '{"name":"Dev Store","defaultCurrency":"USD"}' | python3 -c 'import sys,json; print(json.load(sys.stdin)["id"])')
  save DEV_STORE_ID "$DEV_STORE_ID"
  echo "Created store $DEV_STORE_ID"
fi
echo "Store: $URL/stores/$DEV_STORE_ID"
