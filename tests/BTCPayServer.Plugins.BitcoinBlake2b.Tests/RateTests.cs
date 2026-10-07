using BTCPayServer.Plugins.BitcoinBlake2b.Rates;
using BTCPayServer.Rating;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Tests;

public class RateTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1791382104381);

    static JObject NeoxExTicker(long computedAt, string pair = "BTCB2_USDC", bool success = true) => JObject.Parse($$"""
        {"success":{{(success ? "true" : "false")}},"pair":"{{pair}}","ticker":{"lastPrice":670.659754,"volume24h":364.37,
         "computedAt":{{computedAt}},"bestBid":666.01000001,"bestAsk":674.65419097,"spread":8.64} }
        """);

    static JObject NonKycMarket(bool isActive = true, bool isPaused = false, string symbol = "BTCB2/USDT") => JObject.Parse($$"""
        {"symbol":"{{symbol}}","primaryTicker":"BTCB2","lastPrice":"680","isActive":{{(isActive ? "true" : "false")}},
         "isPaused":{{(isPaused ? "true" : "false")}},"bestAsk":"680.00","bestBid":"679.87","bestBidNumber":679.87,"bestAskNumber":680}
        """);

    [Fact]
    public void ParsesNeoxExTicker()
    {
        var q = NeoxExQuoteSource.ParseTicker(NeoxExTicker(Now.ToUnixTimeMilliseconds() - 30_000), Now);
        Assert.Equal("NeoxEX", q.Source);
        Assert.Equal(666.01000001m, q.Bid);
        Assert.Equal(674.65419097m, q.Ask);
    }

    [Theory]
    [InlineData(-6 * 60_000)] // older than five minutes
    [InlineData(2 * 60_000)]  // dated in the future
    public void RejectsNeoxExQuoteWithBadTimestamp(long offsetMs)
    {
        var json = NeoxExTicker(Now.ToUnixTimeMilliseconds() + offsetMs);
        Assert.Throws<QuoteUnavailableException>(() => NeoxExQuoteSource.ParseTicker(json, Now));
    }

    [Fact]
    public void RejectsNeoxExWrongMarketOrFailure()
    {
        var ts = Now.ToUnixTimeMilliseconds();
        Assert.Throws<QuoteUnavailableException>(() => NeoxExQuoteSource.ParseTicker(NeoxExTicker(ts, pair: "BTC_USDC"), Now));
        Assert.Throws<QuoteUnavailableException>(() => NeoxExQuoteSource.ParseTicker(NeoxExTicker(ts, success: false), Now));
    }

    [Fact]
    public void ParsesNonKycMarket()
    {
        var q = NonKycQuoteSource.ParseMarket(NonKycMarket());
        Assert.Equal("NonKYC", q.Source);
        Assert.Equal(679.87m, q.Bid);
        Assert.Equal(680m, q.Ask);
    }

    [Fact]
    public void RejectsHaltedOrWrongNonKycMarket()
    {
        Assert.Throws<QuoteUnavailableException>(() => NonKycQuoteSource.ParseMarket(NonKycMarket(isActive: false)));
        Assert.Throws<QuoteUnavailableException>(() => NonKycQuoteSource.ParseMarket(NonKycMarket(isPaused: true)));
        Assert.Throws<QuoteUnavailableException>(() => NonKycQuoteSource.ParseMarket(NonKycMarket(symbol: "BTC/USDT")));
    }

    [Fact]
    public void AveragesAgreeingExchanges()
    {
        var rate = QuoteCombiner.Combine([new("A", 690m, 700m), new("B", 700m, 710m)], []);
        Assert.Equal(695m, rate.Bid);
        Assert.Equal(705m, rate.Ask);
    }

    [Fact]
    public void FallsBackToOneExchangeWhenTheOtherIsDown()
    {
        var rate = QuoteCombiner.Combine([new("A", 690m, 700m)], ["B: timeout"]);
        Assert.Equal(690m, rate.Bid);
        Assert.Equal(700m, rate.Ask);
    }

    [Fact]
    public void RefusesWhenExchangesDisagree()
    {
        // Mids 695 and 755: 8.6% apart.
        var ex = Assert.Throws<QuoteUnavailableException>(() =>
            QuoteCombiner.Combine([new("A", 690m, 700m), new("B", 750m, 760m)], []));
        Assert.Contains("disagree", ex.Message);
    }

    [Fact]
    public void IgnoresThinOrInvalidOrderBooks()
    {
        // B's spread is about 20%, C's bid is above its ask, D has no bid: only A is used.
        var rate = QuoteCombiner.Combine(
            [new("A", 690m, 700m), new("B", 600m, 730m), new("C", 710m, 700m), new("D", 0m, 700m)], []);
        Assert.Equal(690m, rate.Bid);

        var ex = Assert.Throws<QuoteUnavailableException>(() => QuoteCombiner.Combine([new("B", 600m, 730m)], ["A: down"]));
        Assert.Contains("A: down", ex.Message);
        Assert.Contains("spread", ex.Message);
    }

    [Fact]
    public async Task ProviderReturnsUsdRateAndToleratesOneFailingSource()
    {
        var provider = new Btcb2RateProvider([new FakeSource("A", new("A", 690m, 700m)), new FakeSource("B", null)]);
        var rates = await provider.GetRatesAsync(CancellationToken.None);
        var rate = Assert.Single(rates);
        Assert.Equal("BTCB2_USD", rate.CurrencyPair.ToString());
        Assert.Equal(690m, rate.BidAsk.Bid);

        var allDown = new Btcb2RateProvider([new FakeSource("A", null), new FakeSource("B", null)]);
        await Assert.ThrowsAsync<QuoteUnavailableException>(() => allDown.GetRatesAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("BTCB2_USD", 700)]
    [InlineData("BTCB2_EUR", 630)] // 700 USD * (90000 EUR / 100000 USD)
    public void DefaultRulesPriceXbtFromItsOwnSource(string pair, decimal expected)
    {
        // The store's preferred exchange covers everything else, as BTCPay's defaults do.
        var rules = RateRules.Combine([RateRules.Parse("X_X = kraken(X_X);"), RateRules.Parse(string.Join("\n", Btcb2.DefaultRateRules))]);
        var rule = rules.GetRuleFor(CurrencyPair.Parse(pair));
        rule.ExchangeRates.SetRate("kraken", CurrencyPair.Parse("BTC_USD"), new BidAsk(100_000m));
        rule.ExchangeRates.SetRate("kraken", CurrencyPair.Parse("BTC_EUR"), new BidAsk(90_000m));
        rule.ExchangeRates.SetRate(Btcb2RateProvider.SourceId, CurrencyPair.Parse("BTCB2_USD"), new BidAsk(700m));

        Assert.True(rule.Reevaluate(), rule.ToString(true));
        Assert.Equal(expected, rule.BidAsk!.Bid);
        // Never priced from a BTC/"XBT" market.
        Assert.DoesNotContain("XBT", rule.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kraken(BTCB2", rule.ToString());
    }

    class FakeSource(string name, ExchangeQuote? quote) : IQuoteSource
    {
        public string Name => name;
        public Task<ExchangeQuote> GetQuoteAsync(CancellationToken cancellationToken) =>
            quote is null ? throw new HttpRequestException("unreachable") : Task.FromResult(quote);
    }
}
