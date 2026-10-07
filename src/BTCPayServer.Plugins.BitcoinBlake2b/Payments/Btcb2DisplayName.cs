#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.BitcoinBlake2b.Chain;
using BTCPayServer.Plugins.Translations;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Payments;

/// <summary>
/// Shows the admin's display name for XBT on every checkout, including the payment method
/// tab while another method is selected. Applies immediately when the name changes.
/// </summary>
public class Btcb2GlobalCheckoutExtension(Btcb2Network network) : IGlobalCheckoutModelExtension
{
    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        foreach (var method in context.Model.AvailablePaymentMethods.Where(m => m.PaymentMethodId == network.PaymentMethodId))
            method.PaymentMethodName = network.DisplayName;
        if (context.Model.PaymentMethodId == network.PaymentMethodId.ToString())
            context.Model.PaymentMethodName = network.DisplayName;
    }
}

/// <summary>
/// The payment method's name on BTCPay's other pages. BTCPay loads it once at startup, so a
/// changed display name reaches those pages after a restart.
/// </summary>
public class Btcb2TranslationProvider(Btcb2Network network, ChainSources sources) : IDefaultTranslationProvider
{
    public async Task<KeyValuePair<string, string?>[]> GetDefaultTranslations()
    {
        await sources.EnsureLoadedAsync();
        return [KeyValuePair.Create<string, string?>(PrettyNameProvider.GetTranslationKey(network.PaymentMethodId), network.DisplayName)];
    }
}
