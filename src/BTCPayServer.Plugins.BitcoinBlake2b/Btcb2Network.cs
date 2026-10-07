#nullable enable
using System;
using BTCPayServer.Configuration;
using BTCPayServer.Payments;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.BitcoinBlake2b;

public enum Btcb2Chain
{
    Mainnet,
    Testnet4
}

/// <summary>
/// The Bitcoin BLAKE2b network as BTCPay Server sees it. It is deliberately not a
/// <see cref="BTCPayNetwork"/>: NBXplorer cannot follow this chain, so none of BTCPay's
/// built-in Bitcoin wallet code must ever pick it up.
/// </summary>
public class Btcb2Network : BTCPayNetworkBase
{
    public Btcb2Network(Btcb2Chain chain)
    {
        Chain = chain;
        CryptoCode = Btcb2.CryptoCode;
        DisplayName = Btcb2.DefaultDisplayName;
        Divisibility = Btcb2.Divisibility;
        CryptoImagePath = "Resources/btcb2.svg";
        DefaultRateRules = Btcb2.DefaultRateRules;
        // XBT addresses are encoded exactly like Bitcoin's (bc1/1/3 on mainnet, tb1 on testnet4).
        var chainName = chain == Btcb2Chain.Mainnet ? ChainName.Mainnet : ChainName.Testnet;
        AddressNetwork = chain == Btcb2Chain.Mainnet ? Network.Main : Network.TestNet;
        _keyParsingNetwork = new Lazy<BTCPayNetwork>(() => new BTCPayNetwork
        {
            CryptoCode = "BTC",
            NBXplorerNetwork = new NBXplorerNetworkProvider(chainName).GetBTC()
        }.SetDefaultElectrumMapping(chainName));
    }

    public Btcb2Chain Chain { get; }
    public PaymentMethodId PaymentMethodId { get; } = PaymentTypes.CHAIN.GetPaymentMethodId(Btcb2.CryptoCode);

    /// <summary>Network used to encode and decode addresses and extended public keys.</summary>
    public Network AddressNetwork { get; }

    readonly Lazy<BTCPayNetwork> _keyParsingNetwork;
    /// <summary>
    /// A stand-in Bitcoin network with the same key and address encoding, used only to parse
    /// wallet keys with BTCPay's own parser. Never registered with BTCPay.
    /// </summary>
    public BTCPayNetwork KeyParsingNetwork => _keyParsingNetwork.Value;

    public string ExplorerTxLink => Chain == Btcb2Chain.Mainnet
        ? "https://mempool.guide/tx/{0}"
        : "https://mempool.guide/testnet4/tx/{0}";

    /// <summary>
    /// A block after the fork. A chain data source must report this hash at this height,
    /// which proves it follows Bitcoin BLAKE2b and not Bitcoin.
    /// </summary>
    public (long Height, string Hash) Checkpoint => Chain == Btcb2Chain.Mainnet
        ? (961_640, "0000000000000050c1e5f69672f459293be14f46e5a494e7a8c8541396f18eeb")
        : (150_308, "000000000000b9d1b7e1bb0e77215ee92c6ef7ec8f4473e23908380649e779b6");

    /// <summary>Independently operated public explorers with an Esplora-style API.</summary>
    public string[] PublicEsploraUrls => Chain == Btcb2Chain.Mainnet
        ? ["https://mempool.guide/api", "https://mempool.lazarus-xbt.xyz/api", "https://mempool.kilombino.com/api"]
        : ["https://mempool.guide/testnet4/api"];

    /// <summary>How many public explorers must agree. Testnet4 has only one public explorer.</summary>
    public int PublicRequiredAgreement => Chain == Btcb2Chain.Mainnet ? 2 : 1;

    /// <summary>
    /// Uses BTCPay's chain unless BTCPAY_BTCB2_CHAIN overrides it. A mainnet BTCPay server
    /// follows XBT mainnet; testnet and regtest servers follow XBT testnet4.
    /// </summary>
    public static Btcb2Chain SelectChain(ChainName btcpayChain, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim().ToLowerInvariant() switch
            {
                "mainnet" or "main" => Btcb2Chain.Mainnet,
                "testnet4" => Btcb2Chain.Testnet4,
                _ => throw new ConfigException($"BTCPAY_BTCB2_CHAIN must be 'mainnet' or 'testnet4', not '{configured}'")
            };
        }
        return btcpayChain == ChainName.Mainnet ? Btcb2Chain.Mainnet : Btcb2Chain.Testnet4;
    }
}
