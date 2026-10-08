#nullable enable
using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.BitcoinBlake2b.Chain;
using BTCPayServer.Plugins.BitcoinBlake2b.Payments;
using BTCPayServer.Plugins.BitcoinBlake2b.Rates;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
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
    ChainSources sources,
    Btcb2RateProvider prices,
    Btcb2StorePricing storePricing,
    RateFetcher rateFetcher,
    DefaultRulesCollection defaultRules,
    StoreRepository storeRepository) : Controller
{
    public const string MenuItemId = "BitcoinBlake2b-Wallet";

    StoreData Store => HttpContext.GetStoreData();

    [HttpGet("")]
    public async Task<IActionResult> Settings()
    {
        var vm = new Btcb2WalletViewModel { DisplayName = network.DisplayName, Chain = network.Chain };
        var storeSources = await sources.GetStoreSettingsAsync(Store.Id);
        vm.SourceMode = storeSources?.Mode ?? ChainSourceMode.Public;
        vm.OwnSources = string.Join("\n", storeSources?.OwnSources ?? []);
        await FillSources(vm);
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
        return View(nameof(Settings), vm);
    }

    [HttpPost("sources")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSources(ChainSourceMode sourceMode, string? ownSources, string command)
    {
        var store = Store;
        if (!sources.ServerSettings.AllowStoreSources || sources.ConfiguredByEnvironment)
            return RedirectToAction(nameof(Settings), new { storeId = store.Id });
        var own = ChainSources.ParseUrls(ownSources);
        try
        {
            var (urls, agreement) = ChainSourceRules.Resolve(sourceMode, own, network);
            foreach (var url in urls)
                ChainSources.ValidateSource(url);
            if (command == "test")
            {
                // Store owners' sources may only reach the public internet.
                using var test = sources.CreateTestMonitor(urls, agreement, ChainSources.Describe(sourceMode), restricted: true);
                await test.RefreshAsync(HttpContext.RequestAborted);
                return await SettingsWith(new Btcb2WalletViewModel { SourceMode = sourceMode, OwnSources = ownSources, TestSources = SourceSetViewModel.From(test) });
            }
            await sources.SaveStoreSettingsAsync(store.Id, sourceMode == ChainSourceMode.Public ? null : new Btcb2StoreSourceSettings { Mode = sourceMode, OwnSources = own });
        }
        catch (FormatException ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel { Severity = StatusMessageModel.StatusSeverity.Error, Message = ex.Message });
            return RedirectToAction(nameof(Settings), new { storeId = store.Id });
        }
        var monitor = await sources.GetMonitorForStoreAsync(store.Id);
        await monitor.RefreshAsync(HttpContext.RequestAborted);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = monitor.IsAvailable ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Warning,
            Message = monitor.IsAvailable
                ? "Chain data sources saved. They apply to new and pending invoices."
                : $"Chain data sources saved, but not enough of them are working: {network.DisplayName} can't be offered at checkout until they are."
        });
        return RedirectToAction(nameof(Settings), new { storeId = store.Id });
    }

    /// <summary>The settings page with the wallet part filled in, keeping the given sources form.</summary>
    async Task<IActionResult> SettingsWith(Btcb2WalletViewModel sourcesForm)
    {
        var page = (ViewResult)await Settings();
        var vm = (Btcb2WalletViewModel)page.Model!;
        vm.SourceMode = sourcesForm.SourceMode;
        vm.OwnSources = sourcesForm.OwnSources;
        vm.TestSources = sourcesForm.TestSources;
        return page;
    }

    async Task FillSources(Btcb2WalletViewModel vm)
    {
        var monitor = await sources.GetMonitorForStoreAsync(Store.Id);
        if (monitor.LastRefresh is null || monitor.LastRefresh < DateTimeOffset.UtcNow.AddMinutes(-1))
            await monitor.RefreshAsync(HttpContext.RequestAborted);
        vm.Sources = SourceSetViewModel.From(monitor);
        vm.CanChooseSources = sources.ServerSettings.AllowStoreSources && !sources.ConfiguredByEnvironment;
        await FillPricing(vm);
    }

    /// <summary>The store's XBT price exactly as its invoices would get it, with how it is made up.</summary>
    async Task FillPricing(Btcb2WalletViewModel vm)
    {
        var store = Store;
        var blob = store.GetStoreBlob();
        vm.Price = await prices.GetSnapshotAsync(HttpContext.RequestAborted);
        vm.AdjustmentPercent = (await storePricing.GetAsync(store.Id)).AdjustmentPercent;
        vm.StoreCurrency = blob.DefaultCurrency;
        vm.StoreSpreadPercent = blob.Spread * 100m;
        var rateSettings = blob.GetRateSettings(false);
        vm.UsesRateScript = rateSettings?.RateScripting is true;
        vm.RateScriptPricesXbt = vm.UsesRateScript && (rateSettings?.RateScript ?? "").Contains(Btcb2.CryptoCode, StringComparison.OrdinalIgnoreCase);
        var pair = new CurrencyPair(Btcb2.CryptoCode, blob.DefaultCurrency);
        var result = await rateFetcher.FetchRates([pair], blob.GetRateRules(defaultRules), new StoreIdRateContext(store.Id), HttpContext.RequestAborted)[pair];
        vm.StoreRate = result.BidAsk?.Bid;
        vm.StoreRateRule = result.EvaluatedRule;
    }

    [HttpPost("pricing")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePricing(decimal adjustmentPercent)
    {
        var store = Store;
        if (adjustmentPercent < Btcb2StorePricingSettings.MinAdjustmentPercent || adjustmentPercent > Btcb2StorePricingSettings.MaxAdjustmentPercent)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Error,
                Message = $"The adjustment must be between {Btcb2StorePricingSettings.MinAdjustmentPercent}% and {Btcb2StorePricingSettings.MaxAdjustmentPercent}%."
            });
            return RedirectToAction(nameof(Settings), new { storeId = store.Id });
        }
        await storePricing.SaveAsync(store.Id, new Btcb2StorePricingSettings { AdjustmentPercent = adjustmentPercent });
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = adjustmentPercent == 0
                ? $"{network.DisplayName} invoices now use the exchange price. This applies to new invoices."
                : $"{network.DisplayName} payers now pay {Math.Abs(adjustmentPercent)}% {(adjustmentPercent > 0 ? "more" : "less")} than the exchange price. This applies to new invoices."
        });
        return RedirectToAction(nameof(Settings), new { storeId = store.Id });
    }

    [HttpPost("preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview(Btcb2WalletViewModel vm)
    {
        vm.DisplayName = network.DisplayName;
        vm.Chain = network.Chain;
        await FillSources(vm);
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
            return await Preview(vm);
        }
        if (!vm.ConfirmAddressesMatch)
            ModelState.AddModelError(nameof(vm.ConfirmAddressesMatch), "Check that these addresses match your wallet's receive addresses.");
        if (!vm.ConfirmDedicatedWallet)
            ModelState.AddModelError(nameof(vm.ConfirmDedicatedWallet), "Confirm that this wallet is used only for XBT.");
        if (!ModelState.IsValid)
            return await Preview(vm);

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

    // Chain data sources
    public SourceSetViewModel? Sources { get; set; }
    public SourceSetViewModel? TestSources { get; set; }
    public bool CanChooseSources { get; set; }
    public ChainSourceMode SourceMode { get; set; }
    public string? OwnSources { get; set; }

    // Pricing
    public PriceSnapshot? Price { get; set; }
    public decimal AdjustmentPercent { get; set; }
    public string StoreCurrency { get; set; } = "USD";
    public decimal StoreSpreadPercent { get; set; }
    public bool UsesRateScript { get; set; }
    public bool RateScriptPricesXbt { get; set; }
    public decimal? StoreRate { get; set; }
    public string? StoreRateRule { get; set; }

    // Setting up a wallet
    public string? WalletKey { get; set; }
    public string[]? PreviewAddresses { get; set; }
    public bool ConfirmAddressesMatch { get; set; }
    public bool ConfirmDedicatedWallet { get; set; }
}
