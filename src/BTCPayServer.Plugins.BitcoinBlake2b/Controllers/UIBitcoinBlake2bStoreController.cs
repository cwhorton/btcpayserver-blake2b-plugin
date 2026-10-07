#nullable enable
using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.BitcoinBlake2b.Payments;
using BTCPayServer.Plugins.BitcoinBlake2b.Wallet;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Controllers;

[Area(Plugin.Area)]
[Route("stores/{storeId}/bitcoin-blake2b")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UIBitcoinBlake2bStoreController(
    Btcb2Network network,
    Btcb2PaymentHandler handler,
    AddressAllocator allocator,
    StoreRepository storeRepository) : Controller
{
    public const string MenuItemId = "BitcoinBlake2b-Wallet";

    StoreData Store => HttpContext.GetStoreData();

    [HttpGet("")]
    public async Task<IActionResult> Settings()
    {
        var vm = new Btcb2WalletViewModel { DisplayName = network.DisplayName, Chain = network.Chain };
        var config = GetConfig();
        if (config is not null)
        {
            vm.Configured = true;
            vm.ChainMismatch = config.Chain != network.Chain;
            vm.AccountOriginal = config.AccountOriginal ?? config.AccountDerivation;
            vm.Enabled = !Store.GetStoreBlob().IsExcluded(network.PaymentMethodId);
            vm.ConfirmationsRequired = config.ConfirmationsRequired;
            vm.SpeedPolicyConfirmations = ConfirmationPolicy.Required(Store.SpeedPolicy, null);
            if (!vm.ChainMismatch)
            {
                vm.Addresses = WalletKey.PreviewAddresses(WalletKey.ParseDerivation(config.AccountDerivation, network), network);
                vm.NextIndex = await allocator.GetNextIndexAsync(Store.Id, config.AccountDerivation);
            }
        }
        return View(vm);
    }

    [HttpPost("preview")]
    [ValidateAntiForgeryToken]
    public IActionResult Preview(Btcb2WalletViewModel vm)
    {
        vm.DisplayName = network.DisplayName;
        vm.Chain = network.Chain;
        try
        {
            var settings = WalletKey.Parse(vm.WalletKey ?? "", network);
            vm.PreviewAddresses = WalletKey.PreviewAddresses(settings.AccountDerivation, network);
        }
        catch (FormatException ex)
        {
            ModelState.AddModelError(nameof(vm.WalletKey), ex.Message);
        }
        return View(nameof(Settings), vm);
    }

    [HttpPost("save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Btcb2WalletViewModel vm)
    {
        DerivationSchemeSettings settings;
        try
        {
            settings = WalletKey.Parse(vm.WalletKey ?? "", network);
        }
        catch (FormatException ex)
        {
            ModelState.AddModelError(nameof(vm.WalletKey), ex.Message);
            return Preview(vm);
        }
        if (!vm.ConfirmAddressesMatch)
            ModelState.AddModelError(nameof(vm.ConfirmAddressesMatch), "Check that these addresses match your wallet's receive addresses.");
        if (!vm.ConfirmDedicatedWallet)
            ModelState.AddModelError(nameof(vm.ConfirmDedicatedWallet), "Confirm that this wallet is used only for XBT.");
        if (!ModelState.IsValid)
            return Preview(vm);

        var store = Store;
        store.SetPaymentMethodConfig(handler, new Btcb2PaymentMethodConfig
        {
            AccountDerivation = settings.AccountDerivation.ToString(),
            AccountOriginal = vm.WalletKey!.Trim(),
            Chain = network.Chain,
            ConfirmationsRequired = GetConfig()?.ConfirmationsRequired
        });
        var blob = store.GetStoreBlob();
        blob.SetExcluded(network.PaymentMethodId, false);
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = $"{network.DisplayName} wallet saved. New invoices can now be paid in {network.DisplayName}."
        });
        return RedirectToAction(nameof(Settings), new { storeId = store.Id });
    }

    [HttpPost("settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSettings(bool enabled, int? confirmationsRequired)
    {
        var store = Store;
        var config = GetConfig();
        if (config is null)
            return RedirectToAction(nameof(Settings), new { storeId = store.Id });
        if (confirmationsRequired is < 0 or > 100)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Error,
                Message = "Confirmations must be between 0 and 100."
            });
            return RedirectToAction(nameof(Settings), new { storeId = store.Id });
        }
        config.ConfirmationsRequired = confirmationsRequired;
        store.SetPaymentMethodConfig(handler, config);
        var blob = store.GetStoreBlob();
        blob.SetExcluded(network.PaymentMethodId, !enabled);
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = "Settings saved. They apply to new invoices."
        });
        return RedirectToAction(nameof(Settings), new { storeId = store.Id });
    }

    [HttpPost("remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove()
    {
        var store = Store;
        store.SetPaymentMethodConfig(handler, null);
        await storeRepository.UpdateStore(store);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = $"{network.DisplayName} wallet removed. Existing invoices are still monitored."
        });
        return RedirectToAction(nameof(Settings), new { storeId = store.Id });
    }

    Btcb2PaymentMethodConfig? GetConfig() =>
        Store.GetPaymentMethodConfig(network.PaymentMethodId) is { } json ? handler.ParsePaymentMethodConfig(json) : null;
}

public class Btcb2WalletViewModel
{
    public string DisplayName { get; set; } = Btcb2.DefaultDisplayName;
    public Btcb2Chain Chain { get; set; }

    // Current wallet
    public bool Configured { get; set; }
    public bool ChainMismatch { get; set; }
    public string? AccountOriginal { get; set; }
    public string[] Addresses { get; set; } = [];
    public int NextIndex { get; set; }
    public bool Enabled { get; set; }
    public int? ConfirmationsRequired { get; set; }
    public int SpeedPolicyConfirmations { get; set; }

    // Setting up a wallet
    public string? WalletKey { get; set; }
    public string[]? PreviewAddresses { get; set; }
    public bool ConfirmAddressesMatch { get; set; }
    public bool ConfirmDedicatedWallet { get; set; }
}
