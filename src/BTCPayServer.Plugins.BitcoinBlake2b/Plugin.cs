using System;
using System.Net.Http;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.BitcoinBlake2b.Chain;
using BTCPayServer.Plugins.BitcoinBlake2b.Payments;
using BTCPayServer.Plugins.BitcoinBlake2b.Rates;
using BTCPayServer.Plugins.BitcoinBlake2b.Wallet;
using BTCPayServer.Services;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBXplorer;

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
        var bootstrap = ((PluginServiceCollection)services).BootstrapServices;
        var btcpayChain = bootstrap.GetRequiredService<NBXplorerNetworkProvider>().NetworkType;
        var config = bootstrap.GetRequiredService<IConfiguration>();
        var configuredChain = config["BTCB2_CHAIN"];
        var network = new Btcb2Network(Btcb2Network.SelectChain(btcpayChain, configuredChain));

        // Network, currency and pricing
        services.AddSingleton(network);
        services.AddBTCPayNetwork(network);
        services.AddCurrencyData(new CurrencyData
        {
            Code = Btcb2.CryptoCode,
            Name = Btcb2.ChainName,
            Symbol = network.DisplayName,
            Divisibility = Btcb2.Divisibility,
            Crypto = true
        });
        services.AddHttpClient(HttpQuoteSource.HttpClientName);
        services.AddSingleton<IQuoteSource, NeoxExQuoteSource>();
        services.AddSingleton<IQuoteSource, NonKycQuoteSource>();
        services.AddRateProvider<Btcb2RateProvider>();

        // Payment method
        services.AddDefaultPrettyName(network.PaymentMethodId, network.DisplayName);
        services.AddTransactionLinkProvider(network.PaymentMethodId, new DefaultTransactionLinkProvider(network.ExplorerTxLink));
        services.AddHttpClient(EsploraChainSource.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("BTCPayServer-BitcoinBlake2b");
        });
        services.AddSingleton(p => new ChainSources(p.GetRequiredService<IHttpClientFactory>(), network, config["BTCB2_ESPLORA"], config["BTCB2_REQUIRED_AGREEMENT"]));
        services.AddSingleton<ChainMonitor>();
        services.AddSingleton<IAddressUsageCheck>(p => p.GetRequiredService<ChainMonitor>());
        services.AddSingleton<AddressAllocator>();
        services.AddSingleton<Btcb2PaymentHandler>();
        services.AddSingleton<IPaymentMethodHandler>(p => p.GetRequiredService<Btcb2PaymentHandler>());
        services.AddSingleton<ICheckoutModelExtension, Btcb2CheckoutModelExtension>();
        services.AddSingleton<IPaymentLinkExtension, Btcb2PaymentLinkExtension>();
        var pollInterval = TimeSpan.FromSeconds(int.TryParse(config["BTCB2_POLL_SECONDS"], out var s) && s > 0 ? s : 15);
        services.AddSingleton(p => ActivatorUtilities.CreateInstance<Btcb2Listener>(p, pollInterval));
        services.AddHostedService(p => p.GetRequiredService<Btcb2Listener>());

        // UI
        services.AddUIExtension("store-wallets-nav", $"{ViewsDirectory}/NavExtension.cshtml");
        services.AddUIExtension("checkout-end", $"{ViewsDirectory}/CheckoutBody.cshtml");
    }
}
