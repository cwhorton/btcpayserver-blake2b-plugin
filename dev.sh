#!/usr/bin/env bash
# Development commands. Everything runs inside Docker; see docker-compose.yml.
#
#   ./dev.sh build        Build the plugin and register it with the dev BTCPay
#   ./dev.sh test         Run the plugin's tests
#   ./dev.sh up           Start BTCPay (http://localhost:14142) and its dependencies
#   ./dev.sh restart      Rebuild the plugin and restart BTCPay to load it
#   ./dev.sh logs         Follow BTCPay's logs
#   ./dev.sh down         Stop everything (data is kept)
#   ./dev.sh reset        Stop everything and delete all dev data
#   ./dev.sh fake-chain   Restart BTCPay against 3 fake chain sources (explorers a and b, Electrum
#                         server c; 2 must agree); drive them
#                         with scripts/fake-chain.sh
#   ./dev.sh real-chain   Restart BTCPay against the real public explorers
#   ./dev.sh package      Build the Release .btcpay package into .build/packed (what Plugin Builder makes)
#   ./dev.sh stock        Run the official BTCPay image (no source changes) at http://localhost:14143
#                         with only the packaged plugin installed
#   ./dev.sh sdk <cmd>    Run any command in the SDK container
set -euo pipefail
cd "$(dirname "$0")"

source plugin-env.sh

sdk() { docker compose run --rm sdk "$@"; }
build_plugin() { sdk bash -c "dotnet build src/$PROJECT/$PROJECT.csproj -c Debug && ./plugin-register.sh"; }

case "${1:-}" in
  build)
    build_plugin
    ;;
  test)
    sdk dotnet test --project "tests/$PROJECT.Tests/$PROJECT.Tests.csproj"
    ;;
  up)
    build_plugin
    docker compose up -d btcpay
    echo "BTCPay is starting at http://localhost:14142 (first start compiles BTCPay; follow with ./dev.sh logs)"
    ;;
  restart)
    build_plugin
    docker compose restart btcpay
    ;;
  logs)
    docker compose logs -f --tail=100 btcpay
    ;;
  down)
    docker compose down
    ;;
  reset)
    docker compose down -v
    ;;
  fake-chain)
    build_plugin
    export BTCB2_ESPLORA="http://fake-esplora-a:3002/api,http://fake-esplora-b:3002/api,tcp://fake-esplora-c:50001"
    export BTCB2_REQUIRED_AGREEMENT=2 BTCB2_POLL_SECONDS=2
    docker compose --profile fake-chain up -d --force-recreate fake-esplora-a fake-esplora-b fake-esplora-c btcpay
    ;;
  real-chain)
    build_plugin
    docker compose --profile fake-chain stop fake-esplora-a fake-esplora-b fake-esplora-c
    docker compose up -d --force-recreate btcpay
    ;;
  package)
    sdk bash -c "rm -rf .build/publish .build/packed && \
      dotnet publish src/$PROJECT/$PROJECT.csproj -c Release -o .build/publish -m:2 && \
      dotnet run --project submodules/btcpayserver/BTCPayServer.PluginPacker -c Release -- .build/publish $PROJECT .build/packed"
    find .build/packed -type f
    ;;
  stock)
    pkg=$(find .build/packed -name "$PROJECT.btcpay" | head -1)
    [ -n "$pkg" ] || { echo "Run ./dev.sh package first" >&2; exit 1; }
    rm -rf .build/stock-plugins && mkdir -p ".build/stock-plugins/$PROJECT"
    (cd ".build/stock-plugins/$PROJECT" && unzip -q "$OLDPWD/$pkg")
    docker compose --profile stock up -d --force-recreate btcpay-stock
    echo "Stock BTCPay starting at http://localhost:14143 (docker compose logs -f btcpay-stock)"
    ;;
  sdk)
    shift
    sdk "$@"
    ;;
  *)
    sed -n '2,18p' "$0"
    exit 1
    ;;
esac
