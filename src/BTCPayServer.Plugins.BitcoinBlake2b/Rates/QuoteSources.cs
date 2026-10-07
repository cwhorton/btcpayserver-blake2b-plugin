#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Rates;

public interface IQuoteSource
{
    string Name { get; }
    Task<ExchangeQuote> GetQuoteAsync(CancellationToken cancellationToken);
}

public abstract class HttpQuoteSource(IHttpClientFactory httpClientFactory) : IQuoteSource
{
    public const string HttpClientName = "BitcoinBlake2b.Rates";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public abstract string Name { get; }
    protected abstract string Endpoint { get; }
    protected abstract ExchangeQuote Parse(JObject json, DateTimeOffset now);

    public async Task<ExchangeQuote> GetQuoteAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(Endpoint, cts.Token);
        response.EnsureSuccessStatusCode();
        var json = JObject.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        return Parse(json, DateTimeOffset.UtcNow);
    }
}

/// <summary>NeoxEX XBT/USDC spot market (its API calls the asset BTCB2).</summary>
public class NeoxExQuoteSource(IHttpClientFactory httpClientFactory) : HttpQuoteSource(httpClientFactory)
{
    public const string Url = "https://neoxa.exchange/api/exchange/ticker/BTCB2_USDC";
    static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    public override string Name => "NeoxEX";
    protected override string Endpoint => Url;
    protected override ExchangeQuote Parse(JObject json, DateTimeOffset now) => ParseTicker(json, now);

    public static ExchangeQuote ParseTicker(JObject json, DateTimeOffset now)
    {
        if (json.Value<bool?>("success") != true || json.Value<string>("pair") != "BTCB2_USDC")
            throw new QuoteUnavailableException("NeoxEX did not return the BTCB2_USDC market");
        var ticker = json["ticker"] as JObject ?? throw new QuoteUnavailableException("NeoxEX response has no ticker");
        var computedAt = ticker.Value<long?>("computedAt") ?? throw new QuoteUnavailableException("NeoxEX quote has no timestamp");
        var age = now - DateTimeOffset.FromUnixTimeMilliseconds(computedAt);
        if (age > MaxAge || age < TimeSpan.FromMinutes(-1))
            throw new QuoteUnavailableException($"NeoxEX quote is stale or future-dated ({age.TotalSeconds:0}s old)");
        return new ExchangeQuote("NeoxEX",
            ticker.Value<decimal?>("bestBid") ?? 0m,
            ticker.Value<decimal?>("bestAsk") ?? 0m);
    }
}

/// <summary>NonKYC BTCB2/USDT market.</summary>
public class NonKycQuoteSource(IHttpClientFactory httpClientFactory) : HttpQuoteSource(httpClientFactory)
{
    public const string Url = "https://api.nonkyc.io/api/v2/market/getbysymbol/BTCB2_USDT";

    public override string Name => "NonKYC";
    protected override string Endpoint => Url;
    protected override ExchangeQuote Parse(JObject json, DateTimeOffset now) => ParseMarket(json);

    // The order book is live, so there is no quote timestamp to check; a halted market is rejected instead.
    public static ExchangeQuote ParseMarket(JObject json)
    {
        if (json.Value<string>("symbol") != "BTCB2/USDT")
            throw new QuoteUnavailableException("NonKYC did not return the BTCB2/USDT market");
        if (json.Value<bool?>("isActive") != true || json.Value<bool?>("isPaused") == true)
            throw new QuoteUnavailableException("NonKYC BTCB2/USDT market is inactive or paused");
        return new ExchangeQuote("NonKYC",
            json.Value<decimal?>("bestBidNumber") ?? 0m,
            json.Value<decimal?>("bestAskNumber") ?? 0m);
    }
}
