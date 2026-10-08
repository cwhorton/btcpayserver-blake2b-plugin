#nullable enable
using System;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Rates;

/// <summary>Server-wide pricing settings, managed by the server admin.</summary>
public class Btcb2PricingSettings
{
    /// <summary>Exchanges (by name) not used for pricing.</summary>
    public string[] DisabledExchanges { get; set; } = [];
    /// <summary>An exchange whose bid/ask spread is wider than this is ignored as too thin.</summary>
    public decimal MaxSpreadPercent { get; set; } = 5m;
    /// <summary>
    /// Exchanges disagreeing by more than this stop XBT pricing. A lone exchange must also be
    /// this close to the last price two exchanges agreed on.
    /// </summary>
    public decimal MaxDivergencePercent { get; set; } = 5m;

    [JsonIgnore]
    public decimal MaxSpread => MaxSpreadPercent / 100m;
    [JsonIgnore]
    public decimal MaxDivergence => MaxDivergencePercent / 100m;

    public const decimal MinLimitPercent = 0.5m;
    public const decimal MaxLimitPercent = 25m;

    public bool IsEnabled(string exchange) =>
        !Array.Exists(DisabledExchanges, e => string.Equals(e, exchange, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A store's own XBT pricing.</summary>
public class Btcb2StorePricingSettings
{
    public const string SettingName = "BitcoinBlake2b.Pricing";
    public const decimal MinAdjustmentPercent = -50m;
    public const decimal MaxAdjustmentPercent = 50m;

    /// <summary>
    /// How much more (positive) or less (negative) customers pay when paying in XBT, in percent.
    /// +2 means a 100 USD invoice costs 102 USD worth of XBT.
    /// </summary>
    public decimal AdjustmentPercent { get; set; }

    /// <summary>The exchange rate (USD per XBT) that makes customers pay the adjusted amount.</summary>
    public decimal Apply(decimal rate) => rate / (1m + AdjustmentPercent / 100m);
}
