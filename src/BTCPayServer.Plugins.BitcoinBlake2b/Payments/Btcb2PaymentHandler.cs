#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.BitcoinBlake2b.Chain;
using BTCPayServer.Plugins.BitcoinBlake2b.Wallet;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Payments;

public class Btcb2PaymentHandler(Btcb2Network network, AddressAllocator allocator, ChainMonitor monitor) : IPaymentMethodHandler
{
    /// <summary>Outputs below this are non-standard on Bitcoin-derived chains.</summary>
    static readonly decimal DustThreshold = Money.Satoshis(546).ToDecimal(MoneyUnit.BTC);

    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;
    public PaymentMethodId PaymentMethodId => network.PaymentMethodId;
    public Btcb2Network Network => network;

    class Prepare
    {
        public required Btcb2PaymentMethodConfig Config { get; init; }
        public required NBXplorer.DerivationStrategy.DerivationStrategyBase Strategy { get; init; }
    }

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = network.CryptoCode;
        context.Prompt.Divisibility = network.Divisibility;
        if (context.Prompt.Activated)
        {
            var config = ParsePaymentMethodConfig(context.PaymentMethodConfig);
            if (config.Chain == network.Chain)
                context.State = new Prepare { Config = config, Strategy = WalletKey.ParseDerivation(config.AccountDerivation, network) };
        }
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (context.State is not Prepare prepare)
            throw new PaymentMethodUnavailableException($"The store's XBT wallet was set up for a different chain than this server's ({network.Chain})");

        var prompt = context.Prompt;
        if (context.InvoiceEntity.Type != Client.Models.InvoiceType.TopUp && prompt.Calculate().Due < DustThreshold)
            throw new PaymentMethodUnavailableException("Amount is below the dust threshold");

        // Never create an invoice that cannot be monitored.
        if (!monitor.IsAvailable)
            await monitor.RefreshAsync(CancellationToken.None);
        if (!monitor.IsAvailable)
            throw new PaymentMethodUnavailableException(
                $"Not enough XBT chain data sources are reachable ({monitor.Healthy.Count} of {monitor.Set.RequiredAgreement} required)");

        // Reserved only now, after rates succeeded, so failed invoice attempts don't widen the wallet's address gap.
        var (address, index) = await allocator.ReserveAsync(context.Store.Id, prepare.Strategy, CancellationToken.None);
        prompt.Destination = address.ToString();
        prompt.PaymentMethodFee = 0m;
        prompt.Details = JObject.FromObject(new Btcb2PromptDetails
        {
            KeyIndex = index,
            AccountDerivation = prepare.Config.AccountDerivation,
            ConfirmationsRequired = ConfirmationPolicy.Required(context.InvoiceEntity.SpeedPolicy, prepare.Config.ConfirmationsRequired)
        }, Serializer);
        context.TrackedDestinations.Add(address.ToString());
    }

    public void StripDetailsForNonOwner(object details) => ((Btcb2PromptDetails)details).AccountDerivation = null;

    public Btcb2PaymentMethodConfig ParsePaymentMethodConfig(JToken config) =>
        config.ToObject<Btcb2PaymentMethodConfig>(Serializer) ?? throw new FormatException($"Invalid {nameof(Btcb2PaymentMethodConfig)}");
    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config) => ParsePaymentMethodConfig(config);

    public Btcb2PromptDetails ParsePaymentPromptDetails(JToken details) =>
        details.ToObject<Btcb2PromptDetails>(Serializer) ?? throw new FormatException($"Invalid {nameof(Btcb2PromptDetails)}");
    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details) => ParsePaymentPromptDetails(details);

    public Btcb2PaymentData ParsePaymentDetails(JToken details) =>
        details.ToObject<Btcb2PaymentData>(Serializer) ?? throw new FormatException($"Invalid {nameof(Btcb2PaymentData)}");
    object IPaymentMethodHandler.ParsePaymentDetails(JToken details) => ParsePaymentDetails(details);
}
