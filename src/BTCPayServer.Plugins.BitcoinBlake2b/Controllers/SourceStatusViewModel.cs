#nullable enable
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Plugins.BitcoinBlake2b.Chain;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Controllers;

public record SourceStatusViewModel(string Name, string Url, string State, bool Healthy, long? TipHeight, string? Error);

public record SourceSetViewModel(string Description, int RequiredAgreement, bool Available, IReadOnlyList<SourceStatusViewModel> Sources)
{
    public static SourceSetViewModel From(ChainMonitor monitor) => new(
        monitor.Set.Description,
        monitor.Set.RequiredAgreement,
        monitor.IsAvailable,
        monitor.States.Select(s => new SourceStatusViewModel(
            s.Source.Name,
            s.Source.Url,
            s.IsHealthy ? "Working" : s.OnChain == false ? "Wrong chain" : s.Error is not null ? "Unreachable" : "Not checked yet",
            s.IsHealthy,
            s.TipHeight,
            s.Error)).ToArray());
}
