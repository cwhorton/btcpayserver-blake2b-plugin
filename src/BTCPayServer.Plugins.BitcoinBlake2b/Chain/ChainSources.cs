#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Chain;

/// <summary>The chain data sources in use, and how many of them must agree.</summary>
public record ChainSourceSet(IReadOnlyList<IChainSource> Sources, int RequiredAgreement, string Description);

/// <summary>
/// Decides which chain data sources are used. Public explorers are the default; an operator
/// can point BTCPay at other sources with BTCPAY_BTCB2_ESPLORA (comma-separated: explorer API
/// URLs, or Electrum servers as host:port:s, ssl://host:port or tcp://host:port) and
/// BTCPAY_BTCB2_REQUIRED_AGREEMENT.
/// </summary>
public class ChainSources
{
    readonly IHttpClientFactory _httpClientFactory;
    readonly Btcb2Network _network;
    volatile ChainSourceSet _current;

    public ChainSources(IHttpClientFactory httpClientFactory, Btcb2Network network, string? configuredUrls, string? configuredAgreement)
    {
        _httpClientFactory = httpClientFactory;
        _network = network;
        var urls = ParseUrls(configuredUrls);
        int? agreement = int.TryParse(configuredAgreement, out var a) ? a : null;
        _current = urls.Length > 0
            ? Build(urls, agreement ?? 1, "Configured explorers")
            : Build(network.PublicEsploraUrls, network.PublicRequiredAgreement, "Public explorers");
    }

    public ChainSourceSet Current => _current;

    /// <exception cref="FormatException">If a source address is invalid.</exception>
    public ChainSourceSet Build(IEnumerable<string> urls, int requiredAgreement, string description)
    {
        var sources = urls
            .Select(u => u.Trim().TrimEnd('/'))
            .Where(u => u.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(CreateSource)
            .ToArray();
        if (sources.Length == 0)
            throw new ArgumentException("At least one chain data source is required");
        if (requiredAgreement < 1 || requiredAgreement > sources.Length)
            throw new ArgumentException($"Required agreement must be between 1 and {sources.Length}");
        return new ChainSourceSet(sources, requiredAgreement, description);
    }

    public IChainSource CreateSource(string url) => ElectrumEndpoint.LooksLikeElectrum(url)
        ? new ElectrumChainSource(new ElectrumClient(ElectrumEndpoint.Parse(url)), _network)
        : new EsploraChainSource(_httpClientFactory, url);

    public void Set(ChainSourceSet set)
    {
        var previous = _current;
        _current = set;
        foreach (var source in previous.Sources.Except(set.Sources).OfType<IDisposable>())
            source.Dispose();
    }

    public static string[] ParseUrls(string? urls) =>
        (urls ?? "").Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
