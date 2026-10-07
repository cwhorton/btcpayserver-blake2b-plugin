# Bitcoin BLAKE2b (XBT) for BTCPay Server

**Status: beta. Tested on testnet4 and against simulated chains, not yet with real mainnet payments. Use small amounts.**

Accept Bitcoin BLAKE2b (XBT, also traded as BTCB2) in BTCPay Server. XBT is the chain that forked from Bitcoin at block 961,640 (August 30, 2026) and switched to BLAKE2b proof of work.

The plugin installs like any other plugin and **needs no full node**. Payments are detected through public block explorers, or through your own explorer or Electrum server.

## What it does

- **On-chain XBT payments** with a new address for every invoice, from a watch-only wallet key. BTCPay never holds your spending keys.
- **Payment detection through chain data sources:** mempool/Esplora explorers and Electrum servers (electrs, Fulcrum).
  - By default it uses three independently run public explorers, and two of them must agree before a payment counts.
  - You can use your own explorer or Electrum server instead, or cross-check yours against the public ones.
- **Protection against the wrong chain.** XBT and Bitcoin addresses are identical, so every source must prove it follows the BLAKE2b chain before it's used. A Bitcoin explorer is rejected, so real BTC is never counted as XBT.
- **Prices from public exchanges.** NeoxEX (XBT/USDC) and NonKYC (XBT/USDT) are combined and treated as USD.
  - Thin order books are ignored.
  - If the two exchanges disagree by more than 5%, no invoice is priced in XBT.
  - When one exchange is down, the other is used only if its price is within 5% of the last price both agreed on in the past hour.
  - Other currencies are converted from USD with your store's usual rates.
- **Stricter confirmation defaults than Bitcoin.** The chain is young and its hashrate can be rented, so a medium speed policy waits for 3 confirmations. You can change this per store.
- **A configurable display name**, "XBT" by default.

Not included: Lightning, refunds and payouts, and spending from BTCPay. Send from your own XBT wallet.

## Before you start: use a dedicated wallet

**XBT addresses look exactly like Bitcoin addresses, and the same keys control coins on both chains.** Create a new wallet just for XBT in an XBT-capable wallet, such as the [Sparrow BLAKE2b build](https://github.com/paulscode/sparrow/releases). Never use the wallet that holds your BTC.

## Setup

1. **Install:** in BTCPay go to *Server settings → Plugins*, install **Bitcoin BLAKE2b (XBT)**, then restart BTCPay.
2. **Add your wallet:**
   - Open your store, then *Wallets → XBT*.
   - Paste your wallet's account public key (zpub, ypub or xpub) or its output descriptor.
   - Click *Preview addresses* and compare the addresses with your wallet's first receive addresses.
   - Confirm both checkboxes and save.
3. **Optional, choose sources and name:** go to *Server settings → Bitcoin BLAKE2b*. There you can:
   - choose which chain data sources to use;
   - test them;
   - change the display name;
   - let store owners use their own sources.
4. **Test it:** create a small invoice and pay it from your XBT wallet.

Set your wallet's **gap limit** well above the number of unpaid invoices you expect, for example 100. Otherwise the wallet may not show payments to later addresses. Every invoice uses a new address, including invoices that are never paid.

### Using your own explorer or Electrum server

Your own source is the most private option: public explorers see which addresses you watch. Enter any of these:

| Source | Example |
| --- | --- |
| mempool or Esplora explorer (its API URL) | `https://mempool.mynode.local/api` |
| Electrum server with TLS | `ssl://electrs.mynode.local:50002` or `electrs.mynode.local:50002:s` |
| Electrum server without TLS (local network only) | `tcp://192.168.1.20:50001` |
| Electrum server with a self-signed certificate | `ssl://electrs.mynode.local:50002?fingerprint=AB:CD:…` (its SHA-256 fingerprint) |

Sources that store owners enter (when the server admin allows it) must be on the public internet. BTCPay refuses to connect them to private or local addresses. Sources the server admin enters may be on the local network.

Use the BLAKE2b builds of these servers, for example [electrs](https://github.com/jasonsopko/electrs), [Fulcrum](https://github.com/privkeyio/Fulcrum) or [mempool](https://github.com/Retropex/mempool). See [awesome-bitcoin-blake](https://github.com/bitcoin-blake/awesome-bitcoin-blake) for more.

### Testnet4

A BTCPay server running on testnet (or regtest) follows **XBT testnet4**, and mainnet follows XBT mainnet. Set `BTCPAY_BTCB2_CHAIN=mainnet` or `testnet4` to override this.

### Configuration for operators

| Environment variable | Purpose |
| --- | --- |
| `BTCPAY_BTCB2_CHAIN` | `mainnet` or `testnet4` |
| `BTCPAY_BTCB2_ESPLORA` | Comma-separated chain data sources. Overrides the admin page and locks it. |
| `BTCPAY_BTCB2_REQUIRED_AGREEMENT` | How many of those sources must agree (default: 2 when several are set, otherwise 1) |
| `BTCPAY_BTCB2_POLL_SECONDS` | How often pending invoices are checked (default 15) |

The internal currency code is `BTCB2`, because BTCPay Server treats `XBT` as another name for Bitcoin. Use `BTCB2` for invoices priced directly in XBT and in rate scripts. The default rate rules are:

```
BTCB2_X = BTCB2_USD * BTC_X / BTC_USD;
BTCB2_USD = btcb2(BTCB2_USD);
```

Through the Greenfield API, set a store's wallet with
`PUT /api/v1/stores/{storeId}/payment-methods/BTCB2-CHAIN` and a body of
`{"enabled": true, "config": {"walletKey": "<zpub or descriptor>", "confirmationsRequired": 3}}`.

## Risks to understand

- **Public sources are trusted parties.** They can be wrong, offline or compromised. Requiring agreement between independent operators reduces this risk without removing it. For larger amounts, run your own source.
- **A payment stops counting only on positive evidence.** Enough sources must answer and no longer report it; a silent source never counts as evidence. Confirmed payments need every answering source to agree they are gone, over several minutes. Payments that arrive after an invoice expires are still recorded for 3 days and marked as paid late.
- **The chain is young.** Reorganizations are more likely than on Bitcoin. Raise the confirmation count for large payments.
- **Prices come from thin markets.** The plugin refuses to price when sources look wrong, but check your invoices.
- **Replay protection is opt-in on XBT.** Customers should pay from an XBT wallet that uses it. The checkout warns them not to send BTC.

## Development

Everything runs in Docker. The .NET SDK, BTCPay Server and its dependencies all run in containers, and nothing is installed on the host.

```bash
git clone --recurse-submodules <this repository>
./dev.sh up            # build the plugin and start BTCPay at http://localhost:14142
./scripts/dev-seed.sh  # local admin, API key and store (credentials in the gitignored .dev.env)
./dev.sh restart       # rebuild the plugin and reload BTCPay
./dev.sh test          # unit tests (LIVE_TESTS=1 adds tests against public mainnet sources)
./dev.sh logs          # follow BTCPay's logs
./dev.sh down          # stop everything
```

BTCPay Server is pinned as a git submodule in `submodules/btcpayserver`.

### Testing payment detection without real coins

`./dev.sh fake-chain` points BTCPay at three fake chain sources (two explorers and an Electrum server), and two of them must agree. They poll every 2 seconds. You can drive them with `scripts/fake-chain.sh` (`pay`, `mine`, `reorg`, `drop`, `fail`), or run the end-to-end scenarios:

```bash
./dev.sh fake-chain
python3 scripts/e2e_fake_chain.py
./dev.sh real-chain    # back to the public sources
```

### Packaging

```bash
./dev.sh package       # Release .btcpay package in .build/packed, as Plugin Builder makes it
./dev.sh stock         # the official BTCPay image with only that package installed (http://localhost:14143)
./scripts/stock-smoke.sh
```

## License

MIT
