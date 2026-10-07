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

    /// <summary>How long a two-exchange price can vouch for a single exchange's price.</summary>
    public static readonly TimeSpan ReferenceMaxAge = TimeSpan.FromHours(1);

    /// <param name="reference">
    /// The last price two exchanges agreed on. With only one usable exchange, its price must be
    /// within <see cref="MaxDivergence"/> of a recent reference, so a thin order book on one
    /// exchange can't be moved to misprice invoices while the other is down.
    /// </param>
    public static BidAsk Combine(IEnumerable<ExchangeQuote> quotes, IEnumerable<string> unavailable,
        (decimal Mid, DateTimeOffset At)? reference = null, DateTimeOffset? now = null)
        => CombineDetailed(quotes, unavailable, reference, now).Rate;

    /// <returns>The rate, and how many exchanges it is based on.</returns>
    public static (BidAsk Rate, int Exchanges) CombineDetailed(IEnumerable<ExchangeQuote> quotes, IEnumerable<string> unavailable,
        (decimal Mid, DateTimeOffset At)? reference = null, DateTimeOffset? now = null)
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

        if (usable.Count == 1)
        {
            var single = usable[0];
            var time = now ?? DateTimeOffset.UtcNow;
            if (reference is not { } r || time - r.At > ReferenceMaxAge)
                throw new QuoteUnavailableException(
                    $"Only {single.Source} has a usable XBT price, and there is no recent price from two exchanges to check it against. " + string.Join("; ", problems));
            var drift = Math.Abs(single.Mid - r.Mid) / r.Mid;
            if (drift > MaxDivergence)
                throw new QuoteUnavailableException(
                    $"Only {single.Source} has a usable XBT price ({single.Mid:0.##}), {drift:P1} away from the last two-exchange price ({r.Mid:0.##}). " + string.Join("; ", problems));
            return (new BidAsk(single.Bid, single.Ask), 1);
        }

        var lowest = usable.Min(q => q.Mid);
        var highest = usable.Max(q => q.Mid);
        var divergence = (highest - lowest) / lowest;
        if (divergence > MaxDivergence)
            throw new QuoteUnavailableException(
                $"XBT prices disagree by {divergence:P1} (more than {MaxDivergence:P0}): " +
                string.Join(", ", usable.Select(q => $"{q.Source} {q.Mid:0.##}")));

        return (new BidAsk(usable.Average(q => q.Bid), usable.Average(q => q.Ask)), usable.Count);
    }
}
