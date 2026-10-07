#nullable enable
using System;
using BTCPayServer.Configuration;
using BTCPayServer.Payments;
using BTCPayServer.Services.Rates;
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
/// <summary>A post-fork block: its hash (as explorers report it) and its raw 164-byte header (as Electrum servers do).</summary>
public record ChainCheckpoint(long Height, string BlockHash, string HeaderHex);

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

    /// <summary>Registered with BTCPay; its symbol follows the display name.</summary>
    public CurrencyData CurrencyData { get; } = new()
    {
        Code = Btcb2.CryptoCode,
        Name = Btcb2.ChainName,
        Symbol = Btcb2.DefaultDisplayName,
        Divisibility = Btcb2.Divisibility,
        Crypto = true
    };
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
    public ChainCheckpoint Checkpoint => Chain == Btcb2Chain.Mainnet
        ? new(961_640, "0000000000000050c1e5f69672f459293be14f46e5a494e7a8c8541396f18eeb",
            "000000a0657e02138733654183a2c7320d85ca9d743fe139c4bb01000000000000000000c137a8515a0f6b3aaf6049cc7611787c022ad523d51094be0a0363d0dc0bc7684dca936a4f8d001a5671798c84daeb494dca936a00000000b1ccf00d0300000000000000000000001e0300000000000000000000000000000000000068ac0e000000000000000000000000000000000000000000000000000000000000000000")
        : new(150_308, "000000000000b9d1b7e1bb0e77215ee92c6ef7ec8f4473e23908380649e779b6",
            "000000a0ccb157caa788400a667f6c19858ee913c701a42c8d1cd85122ec17000000000043d2e57990429ae581621ce01aa5fbf5e4c2723996be18660a4930b91e96d6c871b4946affff001dce0ac801d123881f71b4946a00000000b10cf00d0100000000000000000000008e00000000000000000000000000000000000000244b02000000000000000000000000000000000000000000000000000000000000000000");

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
