#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Chain;

/// <summary>
/// Tracks the health of a set of chain data sources and answers questions about addresses by
/// asking all its healthy sources. A source is used only after it proves it follows the
/// Bitcoin BLAKE2b chain: XBT and BTC share addresses, so a BTC explorer would otherwise
/// report real BTC payments as XBT.
/// </summary>
public class ChainMonitor(ChainSourceSet set, Btcb2Network network, ILogger<ChainMonitor> logger)
{
    static readonly TimeSpan CheckpointRecheck = TimeSpan.FromHours(1);
    static readonly TimeSpan TipMaxAge = TimeSpan.FromMinutes(5);

    public class SourceState(IChainSource source)
    {
        public IChainSource Source { get; } = source;
        public bool? OnChain { get; internal set; }
        public DateTimeOffset? CheckpointCheckedAt { get; internal set; }
        public long? TipHeight { get; internal set; }
        public DateTimeOffset? TipCheckedAt { get; internal set; }
        public string? Error { get; internal set; }
        public bool IsHealthy => OnChain == true && Error is null && TipCheckedAt > DateTimeOffset.UtcNow - TipMaxAge;
    }

    readonly SourceState[] _states = set.Sources.Select(s => new SourceState(s)).ToArray();

    public ChainSourceSet Set => set;
    public IReadOnlyList<SourceState> States => _states;
    public IReadOnlyList<SourceState> Healthy => States.Where(s => s.IsHealthy).ToArray();
    public bool IsAvailable => Healthy.Count >= Set.RequiredAgreement;
    public DateTimeOffset? LastRefresh { get; private set; }

    /// <summary>Verifies each source is on the XBT chain (hourly) and updates its chain tip.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_states.Select(s => RefreshAsync(s, cancellationToken)));
        LastRefresh = DateTimeOffset.UtcNow;
    }

    async Task RefreshAsync(SourceState state, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (state.OnChain != true || state.CheckpointCheckedAt < now - CheckpointRecheck)
            {
                var mismatch = await state.Source.CheckCheckpointAsync(network.Checkpoint, cancellationToken);
                state.OnChain = mismatch is null;
                state.CheckpointCheckedAt = now;
                if (mismatch is not null)
                {
                    state.Error = $"Not on the {Btcb2.ChainName} {network.Chain} chain ({mismatch}, expected {network.Checkpoint.BlockHash})";
                    logger.LogWarning("XBT chain source {Source}: {Error}", state.Source.Url, state.Error);
                    return;
                }
            }
            state.TipHeight = await state.Source.GetTipHeightAsync(cancellationToken);
            state.TipCheckedAt = now;
            state.Error = null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (state.Error != ex.Message)
                logger.LogWarning("XBT chain source {Source} unavailable: {Error}", state.Source.Url, ex.Message);
            state.Error = ex.Message;
        }
    }

    /// <summary>Everything the healthy sources agree has been paid to <paramref name="address"/>.</summary>
    public async Task<AddressView> ViewAddressAsync(string address, CancellationToken cancellationToken)
    {
        var observations = await Task.WhenAll(Healthy.Select(async s =>
        {
            try
            {
                var outputs = await s.Source.GetOutputsAsync(address, cancellationToken);
                return new SourceObservation(s.Source.Name, s.TipHeight!.Value, outputs);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("XBT chain source {Source} failed for {Address}: {Error}", s.Source.Url, address, ex.Message);
                return null;
            }
        }));
        return ObservationAggregator.Aggregate(observations.OfType<SourceObservation>().ToArray(), Set.RequiredAgreement);
    }

    /// <summary>
    /// True if any healthy source has seen a transaction for the address. Throws if too few
    /// sources answer, because an invoice must not be created when it cannot be monitored.
    /// </summary>
    public async Task<bool> IsUsedAsync(string address, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            await RefreshAsync(cancellationToken);
        var counts = await Task.WhenAll(Healthy.Select(async s =>
        {
            try { return (int?)await s.Source.GetTransactionCountAsync(address, cancellationToken); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { return null; }
        }));
        var answered = counts.OfType<int>().ToArray();
        if (answered.Length < Set.RequiredAgreement)
            throw new PaymentMethodUnavailableException(
                $"Only {answered.Length} XBT chain data source(s) reachable, {Set.RequiredAgreement} required");
        return answered.Any(c => c > 0);
    }
}
