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
/// can point BTCPay at other explorers with BTCPAY_BTCB2_ESPLORA (comma-separated URLs) and
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

    public ChainSourceSet Build(IEnumerable<string> esploraUrls, int requiredAgreement, string description)
    {
        var sources = esploraUrls
            .Select(u => u.Trim().TrimEnd('/'))
            .Where(u => u.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(u => (IChainSource)new EsploraChainSource(_httpClientFactory, u))
            .ToArray();
        if (sources.Length == 0)
            throw new ArgumentException("At least one chain data source is required");
        if (requiredAgreement < 1 || requiredAgreement > sources.Length)
            throw new ArgumentException($"Required agreement must be between 1 and {sources.Length}");
        return new ChainSourceSet(sources, requiredAgreement, description);
    }

    public void Set(ChainSourceSet set) => _current = set;

    public static string[] ParseUrls(string? urls) =>
        (urls ?? "").Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
