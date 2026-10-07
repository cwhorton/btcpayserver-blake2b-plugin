#nullable enable
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using NBitcoin;
using NBXplorer.DerivationStrategy;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Wallet;

/// <summary>Reports whether an address already has transactions on chain, as seen by the store's sources.</summary>
public interface IAddressUsageCheck
{
    Task<bool> IsUsedAsync(string storeId, string address, CancellationToken cancellationToken);
}

/// <summary>
/// Hands out a fresh receive address per invoice. The next index is stored per store, and
/// any address already tied to an invoice, or already used on chain, is skipped. This keeps
/// allocation safe if the counter is lost or a wallet key is reused.
/// </summary>
public class AddressAllocator(
    StoreRepository storeRepository,
    InvoiceRepository invoiceRepository,
    IAddressUsageCheck usageCheck,
    Btcb2Network network)
{
    const string SettingName = "BitcoinBlake2b.NextAddressIndex";
    const int MaxSkipped = 200;
    static readonly System.TimeSpan MaxSearchTime = System.TimeSpan.FromSeconds(45);

    public class IndexState
    {
        public string AccountDerivation { get; set; } = "";
        public int NextIndex { get; set; }
    }

    // A single BTCPay instance owns the database, so in-process locks are enough: one per store,
    // so one store's slow search can't hold up every other store's invoices.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<(BitcoinAddress Address, int Index)> ReserveAsync(string storeId, DerivationStrategyBase strategy, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MaxSearchTime);
        cancellationToken = timeout.Token;
        var storeLock = _locks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));
        await storeLock.WaitAsync(cancellationToken);
        try
        {
            var key = strategy.ToString();
            var state = await storeRepository.GetSettingAsync<IndexState>(storeId, SettingName);
            var index = state?.AccountDerivation == key ? state.NextIndex : 0;
            for (var skipped = 0; skipped < MaxSkipped; skipped++, index++)
            {
                var address = WalletKey.DeriveReceiveAddress(strategy, index, network);
                if (await invoiceRepository.GetInvoiceFromAddress(network.PaymentMethodId, address.ToString()) is not null)
                    continue;
                if (await usageCheck.IsUsedAsync(storeId, address.ToString(), cancellationToken))
                    continue;
                await storeRepository.UpdateSetting(storeId, SettingName, new IndexState { AccountDerivation = key, NextIndex = index + 1 });
                return (address, index);
            }
            throw new PaymentMethodUnavailableException($"No unused address found in the next {MaxSkipped} addresses of this wallet");
        }
        catch (System.OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new PaymentMethodUnavailableException("Finding an unused address took too long");
        }
        finally
        {
            storeLock.Release();
        }
    }

    public async Task<int> GetNextIndexAsync(string storeId, string accountDerivation)
    {
        var state = await storeRepository.GetSettingAsync<IndexState>(storeId, SettingName);
        return state?.AccountDerivation == accountDerivation ? state.NextIndex : 0;
    }
}
