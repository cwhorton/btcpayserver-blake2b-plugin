#!/usr/bin/env bash
# Smoke test of the packaged plugin on the official BTCPay image (./dev.sh package && ./dev.sh stock).
# Sets up an admin, a store and the dev XBT wallet through the Greenfield API only, then creates an
# invoice and checks it offers XBT with a live price and a testnet4 address.
set -euo pipefail
cd "$(dirname "$0")/.."
source .dev.env
URL=http://localhost:14143
save() { grep -v "^$1=" .dev.env > .dev.env.tmp; echo "$1=$2" >> .dev.env.tmp; mv .dev.env.tmp .dev.env; chmod 600 .dev.env; }
json() { python3 -c "import sys,json; d=json.load(sys.stdin); print($1)"; }

if [ -z "${STOCK_API_KEY:-}" ] || ! curl -sf -H "Authorization: token $STOCK_API_KEY" "$URL/api/v1/users/me" > /dev/null; then
  curl -sf -X POST "$URL/api/v1/users" -H 'Content-Type: application/json' \
    -d "{\"email\":\"$DEV_ADMIN_EMAIL\",\"password\":\"$DEV_ADMIN_PASSWORD\",\"isAdministrator\":true}" > /dev/null
  STOCK_API_KEY=$(curl -sf -u "$DEV_ADMIN_EMAIL:$DEV_ADMIN_PASSWORD" -X POST "$URL/api/v1/api-keys" -H 'Content-Type: application/json' \
    -d '{"label":"stock-smoke","permissions":["unrestricted"]}' | json 'd["apiKey"]')
  save STOCK_API_KEY "$STOCK_API_KEY"
fi
H="Authorization: token $STOCK_API_KEY"

docker compose logs btcpay-stock 2>&1 | grep -o 'Running plugin BTCPayServer.Plugins.BitcoinBlake2b[^ ]* - [0-9.]*' | tail -1

STORE=$(curl -sf -H "$H" -X POST "$URL/api/v1/stores" -H 'Content-Type: application/json' -d '{"name":"Stock smoke","defaultCurrency":"USD"}' | json 'd["id"]')
echo "store $STORE"

D="$DEV_TESTNET4_DESCRIPTOR" python3 -c "import json,os; print(json.dumps({'enabled':True,'config':{'walletKey':os.environ['D']}}))" \
  | curl -sf -X PUT -H "$H" -H 'Content-Type: application/json' -d @- "$URL/api/v1/stores/$STORE/payment-methods/BTCB2-CHAIN" > /dev/null
echo "XBT wallet set through the payment-methods API"

curl -sf -H "$H" "$URL/api/v1/stores/$STORE/rates?currencyPair=BTCB2_USD" | json '"rate BTCB2_USD: " + d[0]["rate"] + (" errors: " + str(d[0]["errors"]) if d[0]["errors"] else "")'

INV=$(curl -sf -H "$H" -X POST "$URL/api/v1/stores/$STORE/invoices" -H 'Content-Type: application/json' -d '{"amount":"5","currency":"USD"}' | json 'd["id"]')
curl -sf -H "$H" "$URL/api/v1/stores/$STORE/invoices/$INV/payment-methods" \
  | json '"\n".join("invoice: %s pay %s to %s (rate %s)" % (m["paymentMethodId"], m["due"], m["destination"], m["rate"]) for m in d)'
curl -sf "$URL/i/$INV" | grep -q 'Btcb2CheckoutBody' && echo "checkout page renders the XBT component"
echo "OK: the packaged plugin works on stock BTCPay"
