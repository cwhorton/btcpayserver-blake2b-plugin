using BTCPayServer.Plugins.BitcoinBlake2b.Rates;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
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
    public void LoneExchangeNeedsARecentTwoExchangePrice()
    {
        var now = DateTimeOffset.UtcNow;
        // No reference: refused rather than trusting one thin market.
        Assert.Throws<QuoteUnavailableException>(() => QuoteCombiner.Combine([new("A", 690m, 700m)], ["B: timeout"], null, now));
        // Close to a recent two-exchange price: accepted.
        var rate = QuoteCombiner.Combine([new("A", 690m, 700m)], ["B: timeout"], (697m, now.AddMinutes(-10)), now);
        Assert.Equal(690m, rate.Bid);
        // Moved more than 5% from it, or the reference is too old: refused.
        Assert.Throws<QuoteUnavailableException>(() => QuoteCombiner.Combine([new("A", 760m, 770m)], [], (697m, now.AddMinutes(-10)), now));
        Assert.Throws<QuoteUnavailableException>(() => QuoteCombiner.Combine([new("A", 690m, 700m)], [], (697m, now.AddHours(-2)), now));
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
        // B's spread is about 20%, C's bid is above its ask, D has no bid: only A is used,
        // and only because a recent reference vouches for it.
        var (rate, exchanges) = QuoteCombiner.CombineDetailed(
            [new("A", 690m, 700m), new("B", 600m, 730m), new("C", 710m, 700m), new("D", 0m, 700m)], [], (695m, DateTimeOffset.UtcNow));
        Assert.Equal(690m, rate.Bid);
        Assert.Equal(1, exchanges);

        var ex = Assert.Throws<QuoteUnavailableException>(() => QuoteCombiner.Combine([new("B", 600m, 730m)], ["A: down"]));
        Assert.Contains("A: down", ex.Message);
        Assert.Contains("spread", ex.Message);
    }

    [Fact]
    public async Task ProviderReturnsUsdRateAndToleratesOneFailingSourceAfterAgreement()
    {
        var b = new FakeSource("B", new("B", 700m, 710m));
        var provider = Provider(new FakeSource("A", new("A", 690m, 700m)), b);
        var rate = Assert.Single(await provider.GetRatesAsync(CancellationToken.None));
        Assert.Equal("BTCB2_USD", rate.CurrencyPair.ToString());
        Assert.Equal(695m, rate.BidAsk.Bid);

        // B goes down: A alone is accepted because it is close to the price both agreed on.
        b.Quote = null;
        await provider.GetSnapshotAsync(CancellationToken.None, forceRefresh: true);
        Assert.Equal(690m, Assert.Single(await provider.GetRatesAsync(CancellationToken.None)).BidAsk.Bid);

        // A fresh provider with one source down has nothing to check it against.
        var fresh = Provider(new FakeSource("A", new("A", 690m, 700m)), new FakeSource("B", null));
        await Assert.ThrowsAsync<QuoteUnavailableException>(() => fresh.GetRatesAsync(CancellationToken.None));
        var allDown = Provider(new FakeSource("A", null), new FakeSource("B", null));
        await Assert.ThrowsAsync<QuoteUnavailableException>(() => allDown.GetRatesAsync(CancellationToken.None));
    }

    static Btcb2RateProvider Provider(params IQuoteSource[] sources) => Provider(new Btcb2PricingSettings(), 0m, sources);

    static Btcb2RateProvider Provider(Btcb2PricingSettings pricing, decimal storeAdjustment, params IQuoteSource[] sources) =>
        new(sources, () => pricing, _ => Task.FromResult(new Btcb2StorePricingSettings { AdjustmentPercent = storeAdjustment }));

    [Fact]
    public async Task SnapshotExplainsEachExchange()
    {
        var provider = Provider(new FakeSource("A", new("A", 690m, 700m)), new FakeSource("B", new("B", 500m, 700m)), new FakeSource("C", null));
        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);
        // B is too thin and C unreachable; A alone has no reference, so there is no price.
        Assert.Null(snapshot.Rate);
        Assert.NotNull(snapshot.Error);
        Assert.Contains("spread", snapshot.Quotes.Single(q => q.Exchange == "B").Problem);
        Assert.Equal("unreachable", snapshot.Quotes.Single(q => q.Exchange == "C").Problem);
        Assert.All(snapshot.Quotes, q => Assert.False(q.Used));
    }

    [Fact]
    public async Task AdminCanTurnExchangesOffAndTrustTheOneLeft()
    {
        var pricing = new Btcb2PricingSettings { DisabledExchanges = ["B"] };
        var provider = Provider(pricing, 0m, new FakeSource("A", new("A", 690m, 700m)), new FakeSource("B", new("B", 900m, 910m)));
        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(690m, snapshot.Rate!.Bid);
        Assert.True(snapshot.Quotes.Single(q => q.Exchange == "B").Disabled);
        Assert.True(snapshot.Quotes.Single(q => q.Exchange == "A").Used);
    }

    [Fact]
    public async Task AdminLimitsAreApplied()
    {
        // 4% apart: fine with the default 5% limit, refused with a 3% limit.
        IQuoteSource[] quotes = [new FakeSource("A", new("A", 700m, 700m)), new FakeSource("B", new("B", 728m, 728m))];
        Assert.NotNull((await Provider(quotes).GetSnapshotAsync(CancellationToken.None)).Rate);
        var strict = Provider(new Btcb2PricingSettings { MaxDivergencePercent = 3m }, 0m, quotes);
        Assert.Null((await strict.GetSnapshotAsync(CancellationToken.None)).Rate);
    }

    [Theory]
    [InlineData(0, 700)]
    [InlineData(2, 686.27)]   // 700 / 1.02: a 100 USD invoice costs 102 USD worth of XBT
    [InlineData(-5, 736.84)]  // 700 / 0.95: a discount
    public async Task StoreAdjustmentChangesWhatCustomersPay(decimal adjustment, decimal expectedRate)
    {
        var provider = Provider(new Btcb2PricingSettings(), adjustment, new FakeSource("A", new("A", 700m, 700m)), new FakeSource("B", new("B", 700m, 700m)));
        var forStore = await provider.GetRatesAsync(new StoreIdRateContext("store"), CancellationToken.None);
        Assert.Equal(expectedRate, Math.Round(Assert.Single(forStore).BidAsk.Bid, 2));
        // Without a store, the plain exchange price.
        Assert.Equal(700m, Assert.Single(await provider.GetRatesAsync(CancellationToken.None)).BidAsk.Bid);
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
        public ExchangeQuote? Quote { get; set; } = quote;
        public string Name => name;
        public string Market => $"XBT/{name}";
        public string TradeUrl => "https://example.com";
        public Task<ExchangeQuote> GetQuoteAsync(CancellationToken cancellationToken) =>
            Quote is null ? throw new HttpRequestException("unreachable") : Task.FromResult(Quote);
    }
}
