#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Chain;

/// <summary>What one source reported for an address, with its chain tip at the time. Source is its URL.</summary>
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
public record AddressView(bool Conclusive, int Responded, IReadOnlyList<AgreedOutput> Outputs, IReadOnlyDictionary<string, int> ReportedBy)
{
    /// <summary>How many of the sources that answered do not report this output at all.</summary>
    public int OmittedBy(string paymentId) => Responded - ReportedBy.GetValueOrDefault(paymentId);
}

public static class ObservationAggregator
{
    /// <summary>
    /// An output counts only if at least <paramref name="requiredAgreement"/> different sources
    /// report the same transaction, output index and amount. Its confirmation count is the
    /// highest that that many sources support, so one source running ahead cannot settle an
    /// invoice early. A source that lists an output more than once still counts once.
    /// </summary>
    public static AddressView Aggregate(IReadOnlyList<SourceObservation> observations, int requiredAgreement)
    {
        if (requiredAgreement < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredAgreement));
        // One report per source and output: duplicates collapse to the least confirmed one.
        var reports = observations
            .DistinctBy(o => o.Source, StringComparer.OrdinalIgnoreCase)
            .SelectMany(o => o.Outputs
                .GroupBy(x => (x.TxId, x.Vout, x.ValueSats))
                .Select(g => (Observation: o, Output: g.MinBy(x => Confirmations(o.TipHeight, x.BlockHeight))!)))
            .ToArray();
        var responded = observations.Select(o => o.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var reportedBy = reports
            .GroupBy(r => $"{r.Output.TxId}-{r.Output.Vout}")
            .ToDictionary(g => g.Key, g => g.Select(r => r.Observation.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        if (responded < requiredAgreement)
            return new AddressView(false, responded, [], reportedBy);

        var agreed = reports
            .GroupBy(r => (r.Output.TxId, r.Output.Vout, r.Output.ValueSats))
            .Where(g => g.Count() >= requiredAgreement)
            .Select(g =>
            {
                var byConfirmations = g
                    .Select(r => (r.Observation.Source, r.Output.BlockHeight, Confirmations: Confirmations(r.Observation.TipHeight, r.Output.BlockHeight)))
                    .OrderByDescending(r => r.Confirmations)
                    .ToArray();
                var supported = byConfirmations[requiredAgreement - 1];
                return new AgreedOutput(g.Key.TxId, g.Key.Vout, g.Key.ValueSats, supported.BlockHeight, supported.Confirmations,
                    byConfirmations.Select(r => r.Source).OrderBy(s => s, StringComparer.Ordinal).ToArray());
            })
            .OrderBy(o => o.TxId, StringComparer.Ordinal).ThenBy(o => o.Vout)
            .ToArray();
        return new AddressView(true, responded, agreed, reportedBy);
    }

    // A confirmed output has at least one confirmation, even if the tip we hold is slightly older than the block.
    public static long Confirmations(long tipHeight, long? blockHeight) =>
        blockHeight is { } h ? Math.Max(1, tipHeight - h + 1) : 0;
}
