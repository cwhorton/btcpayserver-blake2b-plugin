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

public class Btcb2PaymentHandler(Btcb2Network network, AddressAllocator allocator, ChainSources sources) : IPaymentMethodHandler
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
        var monitor = await sources.GetMonitorForStoreAsync(context.Store.Id);
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

    /// <summary>
    /// For the Greenfield API (PUT /api/v1/stores/{storeId}/payment-methods/BTCB2-CHAIN). The config
    /// is a wallet key string, or {"walletKey": "...", "confirmationsRequired": 3}. It is parsed and
    /// checked exactly like the store settings page does.
    /// </summary>
    public Task ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext context)
    {
        string? walletKey = null;
        int? confirmations = null;
        if (context.Config is JValue { Type: JTokenType.String } s)
            walletKey = s.Value<string>();
        else if (context.Config is JObject o)
        {
            walletKey = o.Value<string>("walletKey") ?? o.Value<string>("accountOriginal") ?? o.Value<string>("accountDerivation");
            confirmations = o.Value<int?>("confirmationsRequired");
        }
        if (confirmations is < 0 or > 100)
            context.ModelState.AddModelError("config.confirmationsRequired", "Must be between 0 and 100");
        try
        {
            var settings = WalletKey.Parse(walletKey ?? "", network);
            context.Config = JObject.FromObject(new Btcb2PaymentMethodConfig
            {
                AccountDerivation = settings.AccountDerivation.ToString(),
                AccountOriginal = walletKey!.Trim(),
                Chain = network.Chain,
                ConfirmationsRequired = confirmations
            }, Serializer);
        }
        catch (FormatException ex)
        {
            context.ModelState.AddModelError("config.walletKey", ex.Message);
        }
        return Task.CompletedTask;
    }

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
