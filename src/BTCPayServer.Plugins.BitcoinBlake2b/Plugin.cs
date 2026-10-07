using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Plugins.BitcoinBlake2b.Rates;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.BitcoinBlake2b;

public class Plugin : BaseBTCPayServerPlugin
{
    public const string Area = "BitcoinBlake2b";
    public const string ViewsDirectory = "/Plugins/" + Area + "/Views";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.4.5" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddUIExtension("user-nav", $"{ViewsDirectory}/NavExtension.cshtml");

        services.AddCurrencyData(new CurrencyData
        {
            Code = Btcb2.CryptoCode,
            Name = Btcb2.ChainName,
            Symbol = Btcb2.DefaultDisplayName,
            Divisibility = Btcb2.Divisibility,
            Crypto = true
        });

        services.AddHttpClient(HttpQuoteSource.HttpClientName);
        services.AddSingleton<IQuoteSource, NeoxExQuoteSource>();
        services.AddSingleton<IQuoteSource, NonKycQuoteSource>();
        services.AddRateProvider<Btcb2RateProvider>();
        services.AddSingleton(new DefaultRules(Btcb2.DefaultRateRules));
    }
}
