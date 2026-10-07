namespace BTCPayServer.Plugins.BitcoinBlake2b;

/// <summary>
/// Identity of the Bitcoin BLAKE2b chain inside BTCPay Server.
/// </summary>
/// <remarks>
/// The internal code is BTCB2, never XBT: BTCPay Server treats "XBT" as an alias of BTC,
/// and some exchanges (Kraken) use XBT to mean BTC. Using XBT internally could silently
/// price invoices at BTC's rate. "XBT" is only ever a display name.
/// </remarks>
public static class Btcb2
{
    public const string CryptoCode = "BTCB2";
    public const string DefaultDisplayName = "XBT";
    public const string ChainName = "Bitcoin BLAKE2b";
    public const int Divisibility = 8;

    /// <summary>
    /// The price comes from <see cref="Rates.Btcb2RateProvider"/> in USD. Other currencies are
    /// converted from USD through the store's own BTC price source, so no XBT price is ever
    /// taken from a BTC market.
    /// </summary>
    public static readonly string[] DefaultRateRules =
    [
        $"{CryptoCode}_X = {CryptoCode}_USD * BTC_X / BTC_USD;",
        $"{CryptoCode}_USD = {Rates.Btcb2RateProvider.SourceId}({CryptoCode}_USD);"
    ];
}
