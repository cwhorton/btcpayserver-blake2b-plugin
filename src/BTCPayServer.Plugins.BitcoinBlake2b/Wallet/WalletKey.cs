#nullable enable
using System;
using System.Linq;
using System.Text.RegularExpressions;
using NBitcoin;
using NBXplorer.DerivationStrategy;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Wallet;

/// <summary>Parses a merchant's watch-only wallet key and derives its receive addresses.</summary>
public static class WalletKey
{
    static readonly Regex PrivateKeyPattern = new(@"\b[xyztuv]prv[1-9A-HJ-NP-Za-km-z]{20,}", RegexOptions.Compiled);
    static readonly Regex ExtPubKeyPattern = new(@"\b([xyztuvXYZTUV]pub)[1-9A-HJ-NP-Za-km-z]{20,}", RegexOptions.Compiled);
    static readonly string[] MainnetPrefixes = ["xpub", "ypub", "zpub", "Ypub", "Zpub"];
    static readonly string[] TestnetPrefixes = ["tpub", "upub", "vpub", "Upub", "Vpub"];

    /// <summary>
    /// Accepts what BTCPay accepts for Bitcoin: xpub/ypub/zpub (tpub/upub/vpub on testnet4),
    /// NBXplorer derivation schemes, and output descriptors.
    /// </summary>
    /// <exception cref="FormatException">With a message suitable for the merchant.</exception>
    public static DerivationSchemeSettings Parse(string input, Btcb2Network network)
    {
        input = input?.Trim() ?? "";
        if (input.Length == 0)
            throw new FormatException("Enter your wallet's account public key or output descriptor.");
        if (PrivateKeyPattern.IsMatch(input))
            throw new FormatException("This is a private key. Never paste a private key into BTCPay; use the account public key (xpub, ypub, zpub, or the tpub/upub/vpub testnet equivalents).");

        // BTCPay's own parser silently converts keys between networks; refuse instead, so a
        // mainnet wallet is never set up on testnet4 or the other way around.
        var allowed = network.Chain == Btcb2Chain.Mainnet ? MainnetPrefixes : TestnetPrefixes;
        foreach (Match m in ExtPubKeyPattern.Matches(input))
        {
            if (!allowed.Contains(m.Groups[1].Value))
                throw new FormatException($"This is a {(network.Chain == Btcb2Chain.Mainnet ? "testnet" : "mainnet")} key ({m.Groups[1].Value}), but this server follows {network.Chain}.");
        }

        var parsingNetwork = network.KeyParsingNetwork;
        var parser = new DerivationSchemeParser(parsingNetwork);
        try
        {
            return DerivationSchemeParser.MaybeOD(input)
                ? parser.ParseOD(input)
                : new DerivationSchemeSettings(parser.Parse(input), parsingNetwork);
        }
        catch (Exception ex)
        {
            var expected = network.Chain == Btcb2Chain.Mainnet ? "xpub, ypub or zpub" : "tpub, upub or vpub";
            throw new FormatException($"This is not a valid {network.Chain} wallet key ({ex.Message}). Expected an account public key such as {expected}, or an output descriptor.");
        }
    }

    public static DerivationStrategyBase ParseDerivation(string accountDerivation, Btcb2Network network)
        => network.KeyParsingNetwork.NBXplorerNetwork.DerivationStrategyFactory.Parse(accountDerivation);

    public static BitcoinAddress DeriveReceiveAddress(DerivationStrategyBase strategy, int index, Btcb2Network network)
        => strategy.GetLineFor(DerivationFeature.Deposit).Derive((uint)index).ScriptPubKey.GetDestinationAddress(network.AddressNetwork)
           ?? throw new FormatException("This wallet key does not produce standard addresses.");

    public static string[] PreviewAddresses(DerivationStrategyBase strategy, Btcb2Network network, int count = 5)
        => Enumerable.Range(0, count).Select(i => DeriveReceiveAddress(strategy, i, network).ToString()).ToArray();
}
