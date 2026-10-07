#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Plugins.BitcoinBlake2b.Chain;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Payments;

/// <summary>
/// Polls the chain data sources for every invoice awaiting XBT, records payments once enough
/// sources agree, follows their confirmations, and stops counting a payment that the sources
/// no longer see (replaced, double-spent or reorganized away).
/// </summary>
public class Btcb2Listener(
    Btcb2Network network,
    Btcb2PaymentHandler handler,
    ChainMonitor monitor,
    InvoiceRepository invoiceRepository,
    PaymentService paymentService,
    EventAggregator eventAggregator,
    ILogger<Btcb2Listener> logger,
    TimeSpan pollInterval) : BackgroundService
{
    static readonly TimeSpan IdleRefresh = TimeSpan.FromMinutes(1);
    /// <summary>Consecutive conclusive polls without a payment before it stops counting, so one glitch can't flip it.</summary>
    const int MissingPollsBeforeUnaccounted = 2;
    /// <summary>Keep following confirmations past settlement, as BTCPay does for Bitcoin.</summary>
    const long MaxTrackedConfirmations = 100;

    readonly ConcurrentDictionary<string, int> _missingStreak = new();
    DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Error while checking XBT payments");
            }
            try
            {
                await Task.Delay(pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task PollAsync(CancellationToken cancellationToken)
    {
        var invoices = await invoiceRepository.GetMonitoredInvoices(network.PaymentMethodId, cancellationToken);
        if (invoices.Length == 0 && DateTimeOffset.UtcNow - _lastRefresh < IdleRefresh)
            return;
        await monitor.RefreshAsync(cancellationToken);
        _lastRefresh = DateTimeOffset.UtcNow;
        if (!monitor.IsAvailable)
        {
            if (invoices.Length > 0)
                logger.LogWarning("Not enough XBT chain data sources available ({Healthy} of {Required} required); payments are not being updated",
                    monitor.Healthy.Count, monitor.Set.RequiredAgreement);
            return;
        }
        foreach (var invoice in invoices)
        {
            try
            {
                await UpdateInvoiceAsync(invoice, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Error while checking XBT payments of invoice {InvoiceId}", invoice.Id);
            }
        }
    }

    async Task UpdateInvoiceAsync(InvoiceEntity invoice, CancellationToken cancellationToken)
    {
        var pmi = network.PaymentMethodId;
        var prompt = invoice.GetPaymentPrompt(pmi);
        if (prompt?.Details is not JToken promptDetails)
            return;
        var details = handler.ParsePaymentPromptDetails(promptDetails);
        var required = details.ConfirmationsRequired;
        var payments = invoice.GetPayments(false).Where(p => p.PaymentMethodId == pmi).ToDictionary(p => p.Id);
        var addresses = invoice.Addresses
            .Where(a => a.PaymentMethodId == pmi).Select(a => a.Address)
            .Append(prompt.Destination)
            .OfType<string>().Distinct().ToArray();

        var updated = new List<PaymentEntity>();
        foreach (var address in addresses)
        {
            var view = await monitor.ViewAddressAsync(address, cancellationToken);
            if (!view.Conclusive)
                continue;

            foreach (var output in view.Outputs)
            {
                _missingStreak.TryRemove(Key(invoice, output.PaymentId), out _);
                var data = new Btcb2PaymentData
                {
                    TxId = output.TxId,
                    Vout = output.Vout,
                    KeyIndex = details.KeyIndex,
                    BlockHeight = output.BlockHeight,
                    ConfirmationCount = Math.Min(output.Confirmations, MaxTrackedConfirmations),
                    SeenBy = output.SeenBy
                };
                var status = data.ConfirmationCount >= required ? PaymentStatus.Settled : PaymentStatus.Processing;
                if (payments.TryGetValue(output.PaymentId, out var existing))
                {
                    var previous = handler.ParsePaymentDetails(existing.Details);
                    if (existing.Status == status && previous.ConfirmationCount == data.ConfirmationCount && previous.BlockHeight == data.BlockHeight)
                        continue;
                    if (existing.Status == PaymentStatus.Unaccounted)
                        logger.LogInformation("XBT payment {PaymentId} to invoice {InvoiceId} is visible again", existing.Id, invoice.Id);
                    existing.Status = status;
                    existing.SetDetails(handler, data);
                    updated.Add(existing);
                }
                else
                {
                    await AddPaymentAsync(invoice, output, data, status);
                }
            }

            // Payments to this address that the sources no longer report.
            var seen = view.Outputs.Select(o => o.PaymentId).ToHashSet();
            foreach (var payment in payments.Values.Where(p => p.Destination == address && p.Accounted && !seen.Contains(p.Id)))
            {
                var streak = _missingStreak.AddOrUpdate(Key(invoice, payment.Id), 1, (_, n) => n + 1);
                if (streak < MissingPollsBeforeUnaccounted)
                    continue;
                logger.LogWarning("XBT payment {PaymentId} to invoice {InvoiceId} is no longer seen on chain; it no longer counts", payment.Id, invoice.Id);
                payment.Status = PaymentStatus.Unaccounted;
                updated.Add(payment);
                _missingStreak.TryRemove(Key(invoice, payment.Id), out _);
            }
        }

        if (updated.Count > 0)
        {
            await paymentService.UpdatePayments(updated);
            eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
        }
    }

    async Task AddPaymentAsync(InvoiceEntity invoice, AgreedOutput output, Btcb2PaymentData data, PaymentStatus status)
    {
        var paymentData = new PaymentData
        {
            Id = output.PaymentId,
            Created = DateTimeOffset.UtcNow,
            Status = status,
            Amount = Money.Satoshis(output.ValueSats).ToDecimal(MoneyUnit.BTC),
            Currency = network.CryptoCode
        }.Set(invoice, handler, data);
        var payment = await paymentService.AddPayment(paymentData, [output.TxId]);
        if (payment is null)
            return;
        logger.LogInformation("Invoice {InvoiceId} received {Amount} {Currency} in {PaymentId} (seen by {Sources})",
            invoice.Id, payment.Value, network.DisplayName, payment.Id, string.Join(", ", output.SeenBy));
        eventAggregator.Publish(new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
    }

    static string Key(InvoiceEntity invoice, string paymentId) => $"{invoice.Id}/{paymentId}";
}
