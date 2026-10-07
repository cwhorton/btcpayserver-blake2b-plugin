# Bitcoin BLAKE2b (XBT) for BTCPay Server

**Status: under development, not yet usable for payments.**

A BTCPay Server plugin that lets merchants accept Bitcoin BLAKE2b (XBT, also traded as BTCB2), the chain that forked from Bitcoin at block 961,640 with BLAKE2b proof of work.

It installs like any other plugin and needs no full node. Payments are detected through public block explorers and Electrum servers, or through the merchant's own.

## Planned for the first version

- On-chain XBT receiving with a new address for every invoice, from a watch-only wallet key. No spending keys on the server.
- Payment detection through Esplora-style explorer APIs (mempool, Esplora) and Electrum servers (electrs, Fulcrum).
- Use of the merchant's own explorer, alone or cross-checked against public ones. When only public servers are used, two independent sources must agree before an invoice counts as paid.
- XBT pricing from public exchanges (NeoxEX, NonKYC), with USDC/USDT treated as USD.
- A display name for the currency, "XBT" by default, that the merchant can change.

Not planned for the first version: Lightning, refunds and payouts, server-side spending.

## Important for merchants

XBT addresses look exactly like Bitcoin addresses, and the same keys control coins on both chains. **Use a wallet dedicated to XBT**, never the wallet that holds your BTC.

## Development

Everything runs in Docker. The .NET SDK, BTCPay Server and its dependencies all run in containers. Source files live in this folder.

```bash
git clone --recurse-submodules <this repository>
./dev.sh up        # build the plugin and start BTCPay at http://localhost:14142
./dev.sh restart   # rebuild the plugin and reload BTCPay
./dev.sh test      # run tests
./dev.sh logs      # follow BTCPay's logs
./dev.sh down      # stop everything
```

BTCPay Server is pinned as a git submodule in `submodules/btcpayserver`. A mainnet BTCPay follows XBT mainnet; a testnet or regtest BTCPay (like this dev setup) follows XBT testnet4. `BTCPAY_BTCB2_CHAIN` overrides this.

`./scripts/dev-seed.sh` creates a local admin, an API key and a store (credentials go to the gitignored `.dev.env`).

### Testing payment detection without real coins

`./dev.sh fake-chain` points BTCPay at three fake explorers, of which two must agree, polling every 2 seconds. Drive them with `scripts/fake-chain.sh` (`pay`, `mine`, `reorg`, `drop`, `fail`), or run the end-to-end scenarios:

```bash
./dev.sh fake-chain
python3 scripts/e2e_fake_chain.py
./dev.sh real-chain   # back to the public explorers
```
