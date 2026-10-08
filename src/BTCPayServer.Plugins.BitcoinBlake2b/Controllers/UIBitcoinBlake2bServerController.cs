#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.BitcoinBlake2b.Chain;
using BTCPayServer.Plugins.BitcoinBlake2b.Rates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Controllers;

[Area(Plugin.Area)]
[Route("server/bitcoin-blake2b")]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UIBitcoinBlake2bServerController(Btcb2Network network, ChainSources sources, Btcb2RateProvider prices) : Controller
{
    public const string MenuItemId = "BitcoinBlake2b-Server";
    static readonly string[] ReservedNames = ["BTC", "Bitcoin", "XBTC", "SATS"];

    [HttpGet("")]
    public async Task<IActionResult> ServerSettings()
    {
        var settings = sources.ServerSettings;
        var vm = new Btcb2ServerSettingsViewModel
        {
            DisplayName = settings.DisplayName ?? Btcb2.DefaultDisplayName,
            Mode = settings.Mode,
            OwnSources = string.Join("\n", settings.OwnSources),
            AllowStoreSources = settings.AllowStoreSources,
            EnabledExchanges = prices.Exchanges.Select(e => e.Name).Where(settings.Pricing.IsEnabled).ToArray(),
            MaxSpreadPercent = settings.Pricing.MaxSpreadPercent,
            MaxDivergencePercent = settings.Pricing.MaxDivergencePercent
        };
        return View(await Fill(vm));
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ServerSettings(Btcb2ServerSettingsViewModel vm, string command)
    {
        var displayName = vm.DisplayName?.Trim() ?? "";
        if (!Btcb2.IsValidDisplayName(displayName))
            ModelState.AddModelError(nameof(vm.DisplayName), "Use 1 to 20 letters, digits, spaces, dots, dashes or underscores.");
        else if (ReservedNames.Contains(displayName, StringComparer.OrdinalIgnoreCase))
            ModelState.AddModelError(nameof(vm.DisplayName), "This name would make customers think they should pay in Bitcoin (BTC).");

        var exchanges = prices.Exchanges.Select(e => e.Name).ToArray();
        var enabled = (vm.EnabledExchanges ?? []).Intersect(exchanges).ToArray();
        if (enabled.Length == 0)
            ModelState.AddModelError(nameof(vm.EnabledExchanges), "Use at least one exchange, or XBT can't be priced.");
        foreach (var (field, value) in new[] { (nameof(vm.MaxSpreadPercent), vm.MaxSpreadPercent), (nameof(vm.MaxDivergencePercent), vm.MaxDivergencePercent) })
        {
            if (value < Btcb2PricingSettings.MinLimitPercent || value > Btcb2PricingSettings.MaxLimitPercent)
                ModelState.AddModelError(field, $"Must be between {Btcb2PricingSettings.MinLimitPercent}% and {Btcb2PricingSettings.MaxLimitPercent}%.");
        }

        var own = ChainSources.ParseUrls(vm.OwnSources);
        if (!sources.ConfiguredByEnvironment)
        {
            try
            {
                var (urls, agreement) = ChainSourceRules.Resolve(vm.Mode, own, network);
                foreach (var url in urls)
                    ChainSources.ValidateSource(url);
                if (command == "test")
                {
                    using var monitor = sources.CreateTestMonitor(urls, agreement, ChainSources.Describe(vm.Mode), restricted: false);
                    await monitor.RefreshAsync(HttpContext.RequestAborted);
                    vm.Test = SourceSetViewModel.From(monitor);
                }
            }
            catch (FormatException ex)
            {
                ModelState.AddModelError(nameof(vm.OwnSources), ex.Message);
            }
        }
        if (!ModelState.IsValid || command == "test")
            return View(await Fill(vm));

        await sources.SaveServerSettingsAsync(new Btcb2ServerSettings
        {
            DisplayName = displayName,
            Mode = vm.Mode,
            OwnSources = own,
            AllowStoreSources = vm.AllowStoreSources,
            Pricing = new Btcb2PricingSettings
            {
                DisabledExchanges = exchanges.Except(enabled).ToArray(),
                MaxSpreadPercent = vm.MaxSpreadPercent,
                MaxDivergencePercent = vm.MaxDivergencePercent
            }
        });
        await sources.ServerMonitor.RefreshAsync(HttpContext.RequestAborted);
        await prices.GetSnapshotAsync(HttpContext.RequestAborted, forceRefresh: true);
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = sources.ServerMonitor.IsAvailable ? StatusMessageModel.StatusSeverity.Success : StatusMessageModel.StatusSeverity.Warning,
            Message = sources.ServerMonitor.IsAvailable
                ? "Settings saved."
                : $"Settings saved, but not enough chain data sources are working: {network.DisplayName} can't be offered at checkout until they are."
        });
        return RedirectToAction(nameof(ServerSettings));
    }

    async Task<Btcb2ServerSettingsViewModel> Fill(Btcb2ServerSettingsViewModel vm)
    {
        var monitor = sources.ServerMonitor;
        if (monitor.LastRefresh is null || monitor.LastRefresh < DateTimeOffset.UtcNow.AddMinutes(-1))
            await monitor.RefreshAsync(HttpContext.RequestAborted);
        vm.Chain = network.Chain;
        vm.PublicSources = network.PublicEsploraUrls;
        vm.PublicRequiredAgreement = network.PublicRequiredAgreement;
        vm.ConfiguredByEnvironment = sources.ConfiguredByEnvironment;
        vm.Current = SourceSetViewModel.From(monitor);
        vm.Exchanges = prices.Exchanges.Select(e => (e.Name, e.Market)).ToArray();
        vm.Price = await prices.GetSnapshotAsync(HttpContext.RequestAborted);
        return vm;
    }
}

public class Btcb2ServerSettingsViewModel
{
    public string? DisplayName { get; set; }
    public ChainSourceMode Mode { get; set; }
    public string? OwnSources { get; set; }
    public bool AllowStoreSources { get; set; }

    public Btcb2Chain Chain { get; set; }
    public string[] PublicSources { get; set; } = [];
    public int PublicRequiredAgreement { get; set; }
    public bool ConfiguredByEnvironment { get; set; }
    public SourceSetViewModel? Current { get; set; }
    public SourceSetViewModel? Test { get; set; }

    // Pricing
    public string[]? EnabledExchanges { get; set; }
    public decimal MaxSpreadPercent { get; set; } = 5m;
    public decimal MaxDivergencePercent { get; set; } = 5m;
    public (string Name, string Market)[] Exchanges { get; set; } = [];
    public PriceSnapshot? Price { get; set; }
}
