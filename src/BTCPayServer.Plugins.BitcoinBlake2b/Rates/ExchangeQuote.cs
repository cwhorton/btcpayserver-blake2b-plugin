#nullable enable
using System;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Rates;

/// <summary>
/// Best bid and ask for XBT on one exchange, in USD-equivalent stablecoins (treated as USD).
/// </summary>
public record ExchangeQuote(string Source, decimal Bid, decimal Ask)
{
    public decimal Mid => (Bid + Ask) / 2m;
    public decimal SpreadRatio => Mid == 0m ? decimal.MaxValue : (Ask - Bid) / Mid;
}

public class QuoteUnavailableException(string message) : Exception(message);
