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
  sdk)
    shift
    sdk "$@"
    ;;
  *)
    sed -n '2,12p' "$0"
    exit 1
    ;;
esac
