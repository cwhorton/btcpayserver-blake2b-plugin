#nullable enable
using System.Linq;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Payments;

/// <summary>
/// Shows a plain address in XBT's own checkout component: a "bitcoin:" link would open the
/// customer's BTC wallet, and the same address would receive real BTC.
/// </summary>
public class Btcb2CheckoutModelExtension(Btcb2Network network) : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "Btcb2CheckoutBody";

    public PaymentMethodId PaymentMethodId => network.PaymentMethodId;
    public string Image => network.CryptoImagePath;
    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler is not Btcb2PaymentHandler handler)
            return;
        var model = context.Model;
        model.CheckoutBodyComponentName = CheckoutBodyComponentName;
        model.InvoiceBitcoinUrl = model.InvoiceBitcoinUrlQR = context.Prompt.Destination ?? "";
        model.ShowPayInWalletButton = false;
        model.ShowRecommendedFee = false;
        model.PaymentMethodCurrency = network.DisplayName;

        if (context.Prompt.Details is { } details)
        {
            var payment = context.InvoiceEntity.GetPayments(true)
                .Where(p => p.PaymentMethodId == network.PaymentMethodId)
                .Select(p => handler.ParsePaymentDetails(p.Details))
                .MinBy(p => p.ConfirmationCount);
            if (payment is not null)
            {
                model.RequiredConfirmations = handler.ParsePaymentPromptDetails(details).ConfirmationsRequired;
                model.ReceivedConfirmations = payment.ConfirmationCount;
            }
        }
    }
}

/// <summary>There is no XBT payment URI scheme, so no payment link is offered.</summary>
public class Btcb2PaymentLinkExtension(Btcb2Network network) : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId => network.PaymentMethodId;
    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper) => null;
}
