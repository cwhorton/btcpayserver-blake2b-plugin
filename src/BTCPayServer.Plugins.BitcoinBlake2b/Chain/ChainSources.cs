#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.BitcoinBlake2b.Wallet;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Chain;

/// <summary>A set of chain data sources, and how many of them must agree.</summary>
public record ChainSourceSet(IReadOnlyList<IChainSource> Sources, int RequiredAgreement, string Description);

/// <summary>
/// Decides which chain data sources each store uses and keeps one <see cref="ChainMonitor"/>
/// per distinct set of sources. The server admin chooses the server's sources; store owners
/// may choose their own if the admin allows it. BTCPAY_BTCB2_ESPLORA (comma-separated
/// explorer API URLs or Electrum servers) and BTCPAY_BTCB2_REQUIRED_AGREEMENT override the
/// admin's choice for the whole server.
/// </summary>
public class ChainSources : IAddressUsageCheck
{
    readonly IHttpClientFactory _httpClientFactory;
    readonly Btcb2Network _network;
    readonly ISettingsRepository _settingsRepository;
    readonly StoreRepository _storeRepository;
    readonly ILoggerFactory _loggerFactory;
    readonly (string[] Urls, int RequiredAgreement)? _environment;
    readonly ConcurrentDictionary<string, ChainMonitor> _monitors = new();
    readonly ConcurrentDictionary<string, Btcb2StoreSourceSettings?> _storeSettings = new();
    volatile ChainMonitor _serverMonitor;

    public ChainSources(IHttpClientFactory httpClientFactory, Btcb2Network network, ISettingsRepository settingsRepository,
        StoreRepository storeRepository, ILoggerFactory loggerFactory, string? environmentUrls, string? environmentAgreement)
    {
        _httpClientFactory = httpClientFactory;
        _network = network;
        _settingsRepository = settingsRepository;
        _storeRepository = storeRepository;
        _loggerFactory = loggerFactory;
        var urls = ParseUrls(environmentUrls);
        if (urls.Length > 0)
            _environment = (urls, int.TryParse(environmentAgreement, out var a) ? a : 1);
        _serverMonitor = _environment is { } env
            ? GetMonitor(env.Urls, env.RequiredAgreement, "Set by BTCPAY_BTCB2_ESPLORA")
            : GetMonitor(network.PublicEsploraUrls, network.PublicRequiredAgreement, "Public explorers");
    }

    public Btcb2ServerSettings ServerSettings { get; private set; } = new();
    /// <summary>True when an environment variable fixes the server's sources; the admin page is then read-only.</summary>
    public bool ConfiguredByEnvironment => _environment is not null;
    public ChainMonitor ServerMonitor => _serverMonitor;

    Task? _loading;
    readonly object _loadingLock = new();

    /// <summary>Loads the admin's saved settings once, however many callers ask.</summary>
    public Task EnsureLoadedAsync()
    {
        lock (_loadingLock)
            return _loading ??= LoadAsync();
    }

    async Task LoadAsync()
    {
        ApplyServerSettings(await _settingsRepository.GetSettingAsync<Btcb2ServerSettings>(Btcb2ServerSettings.SettingName) ?? new());
    }

    /// <exception cref="FormatException">If the sources are invalid; nothing is saved.</exception>
    public async Task SaveServerSettingsAsync(Btcb2ServerSettings settings)
    {
        if (!ConfiguredByEnvironment)
            Validate(settings.Mode, settings.OwnSources);
        await _settingsRepository.UpdateSetting(settings, Btcb2ServerSettings.SettingName);
        ApplyServerSettings(settings);
    }

    void ApplyServerSettings(Btcb2ServerSettings settings)
    {
        ServerSettings = settings;
        _network.DisplayName = string.IsNullOrWhiteSpace(settings.DisplayName) ? Btcb2.DefaultDisplayName : settings.DisplayName.Trim();
        _network.CurrencyData.Symbol = _network.DisplayName;
        if (ConfiguredByEnvironment)
            return;
        try
        {
            var (urls, agreement) = ChainSourceRules.Resolve(settings.Mode, settings.OwnSources, _network);
            _serverMonitor = GetMonitor(urls, agreement, Describe(settings.Mode));
        }
        catch (FormatException)
        {
            _serverMonitor = GetMonitor(_network.PublicEsploraUrls, _network.PublicRequiredAgreement, Describe(ChainSourceMode.Public));
        }
    }

    public async Task<Btcb2StoreSourceSettings?> GetStoreSettingsAsync(string storeId)
    {
        if (_storeSettings.TryGetValue(storeId, out var cached))
            return cached;
        var settings = await _storeRepository.GetSettingAsync<Btcb2StoreSourceSettings>(storeId, Btcb2StoreSourceSettings.SettingName);
        _storeSettings[storeId] = settings;
        return settings;
    }

    /// <exception cref="FormatException">If the sources are invalid; nothing is saved.</exception>
    public async Task SaveStoreSettingsAsync(string storeId, Btcb2StoreSourceSettings? settings)
    {
        if (settings is not null)
            Validate(settings.Mode, settings.OwnSources);
        await _storeRepository.UpdateSetting(storeId, Btcb2StoreSourceSettings.SettingName, settings);
        _storeSettings[storeId] = settings;
    }

    /// <summary>The monitor for the sources a store's invoices use.</summary>
    public async Task<ChainMonitor> GetMonitorForStoreAsync(string storeId)
    {
        if (!ServerSettings.AllowStoreSources || ConfiguredByEnvironment)
            return ServerMonitor;
        var store = await GetStoreSettingsAsync(storeId);
        if (store is null || store.Mode == ChainSourceMode.Public)
            return ServerMonitor;
        try
        {
            var (urls, agreement) = ChainSourceRules.Resolve(store.Mode, store.OwnSources, _network);
            return GetMonitor(urls, agreement, $"Store: {Describe(store.Mode)}");
        }
        catch (FormatException)
        {
            return ServerMonitor;
        }
    }

    /// <summary>A monitor for these sources, shared with every store that uses the same ones.</summary>
    public ChainMonitor GetMonitor(IEnumerable<string> urls, int requiredAgreement, string description)
    {
        var normalized = urls.Select(u => u.Trim().TrimEnd('/')).Where(u => u.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Length == 0)
            throw new FormatException("At least one chain data source is required");
        if (requiredAgreement < 1 || requiredAgreement > normalized.Length)
            throw new FormatException($"Required agreement must be between 1 and {normalized.Length}");
        var key = $"{requiredAgreement}|{string.Join("|", normalized)}";
        return _monitors.GetOrAdd(key, _ => new ChainMonitor(
            new ChainSourceSet(normalized.Select(CreateSource).ToArray(), requiredAgreement, description),
            _network, _loggerFactory.CreateLogger<ChainMonitor>()));
    }

    /// <exception cref="FormatException">With a message suitable for the user.</exception>
    void Validate(ChainSourceMode mode, string[] ownSources)
    {
        foreach (var url in ChainSourceRules.Resolve(mode, ownSources, _network).Sources)
            ValidateSource(url);
    }

    /// <exception cref="FormatException">If the address is not a valid explorer URL or Electrum server.</exception>
    public static void ValidateSource(string url)
    {
        if (ElectrumEndpoint.LooksLikeElectrum(url))
            ElectrumEndpoint.Parse(url);
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FormatException($"'{url}' is not an explorer API URL");
    }

    IChainSource CreateSource(string url)
    {
        ValidateSource(url);
        return ElectrumEndpoint.LooksLikeElectrum(url)
            ? new ElectrumChainSource(new ElectrumClient(ElectrumEndpoint.Parse(url)), _network)
            : new EsploraChainSource(_httpClientFactory, url);
    }

    public async Task<bool> IsUsedAsync(string storeId, string address, CancellationToken cancellationToken) =>
        await (await GetMonitorForStoreAsync(storeId)).IsUsedAsync(address, cancellationToken);

    public static string Describe(ChainSourceMode mode) => mode switch
    {
        ChainSourceMode.Public => "Public explorers",
        ChainSourceMode.Own => "Own sources",
        ChainSourceMode.OwnWithPublicCheck => "Own sources, cross-checked with public explorers",
        _ => mode.ToString()
    };

    public static string[] ParseUrls(string? urls) =>
        (urls ?? "").Split([',', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
