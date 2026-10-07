#!/usr/bin/env bash
# Drives the fake XBT explorers started by ./dev.sh fake-chain (a, b, c on ports 3010-3012).
#
#   scripts/fake-chain.sh pay <address> <sats> [explorers] [txid]   Broadcast a payment (default: a,b,c)
#   scripts/fake-chain.sh mine [blocks] [explorers]          Mine blocks
#   scripts/fake-chain.sh reorg [depth] [explorers]          Undo the top blocks
#   scripts/fake-chain.sh drop <txid> [explorers]            Make a transaction vanish
#   scripts/fake-chain.sh fail on|off <explorers>            Make explorers unreachable
#   scripts/fake-chain.sh state [explorer]
set -euo pipefail
port() { case "$1" in a) echo 3010;; b) echo 3011;; c) echo 3012;; *) echo "unknown explorer $1" >&2; exit 1;; esac; }
each() { local list="$1" path="$2" body="$3"; for e in ${list//,/ }; do curl -sf -X POST "http://127.0.0.1:$(port "$e")$path" -H 'Content-Type: application/json' -d "$body" > /dev/null; done; }
case "${1:-}" in
  pay)
    txid=${5:-$(openssl rand -hex 32)}
    each "${4:-a,b,c}" /dev/pay "{\"address\":\"$2\",\"sats\":$3,\"txid\":\"$txid\"}"
    echo "$txid"
    ;;
  mine) each "${3:-a,b,c}" /dev/mine "{\"blocks\":${2:-1}}" ;;
  reorg) each "${3:-a,b,c}" /dev/reorg "{\"depth\":${2:-1}}" ;;
  drop) each "${3:-a,b,c}" /dev/drop "{\"txid\":\"$2\"}" ;;
  fail) each "$3" /dev/fail "{\"on\":$([ "$2" = on ] && echo true || echo false)}" ;;
  state) curl -s "http://127.0.0.1:$(port "${2:-a}")/dev/state" ;;
  *) sed -n '2,10p' "$0"; exit 1 ;;
esac
