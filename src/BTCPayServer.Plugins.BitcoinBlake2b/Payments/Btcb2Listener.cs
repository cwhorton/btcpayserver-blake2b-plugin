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
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Payments;

/// <summary>
/// Polls the chain data sources for every invoice that may still receive XBT, records payments
/// once enough sources agree, follows their confirmations, and stops counting a payment only
/// when enough sources positively report it gone (replaced, double-spent or reorganized away).
/// </summary>
public class Btcb2Listener(
    Btcb2Network network,
    Btcb2PaymentHandler handler,
    ChainSources sources,
    InvoiceRepository invoiceRepository,
    PaymentService paymentService,
    EventAggregator eventAggregator,
    ILogger<Btcb2Listener> logger,
    TimeSpan pollInterval) : BackgroundService
{
    static readonly TimeSpan IdleRefresh = TimeSpan.FromMinutes(1);
    /// <summary>Expired and invalid invoices keep being watched this long, so late payments are still recorded.</summary>
    static readonly TimeSpan LatePaymentWindow = TimeSpan.FromDays(3);
    /// <summary>Consecutive polls with enough sources reporting an unconfirmed payment gone before it stops counting.</summary>
    const int PollsBeforeUnconfirmedGone = 2;
    /// <summary>A confirmed payment must be reported gone by every answering source, for this many polls in a row.</summary>
    const int PollsBeforeConfirmedGone = 6;
    /// <summary>Keep following confirmations past settlement, as BTCPay does for Bitcoin.</summary>
    const long MaxTrackedConfirmations = 100;

    readonly ConcurrentDictionary<string, int> _goneStreak = new();
    readonly ConcurrentDictionary<string, (DateTimeOffset Next, long Tip)> _schedule = new();
    DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    /// <summary>
    /// How soon to look at an invoice again. Public explorers are shared and rate limited, so
    /// only invoices a customer is likely waiting on are checked every poll.
    /// </summary>
    public static TimeSpan NextCheckIn(TimeSpan pollInterval, TimeSpan invoiceAge, bool hasPendingPayment, bool tipChanged) =>
        hasPendingPayment ? (tipChanged ? pollInterval : pollInterval * 4)
        : invoiceAge < TimeSpan.FromHours(1) ? pollInterval
        : invoiceAge < TimeSpan.FromHours(24) ? pollInterval * 8
        : pollInterval * 40;

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

    class Watched
    {
        public string InvoiceId { get; set; } = "";
        public string StoreId { get; set; } = "";
        public string Address { get; set; } = "";
    }

    /// <summary>
    /// Invoices that may still receive XBT: pending ones, ones with payments awaiting confirmation,
    /// and recently expired or invalid ones (late payments). BTCPay's own monitored-invoice list
    /// misses the last kind, and pending invoices already paid partly with another method.
    /// </summary>
    async Task<Dictionary<string, (string StoreId, string[] Addresses)>> GetWatchedInvoicesAsync(CancellationToken cancellationToken)
    {
        await using var ctx = invoiceRepository.DbContextFactory.CreateContext();
        var rows = await ctx.Database.GetDbConnection().QueryAsync<Watched>(new CommandDefinition("""
            SELECT i."Id" AS "InvoiceId", i."StoreDataId" AS "StoreId", ai."Address" AS "Address"
            FROM "Invoices" i
            JOIN "AddressInvoices" ai ON ai."InvoiceDataId" = i."Id" AND ai."PaymentMethodId" = @pmi
            WHERE is_pending(i."Status")
               OR (i."Status" IN ('Expired', 'Invalid') AND i."Created" > @since)
               OR EXISTS (SELECT 1 FROM "Payments" p WHERE p."InvoiceDataId" = i."Id" AND p."PaymentMethodId" = @pmi AND is_pending(p."Status"))
            """, new { pmi = network.PaymentMethodId.ToString(), since = DateTimeOffset.UtcNow - LatePaymentWindow }, cancellationToken: cancellationToken));
        return rows.GroupBy(r => r.InvoiceId)
            .ToDictionary(g => g.Key, g => (g.First().StoreId, g.Select(r => r.Address).Distinct().ToArray()));
    }

    public async Task PollAsync(CancellationToken cancellationToken)
    {
        var watched = await GetWatchedInvoicesAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var idleRefreshDue = now - _lastRefresh >= IdleRefresh;
        if (watched.Count == 0 && !idleRefreshDue)
            return;
        foreach (var gone in _schedule.Keys.Except(watched.Keys).ToArray())
            _schedule.TryRemove(gone, out _);

        // Each store's invoices are checked against that store's sources; each set of sources is refreshed once.
        var monitors = new Dictionary<string, ChainMonitor>();
        foreach (var (id, (storeId, _)) in watched)
            monitors[id] = await sources.GetMonitorForStoreAsync(storeId);
        var toRefresh = monitors.Values.ToHashSet();
        if (idleRefreshDue)
        {
            toRefresh.Add(sources.ServerMonitor);
            _lastRefresh = now;
        }
        await Task.WhenAll(toRefresh.Select(m => m.RefreshAsync(cancellationToken)));

        foreach (var (id, (_, addresses)) in watched)
        {
            var monitor = monitors[id];
            if (!monitor.IsAvailable)
                continue;
            if (_schedule.TryGetValue(id, out var due) && due.Next > now && due.Tip == monitor.TipHeight)
                continue;
            try
            {
                var invoice = await invoiceRepository.GetInvoice(id);
                if (invoice is null)
                    continue;
                var pending = await UpdateInvoiceAsync(invoice, addresses, monitor, cancellationToken);
                var tipChanged = !_schedule.TryGetValue(id, out var last) || last.Tip != monitor.TipHeight;
                _schedule[id] = (DateTimeOffset.UtcNow + NextCheckIn(pollInterval, now - invoice.InvoiceTime, pending, tipChanged), monitor.TipHeight);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Error while checking XBT payments of invoice {InvoiceId}", id);
            }
        }

        foreach (var monitor in toRefresh.Where(m => !m.IsAvailable && monitors.ContainsValue(m)))
            logger.LogWarning("Not enough XBT chain data sources available for {Sources} ({Healthy} of {Required} required); its invoices are not being updated",
                monitor.Set.Description, monitor.Healthy.Count, monitor.Set.RequiredAgreement);
        sources.PruneIdleMonitors();
    }

    /// <returns>Whether the invoice has XBT payments still waiting for confirmations.</returns>
    async Task<bool> UpdateInvoiceAsync(InvoiceEntity invoice, string[] trackedAddresses, ChainMonitor monitor, CancellationToken cancellationToken)
    {
        var pmi = network.PaymentMethodId;
        var prompt = invoice.GetPaymentPrompt(pmi);
        if (prompt?.Details is not JToken promptDetails)
            return false;
        var details = handler.ParsePaymentPromptDetails(promptDetails);
        var required = details.ConfirmationsRequired;
        // Every XBT payment of the invoice, whatever its status, so none is mistaken for new.
        var payments = invoice.GetPayments(false).Where(p => p.PaymentMethodId == pmi).ToDictionary(p => p.Id);
        var addresses = trackedAddresses.Append(prompt.Destination).OfType<string>().Distinct().ToArray();

        var updated = new List<PaymentEntity>();
        var added = new List<(AgreedOutput Output, Btcb2PaymentData Data, PaymentStatus Status)>();
        foreach (var address in addresses)
        {
            var view = await monitor.ViewAddressAsync(address, cancellationToken);
            if (!view.Conclusive)
                continue;

            foreach (var output in view.Outputs)
            {
                _goneStreak.TryRemove(Key(invoice, output.PaymentId), out _);
                var data = new Btcb2PaymentData
                {
                    TxId = output.TxId,
                    Vout = output.Vout,
                    KeyIndex = details.KeyIndex,
                    BlockHeight = output.BlockHeight,
                    ConfirmationCount = Math.Min(output.Confirmations, MaxTrackedConfirmations),
                    SeenBy = output.SeenBy
                };
                if (payments.TryGetValue(output.PaymentId, out var existing))
                {
                    var previous = handler.ParsePaymentDetails(existing.Details);
                    // Which sources answered varies between polls: in the same block, confirmations only go up.
                    if (previous.BlockHeight is not null && previous.BlockHeight == data.BlockHeight && data.ConfirmationCount < previous.ConfirmationCount)
                        data.ConfirmationCount = previous.ConfirmationCount;
                    var status = data.ConfirmationCount >= required ? PaymentStatus.Settled : PaymentStatus.Processing;
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
                    added.Add((output, data, data.ConfirmationCount >= required ? PaymentStatus.Settled : PaymentStatus.Processing));
                }
            }

            // Counted payments to this address that are missing from the agreed view. Silence is not
            // evidence: enough sources must answer and positively not report the payment.
            var agreed = view.Outputs.Select(o => o.PaymentId).ToHashSet();
            foreach (var payment in payments.Values.Where(p => p.Destination == address && p.Accounted && !agreed.Contains(p.Id)))
            {
                var key = Key(invoice, payment.Id);
                var confirmed = handler.ParsePaymentDetails(payment.Details).ConfirmationCount > 0;
                var omitted = view.OmittedBy(payment.Id);
                var evidence = omitted >= monitor.Set.RequiredAgreement && (!confirmed || omitted == view.Responded);
                if (!evidence)
                    continue;
                var streak = _goneStreak.AddOrUpdate(key, 1, (_, n) => n + 1);
                if (streak < (confirmed ? PollsBeforeConfirmedGone : PollsBeforeUnconfirmedGone))
                    continue;
                logger.LogWarning("XBT payment {PaymentId} to invoice {InvoiceId} is no longer on chain according to {Count} source(s); it no longer counts",
                    payment.Id, invoice.Id, omitted);
                payment.Status = PaymentStatus.Unaccounted;
                updated.Add(payment);
                _goneStreak.TryRemove(key, out _);
            }
        }

        if (updated.Count > 0)
        {
            await paymentService.UpdatePayments(updated);
            eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
        }
        foreach (var (output, data, status) in added)
            await AddPaymentAsync(invoice, output, data, status);

        return payments.Values.Any(p => p.Status == PaymentStatus.Processing) || added.Any(a => a.Status == PaymentStatus.Processing);
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
        // Subscribers (webhooks, notifications) need the invoice as it is now, with this payment.
        var current = await invoiceRepository.GetInvoice(invoice.Id) ?? invoice;
        eventAggregator.Publish(new InvoiceEvent(current, InvoiceEvent.ReceivedPayment) { Payment = payment });
    }

    static string Key(InvoiceEntity invoice, string paymentId) => $"{invoice.Id}/{paymentId}";
}
