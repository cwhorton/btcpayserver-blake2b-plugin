#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Chain;

/// <summary>Applies the admin's saved settings (display name, chain data sources) at startup.</summary>
public class Btcb2SettingsLoader(ChainSources sources) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => sources.EnsureLoadedAsync();
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
