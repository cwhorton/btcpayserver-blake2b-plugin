#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Rates;

/// <summary>
/// Price source "btcb2": the XBT/USD rate combined from every configured exchange.
/// BTCPay polls it every minute and keeps the last good value for five minutes.
/// </summary>
public class Btcb2RateProvider(IEnumerable<IQuoteSource> sources) : IRateProvider
{
    public const string SourceId = "btcb2";

    /// <summary>The last price at least two exchanges agreed on, used to check a lone exchange.</summary>
    (decimal Mid, DateTimeOffset At)? _reference;

    public RateSourceInfo RateSourceInfo => new(SourceId, "Bitcoin BLAKE2b (NeoxEX + NonKYC)", "https://neoxa.exchange/trade/BTCB2_USDC");

    public async Task<PairRate[]> GetRatesAsync(CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(sources.Select(async s =>
        {
            try
            {
                return (Quote: await s.GetQuoteAsync(cancellationToken), Error: (string?)null);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                return (Quote: (ExchangeQuote?)null, Error: $"{s.Name}: {ex.Message}");
            }
        }));

        var (rate, exchanges) = QuoteCombiner.CombineDetailed(
            results.Where(r => r.Quote is not null).Select(r => r.Quote!),
            results.Where(r => r.Error is not null).Select(r => r.Error!),
            _reference);
        if (exchanges >= 2)
            _reference = ((rate.Bid + rate.Ask) / 2m, DateTimeOffset.UtcNow);
        return [new PairRate(new CurrencyPair(Btcb2.CryptoCode, "USD"), rate)];
    }
}
