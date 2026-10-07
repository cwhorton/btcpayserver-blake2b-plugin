#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Rating;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Rates;

/// <summary>
/// Turns individual exchange quotes into one XBT/USD rate, failing closed rather than
/// risking a mispriced invoice.
/// </summary>
public static class QuoteCombiner
{
    /// <summary>A wider bid/ask spread means the order book is too thin to trust.</summary>
    public const decimal MaxSpread = 0.05m;
    /// <summary>If exchanges disagree by more than this, something is wrong with one of them.</summary>
    public const decimal MaxDivergence = 0.05m;

    public static BidAsk Combine(IEnumerable<ExchangeQuote> quotes, IEnumerable<string> unavailable)
    {
        var problems = unavailable.ToList();
        var usable = new List<ExchangeQuote>();
        foreach (var q in quotes)
        {
            if (q.Bid <= 0m || q.Ask < q.Bid)
                problems.Add($"{q.Source}: invalid bid/ask ({q.Bid}/{q.Ask})");
            else if (q.SpreadRatio > MaxSpread)
                problems.Add($"{q.Source}: spread {q.SpreadRatio:P1} is wider than {MaxSpread:P0}");
            else
                usable.Add(q);
        }

        if (usable.Count == 0)
            throw new QuoteUnavailableException("No usable XBT price. " + string.Join("; ", problems));

        var lowest = usable.Min(q => q.Mid);
        var highest = usable.Max(q => q.Mid);
        var divergence = (highest - lowest) / lowest;
        if (divergence > MaxDivergence)
            throw new QuoteUnavailableException(
                $"XBT prices disagree by {divergence:P1} (more than {MaxDivergence:P0}): " +
                string.Join(", ", usable.Select(q => $"{q.Source} {q.Mid:0.##}")));

        return new BidAsk(usable.Average(q => q.Bid), usable.Average(q => q.Ask));
    }
}
