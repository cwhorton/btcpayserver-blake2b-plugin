#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.BitcoinBlake2b.Wallet;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Chain;

/// <summary>A set of chain data sources, and how many of them must agree.</summary>
public record ChainSourceSet(IReadOnlyList<IChainSource> Sources, int RequiredAgreement, string Description);

/// <summary>
/// Decides which chain data sources each store uses and keeps one <see cref="ChainMonitor"/>
/// per distinct set of sources. The server admin chooses the server's sources; store owners
/// may choose their own if the admin allows it, limited to public internet hosts.
/// BTCPAY_BTCB2_ESPLORA (comma-separated explorer API URLs or Electrum servers) and
/// BTCPAY_BTCB2_REQUIRED_AGREEMENT override the admin's choice for the whole server.
/// </summary>
public class ChainSources : IAddressUsageCheck
{
    static readonly TimeSpan IdleMonitorLifetime = TimeSpan.FromHours(1);

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
        _environment = ParseEnvironment(environmentUrls, environmentAgreement);
        _serverMonitor = _environment is { } env
            ? GetMonitor(env.Urls, env.RequiredAgreement, "Set by BTCPAY_BTCB2_ESPLORA")
            : GetMonitor(network.PublicEsploraUrls, network.PublicRequiredAgreement, "Public explorers");
    }

    /// <summary>
    /// Several sources default to two having to agree: one source alone is trusted only when
    /// it's the only one, or when the operator says so.
    /// </summary>
    /// <exception cref="ConfigException">For invalid sources or agreement.</exception>
    public static (string[] Urls, int RequiredAgreement)? ParseEnvironment(string? urls, string? agreement)
    {
        var list = ParseUrls(urls);
        if (list.Length == 0)
            return null;
        try
        {
            foreach (var url in list)
                ValidateSource(url);
        }
        catch (FormatException ex)
        {
            throw new ConfigException($"BTCPAY_BTCB2_ESPLORA: {ex.Message}");
        }
        var count = list.Select(Canonical).Distinct().Count();
        int required;
        if (string.IsNullOrWhiteSpace(agreement))
            required = Math.Min(2, count);
        else if (!int.TryParse(agreement, out required) || required < 1 || required > count)
            throw new ConfigException($"BTCPAY_BTCB2_REQUIRED_AGREEMENT must be a number from 1 to {count}, not '{agreement}'");
        return (list, required);
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
        var name = settings.DisplayName?.Trim();
        _network.DisplayName = Btcb2.IsValidDisplayName(name) ? name! : Btcb2.DefaultDisplayName;
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
            return Use(ServerMonitor);
        var store = await GetStoreSettingsAsync(storeId);
        if (store is null || store.Mode == ChainSourceMode.Public)
            return Use(ServerMonitor);
        try
        {
            var (urls, agreement) = ChainSourceRules.Resolve(store.Mode, store.OwnSources, _network);
            return GetMonitor(urls, agreement, $"Store: {Describe(store.Mode)}", restricted: true);
        }
        catch (FormatException)
        {
            return Use(ServerMonitor);
        }
    }

    static ChainMonitor Use(ChainMonitor monitor)
    {
        monitor.LastUsed = DateTimeOffset.UtcNow;
        return monitor;
    }

    /// <summary>
    /// A monitor for these sources, shared with every store that uses the same ones.
    /// <paramref name="restricted"/> sources may only connect to public internet addresses.
    /// </summary>
    public ChainMonitor GetMonitor(IEnumerable<string> urls, int requiredAgreement, string description, bool restricted = false)
    {
        var normalized = Normalize(urls, requiredAgreement);
        var key = $"{(restricted ? "restricted" : "server")}|{requiredAgreement}|{string.Join("|", normalized.Select(Canonical))}";
        return Use(_monitors.GetOrAdd(key, _ => CreateMonitor(normalized, requiredAgreement, description, restricted)));
    }

    /// <summary>A throwaway monitor for testing sources before they are saved. Dispose it after use.</summary>
    public ChainMonitor CreateTestMonitor(IEnumerable<string> urls, int requiredAgreement, string description, bool restricted) =>
        CreateMonitor(Normalize(urls, requiredAgreement), requiredAgreement, description, restricted);

    ChainMonitor CreateMonitor(string[] urls, int requiredAgreement, string description, bool restricted) =>
        new(new ChainSourceSet(urls.Select(u => CreateSource(u, restricted)).ToArray(), requiredAgreement, description),
            _network, _loggerFactory.CreateLogger<ChainMonitor>());

    static string[] Normalize(IEnumerable<string> urls, int requiredAgreement)
    {
        var normalized = urls.Select(u => u.Trim().TrimEnd('/')).Where(u => u.Length > 0)
            .DistinctBy(Canonical).OrderBy(Canonical, StringComparer.Ordinal).ToArray();
        if (normalized.Length == 0)
            throw new FormatException("At least one chain data source is required");
        if (requiredAgreement < 1 || requiredAgreement > normalized.Length)
            throw new FormatException($"Required agreement must be between 1 and {normalized.Length}");
        return normalized;
    }

    /// <summary>Disposes monitors no store has used for an hour, closing their connections.</summary>
    public void PruneIdleMonitors()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleMonitorLifetime;
        foreach (var (key, monitor) in _monitors.ToArray())
        {
            if (monitor != _serverMonitor && monitor.LastUsed < cutoff && _monitors.TryRemove(key, out _))
                monitor.Dispose();
        }
    }

    /// <exception cref="FormatException">With a message suitable for the user.</exception>
    void Validate(ChainSourceMode mode, string[] ownSources)
    {
        foreach (var url in ChainSourceRules.Resolve(mode, ownSources, _network).Sources)
            ValidateSource(url);
    }

    /// <summary>
    /// An explorer URL (http or https, no query, fragment or credentials) or an Electrum server.
    /// </summary>
    /// <exception cref="FormatException">If the address is not a valid explorer URL or Electrum server.</exception>
    public static void ValidateSource(string url)
    {
        if (ElectrumEndpoint.LooksLikeElectrum(url))
        {
            ElectrumEndpoint.Parse(url);
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FormatException($"'{url}' is not an explorer API URL");
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            throw new FormatException($"'{url}': an explorer API URL can't have a query, fragment or credentials");
    }

    /// <summary>One identity per server, however its address is written.</summary>
    public static string Canonical(string url)
    {
        if (ElectrumEndpoint.LooksLikeElectrum(url))
        {
            var e = ElectrumEndpoint.Parse(url);
            return $"{e}{(e.PinnedCertificateSha256 is { } pin ? "#" + Convert.ToHexString(pin) : "")}".ToLowerInvariant();
        }
        var uri = new Uri(url.Trim().TrimEnd('/'));
        return $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}".ToLowerInvariant();
    }

    IChainSource CreateSource(string url, bool restricted)
    {
        ValidateSource(url);
        return ElectrumEndpoint.LooksLikeElectrum(url)
            ? new ElectrumChainSource(new ElectrumClient(ElectrumEndpoint.Parse(url), restricted), _network)
            : new EsploraChainSource(_httpClientFactory, url, restricted);
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
