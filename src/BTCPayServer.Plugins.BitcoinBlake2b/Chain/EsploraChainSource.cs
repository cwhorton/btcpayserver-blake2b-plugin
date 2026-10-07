#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Chain;

/// <summary>
/// A block explorer with an Esplora-style REST API, as served by mempool and Esplora
/// (for example https://mempool.guide/api).
/// </summary>
public class EsploraChainSource : IChainSource
{
    public const string HttpClientName = "BitcoinBlake2b.Chain";
    const int MaxPages = 20;

    readonly IHttpClientFactory _httpClientFactory;
    readonly string _baseUrl;

    public EsploraChainSource(IHttpClientFactory httpClientFactory, string baseUrl)
    {
        _httpClientFactory = httpClientFactory;
        _baseUrl = baseUrl.TrimEnd('/');
        var uri = new Uri(_baseUrl);
        Name = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }

    public string Name { get; }
    public string Url => _baseUrl;

    public async Task<long> GetTipHeightAsync(CancellationToken cancellationToken)
        => long.Parse((await GetStringAsync("/blocks/tip/height", cancellationToken))!.Trim(), CultureInfo.InvariantCulture);

    public async Task<string?> GetBlockHashAsync(long height, CancellationToken cancellationToken)
        => (await GetStringAsync($"/block-height/{height}", cancellationToken, allowNotFound: true))?.Trim();

    public async Task<int> GetTransactionCountAsync(string address, CancellationToken cancellationToken)
        => ParseTransactionCount(JObject.Parse((await GetStringAsync($"/address/{address}", cancellationToken))!));

    public async Task<IReadOnlyList<ChainOutput>> GetOutputsAsync(string address, CancellationToken cancellationToken)
    {
        var expected = await GetTransactionCountAsync(address, cancellationToken);
        if (expected == 0)
            return [];
        var txs = new List<JObject>();
        var seen = new HashSet<string>();
        AddPage(txs, seen, await GetStringAsync($"/address/{address}/txs", cancellationToken));
        // Explorers page their history differently: mempool uses ?after_txid=, Esplora uses
        // /txs/chain/{txid}. An explorer that ignores the one it doesn't know adds nothing new.
        for (var page = 0; txs.Count < expected && page < MaxPages; page++)
        {
            var last = txs.LastOrDefault(t => t["status"]?.Value<bool>("confirmed") is true)?.Value<string>("txid");
            if (last is null)
                break;
            if (AddPage(txs, seen, await GetStringAsync($"/address/{address}/txs?after_txid={last}", cancellationToken, allowNotFound: true)) == 0 &&
                AddPage(txs, seen, await GetStringAsync($"/address/{address}/txs/chain/{last}", cancellationToken, allowNotFound: true)) == 0)
                break;
        }
        if (txs.Count < expected)
            throw new ChainSourceException($"{Name} returned {txs.Count} of {expected} transactions for {address}");
        return ParseOutputs(txs, address);
    }

    static int AddPage(List<JObject> txs, HashSet<string> seen, string? page)
    {
        if (page is null)
            return 0;
        var added = 0;
        foreach (var tx in JArray.Parse(page).OfType<JObject>())
        {
            if (tx.Value<string>("txid") is { } txid && seen.Add(txid))
            {
                txs.Add(tx);
                added++;
            }
        }
        return added;
    }

    public static int ParseTransactionCount(JObject address) =>
        (address["chain_stats"]?.Value<int>("tx_count") ?? throw new ChainSourceException("Missing chain_stats"))
        + (address["mempool_stats"]?.Value<int>("tx_count") ?? 0);

    public static IReadOnlyList<ChainOutput> ParseOutputs(IEnumerable<JObject> txs, string address)
    {
        var outputs = new List<ChainOutput>();
        foreach (var tx in txs)
        {
            var txid = tx.Value<string>("txid") ?? throw new ChainSourceException("Transaction without txid");
            var status = tx["status"] as JObject;
            long? height = status?.Value<bool>("confirmed") is true ? status.Value<long?>("block_height") : null;
            if (tx["vout"] is not JArray vouts)
                continue;
            for (var i = 0; i < vouts.Count; i++)
            {
                if (vouts[i] is JObject o && o.Value<string>("scriptpubkey_address") == address)
                    outputs.Add(new ChainOutput(txid, i, o.Value<long>("value"), height));
            }
        }
        return outputs;
    }

    async Task<string?> GetStringAsync(string path, CancellationToken cancellationToken, bool allowNotFound = false)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(_baseUrl + path, cancellationToken);
        if (allowNotFound && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new ChainSourceException($"{Name} answered {(int)response.StatusCode} for {path}");
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}

public class ChainSourceException(string message) : Exception(message);
