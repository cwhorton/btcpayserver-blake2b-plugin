#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Rates;

/// <summary>What one exchange contributed to the last price.</summary>
public record QuoteStatus(string Exchange, string Market, string TradeUrl, ExchangeQuote? Quote, string? Problem, bool Used, bool Disabled);

/// <summary>The last XBT/USD price and how it was made, for display and for invoices.</summary>
public record PriceSnapshot(DateTimeOffset Time, IReadOnlyList<QuoteStatus> Quotes, BidAsk? Rate, DateTimeOffset? RateTime, int Exchanges, string? Error);

/// <summary>
/// Price source "btcb2": the XBT/USD rate combined from the enabled exchanges, refreshed every
/// minute. For a store, the store's own XBT price adjustment is applied.
/// </summary>
public class Btcb2RateProvider(
    IEnumerable<IQuoteSource> sources,
    Func<Btcb2PricingSettings> pricingSettings,
    Func<string, Task<Btcb2StorePricingSettings>> storePricing) : IContextualRateProvider
{
    public const string SourceId = "btcb2";
    static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    /// <summary>When the exchanges can't be used right now, the last good price stays valid this long, like BTCPay's other price sources.</summary>
    static readonly TimeSpan MaxPriceAge = TimeSpan.FromMinutes(5);

    readonly IQuoteSource[] _sources = sources.ToArray();
    readonly SemaphoreSlim _refreshing = new(1, 1);
    PriceSnapshot? _snapshot;
    (BidAsk Rate, DateTimeOffset At, int Exchanges)? _lastGood;
    /// <summary>The last price at least two exchanges agreed on, used to check a lone exchange.</summary>
    (decimal Mid, DateTimeOffset At)? _reference;

    public RateSourceInfo RateSourceInfo => new(SourceId, "Bitcoin BLAKE2b (NeoxEX + NonKYC)", "https://neoxa.exchange/trade/BTCB2_USDC");
    public IReadOnlyList<IQuoteSource> Exchanges => _sources;

    public async Task<PairRate[]> GetRatesAsync(CancellationToken cancellationToken) =>
        [new PairRate(new CurrencyPair(Btcb2.CryptoCode, "USD"), await GetBaseRateAsync(cancellationToken))];

    public async Task<PairRate[]> GetRatesAsync(IRateContext context, CancellationToken cancellationToken)
    {
        var rate = await GetBaseRateAsync(cancellationToken);
        if (context is IHasStoreIdRateContext { StoreId: { } storeId })
        {
            var pricing = await storePricing(storeId);
            rate = new BidAsk(pricing.Apply(rate.Bid), pricing.Apply(rate.Ask));
        }
        return [new PairRate(new CurrencyPair(Btcb2.CryptoCode, "USD"), rate)];
    }

    async Task<BidAsk> GetBaseRateAsync(CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.Rate ?? throw new QuoteUnavailableException(snapshot.Error ?? "No XBT price");
    }

    /// <summary>The current price, refreshed if older than a minute. Concurrent callers share one refresh.</summary>
    public async Task<PriceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var requested = DateTimeOffset.UtcNow;
        if (!forceRefresh && _snapshot is { } fresh && requested - fresh.Time < RefreshInterval)
            return fresh;
        await _refreshing.WaitAsync(cancellationToken);
        try
        {
            if (_snapshot is { } current && current.Time >= requested.Add(forceRefresh ? TimeSpan.Zero : -RefreshInterval))
                return current;
            return _snapshot = await RefreshAsync(cancellationToken);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    async Task<PriceSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        var pricing = pricingSettings();
        var enabled = _sources.Where(s => pricing.IsEnabled(s.Name)).ToArray();
        var results = await Task.WhenAll(enabled.Select(async s =>
        {
            try
            {
                return (Source: s, Quote: (ExchangeQuote?)await s.GetQuoteAsync(cancellationToken), Error: (string?)null);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                return (Source: s, Quote: null, Error: ex.Message);
            }
        }));
        var now = DateTimeOffset.UtcNow;
        var problems = results.ToDictionary(r => r.Source.Name,
            r => r.Error ?? (r.Quote is { } q ? QuoteCombiner.Reject(q, pricing.MaxSpread) : null));

        BidAsk? rate = null;
        DateTimeOffset? rateTime = null;
        int exchanges = 0;
        string? error = null;
        try
        {
            (var combined, exchanges) = QuoteCombiner.CombineDetailed(
                results.Where(r => r.Quote is not null).Select(r => r.Quote!),
                results.Where(r => r.Error is not null).Select(r => $"{r.Source.Name}: {r.Error}"),
                _reference, now, pricing.MaxSpread, pricing.MaxDivergence,
                // An admin who enabled only one exchange chose to trust it alone.
                loneExchangeNeedsReference: enabled.Length > 1);
            if (exchanges >= 2)
                _reference = ((combined.Bid + combined.Ask) / 2m, now);
            _lastGood = (combined, now, exchanges);
            (rate, rateTime) = (combined, now);
        }
        catch (QuoteUnavailableException ex)
        {
            error = enabled.Length == 0 ? "All exchanges are disabled" : ex.Message;
            if (_lastGood is { } last && now - last.At < MaxPriceAge)
                (rate, rateTime, exchanges) = (last.Rate, last.At, last.Exchanges);
        }

        var statuses = _sources.Select(s =>
        {
            if (!pricing.IsEnabled(s.Name))
                return new QuoteStatus(s.Name, s.Market, s.TradeUrl, null, null, false, true);
            var result = results.First(r => r.Source == s);
            var problem = problems[s.Name];
            return new QuoteStatus(s.Name, s.Market, s.TradeUrl, result.Quote, problem, error is null && problem is null && result.Quote is not null, false);
        }).ToArray();
        return new PriceSnapshot(now, statuses, rate, rateTime, exchanges, error);
    }
}

/// <summary>Each store's XBT price adjustment.</summary>
public class Btcb2StorePricing(StoreRepository storeRepository)
{
    readonly ConcurrentDictionary<string, Btcb2StorePricingSettings> _cache = new();

    public async Task<Btcb2StorePricingSettings> GetAsync(string storeId)
    {
        if (_cache.TryGetValue(storeId, out var cached))
            return cached;
        var settings = await storeRepository.GetSettingAsync<Btcb2StorePricingSettings>(storeId, Btcb2StorePricingSettings.SettingName) ?? new();
        return _cache[storeId] = settings;
    }

    public async Task SaveAsync(string storeId, Btcb2StorePricingSettings settings)
    {
        await storeRepository.UpdateSetting(storeId, Btcb2StorePricingSettings.SettingName, settings);
        _cache[storeId] = settings;
    }
}
