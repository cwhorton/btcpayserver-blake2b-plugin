#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
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
public sealed class ChainMonitor(ChainSourceSet set, Btcb2Network network, ILogger<ChainMonitor> logger) : IDisposable
{
    static readonly TimeSpan CheckpointRecheck = TimeSpan.FromHours(1);
    static readonly TimeSpan TipMaxAge = TimeSpan.FromMinutes(5);
    /// <summary>A source this many blocks behind the others is not keeping up and isn't used.</summary>
    const long MaxBlocksBehind = 3;

    public class SourceState(IChainSource source)
    {
        public IChainSource Source { get; } = source;
        public bool? OnChain { get; internal set; }
        public DateTimeOffset? CheckpointCheckedAt { get; internal set; }
        public long? TipHeight { get; internal set; }
        public DateTimeOffset? TipCheckedAt { get; internal set; }
        public long BlocksBehind { get; internal set; }
        /// <summary>Safe to show to store owners: never contains content sent by the source.</summary>
        public string? Error { get; internal set; }
        public bool IsHealthy => OnChain == true && Error is null && BlocksBehind <= MaxBlocksBehind && TipCheckedAt > DateTimeOffset.UtcNow - TipMaxAge;
    }

    readonly SourceState[] _states = set.Sources.Select(s => new SourceState(s)).ToArray();
    readonly SemaphoreSlim _refreshing = new(1, 1);

    public ChainSourceSet Set => set;
    public IReadOnlyList<SourceState> States => _states;
    public IReadOnlyList<SourceState> Healthy => _states.Where(s => s.IsHealthy).ToArray();
    public bool IsAvailable => Healthy.Count >= Set.RequiredAgreement;
    public long TipHeight => Healthy.Select(h => h.TipHeight ?? 0).DefaultIfEmpty(0).Max();
    public DateTimeOffset? LastRefresh { get; private set; }
    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Verifies each source is on the XBT chain (hourly) and updates its chain tip. Concurrent
    /// callers share one refresh instead of querying every source again.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        LastUsed = DateTimeOffset.UtcNow;
        var started = DateTimeOffset.UtcNow;
        await _refreshing.WaitAsync(cancellationToken);
        try
        {
            if (LastRefresh >= started)
                return;
            await Task.WhenAll(_states.Select(s => RefreshAsync(s, cancellationToken)));
            var best = _states.Where(s => s.OnChain == true && s.Error is null).Select(s => s.TipHeight ?? 0).DefaultIfEmpty(0).Max();
            foreach (var s in _states)
                s.BlocksBehind = s.TipHeight is { } tip ? Math.Max(0, best - tip) : 0;
            LastRefresh = DateTimeOffset.UtcNow;
        }
        finally
        {
            _refreshing.Release();
        }
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
            var error = Describe(ex);
            if (state.Error != error)
                logger.LogWarning("XBT chain source {Source} unavailable: {Error}", state.Source.Url, ex is ChainSourceException c ? c.Detail : ex.Message);
            state.Error = error;
        }
    }

    /// <summary>
    /// A short explanation for the UI. Sources may be on a store owner's chosen host, so nothing
    /// they sent is repeated, only what kind of failure it was.
    /// </summary>
    public static string Describe(Exception ex) => ex switch
    {
        ChainSourceException c => c.Message,
        HttpRequestException { InnerException: ChainSourceException c } => c.Message,
        TaskCanceledException or OperationCanceledException or TimeoutException => "Timed out",
        AuthenticationException => "Its TLS certificate is not trusted (for a self-signed certificate, pin its fingerprint)",
        HttpRequestException { StatusCode: { } code } => $"Answered HTTP {(int)code}",
        HttpRequestException or SocketException => "Could not connect",
        IOException => "Connection lost",
        _ => "Unexpected answer"
    };

    /// <summary>Everything the healthy sources agree has been paid to <paramref name="address"/>.</summary>
    public async Task<AddressView> ViewAddressAsync(string address, CancellationToken cancellationToken)
    {
        LastUsed = DateTimeOffset.UtcNow;
        var observations = await Task.WhenAll(Healthy.Select(async s =>
        {
            try
            {
                var outputs = await s.Source.GetOutputsAsync(address, cancellationToken);
                return new SourceObservation(s.Source.Url, s.TipHeight!.Value, outputs);
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
        LastUsed = DateTimeOffset.UtcNow;
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

    public void Dispose()
    {
        foreach (var source in set.Sources.OfType<IDisposable>())
            source.Dispose();
        _refreshing.Dispose();
    }
}
