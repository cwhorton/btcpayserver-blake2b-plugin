using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
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
        var configuredChain = bootstrap.GetService<IConfiguration>()?["BTCB2_CHAIN"];
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
        services.AddSingleton<IAddressUsageCheck, NoAddressUsageCheck>();
        services.AddSingleton<AddressAllocator>();
        services.AddSingleton<Btcb2PaymentHandler>();
        services.AddSingleton<IPaymentMethodHandler>(p => p.GetRequiredService<Btcb2PaymentHandler>());
        services.AddSingleton<ICheckoutModelExtension, Btcb2CheckoutModelExtension>();
        services.AddSingleton<IPaymentLinkExtension, Btcb2PaymentLinkExtension>();

        // UI
        services.AddUIExtension("store-wallets-nav", $"{ViewsDirectory}/NavExtension.cshtml");
        services.AddUIExtension("checkout-end", $"{ViewsDirectory}/CheckoutBody.cshtml");
    }
}
