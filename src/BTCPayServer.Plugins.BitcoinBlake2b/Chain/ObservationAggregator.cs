#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Chain;

/// <summary>What one source reported for an address, with its chain tip at the time.</summary>
public record SourceObservation(string Source, long TipHeight, IReadOnlyList<ChainOutput> Outputs);

/// <summary>An output that enough sources agree on.</summary>
public record AgreedOutput(string TxId, int Vout, long ValueSats, long? BlockHeight, long Confirmations, string[] SeenBy)
{
    public string PaymentId => $"{TxId}-{Vout}";
}

/// <summary>
/// The combined view of an address. When fewer sources answered than must agree, the view is
/// inconclusive and nothing may be concluded from it, neither payments nor their absence.
/// </summary>
public record AddressView(bool Conclusive, int Responded, IReadOnlyList<AgreedOutput> Outputs);

public static class ObservationAggregator
{
    /// <summary>
    /// An output counts only if at least <paramref name="requiredAgreement"/> sources report the
    /// same transaction, output index and amount. Its confirmation count is the highest that
    /// that many sources support, so one source running ahead cannot settle an invoice early.
    /// </summary>
    public static AddressView Aggregate(IReadOnlyList<SourceObservation> observations, int requiredAgreement)
    {
        if (requiredAgreement < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredAgreement));
        if (observations.Count < requiredAgreement)
            return new AddressView(false, observations.Count, []);

        var agreed = observations
            .SelectMany(o => o.Outputs.Distinct().Select(output => (Observation: o, Output: output)))
            .GroupBy(x => (x.Output.TxId, x.Output.Vout, x.Output.ValueSats))
            .Where(g => g.Count() >= requiredAgreement)
            .Select(g =>
            {
                var reports = g
                    .Select(x => (x.Observation.Source, x.Output.BlockHeight, Confirmations: Confirmations(x.Observation.TipHeight, x.Output.BlockHeight)))
                    .OrderByDescending(r => r.Confirmations)
                    .ToArray();
                var supported = reports[requiredAgreement - 1];
                return new AgreedOutput(g.Key.TxId, g.Key.Vout, g.Key.ValueSats, supported.BlockHeight, supported.Confirmations,
                    reports.Select(r => r.Source).OrderBy(s => s, StringComparer.Ordinal).ToArray());
            })
            .OrderBy(o => o.TxId, StringComparer.Ordinal).ThenBy(o => o.Vout)
            .ToArray();
        return new AddressView(true, observations.Count, agreed);
    }

    // A confirmed output has at least one confirmation, even if the tip we hold is slightly older than the block.
    public static long Confirmations(long tipHeight, long? blockHeight) =>
        blockHeight is { } h ? Math.Max(1, tipHeight - h + 1) : 0;
}
