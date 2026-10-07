#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Chain;

/// <summary>An Electrum server (electrs, Fulcrum and their BLAKE2b forks).</summary>
public class ElectrumChainSource(ElectrumClient client, Btcb2Network network) : IChainSource, IDisposable
{
    const int MaxCachedTransactions = 2000;
    // Transactions never change once known, so their outputs are cached by txid.
    readonly ConcurrentDictionary<string, Transaction> _transactions = new();

    public string Name => client.Endpoint.Name;
    public string Url => client.Endpoint.ToString();

    public async Task<long> GetTipHeightAsync(CancellationToken cancellationToken)
    {
        var tip = await client.CallAsync("blockchain.headers.subscribe", [], cancellationToken);
        return tip.Value<long?>("height") ?? throw new ElectrumException($"{Name}: no tip height");
    }

    public async Task<string?> CheckCheckpointAsync(ChainCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        string? header;
        try
        {
            header = (await client.CallAsync("blockchain.block.header", [checkpoint.Height], cancellationToken)).Value<string>();
        }
        catch (ElectrumException ex) when (ex.Detail.Contains("height", StringComparison.OrdinalIgnoreCase))
        {
            header = null;
        }
        return string.Equals(header, checkpoint.HeaderHex, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"block {checkpoint.Height} header is {(header is null ? "missing" : "different")}";
    }

    public async Task<int> GetTransactionCountAsync(string address, CancellationToken cancellationToken)
        => (await GetHistoryAsync(address, cancellationToken)).Count;

    public async Task<IReadOnlyList<ChainOutput>> GetOutputsAsync(string address, CancellationToken cancellationToken)
    {
        var script = ScriptFor(address);
        var outputs = new List<ChainOutput>();
        foreach (var (txid, height) in await GetHistoryAsync(address, cancellationToken))
        {
            var tx = await GetTransactionAsync(txid, cancellationToken);
            for (var i = 0; i < tx.Outputs.Count; i++)
            {
                if (tx.Outputs[i].ScriptPubKey == script)
                    outputs.Add(new ChainOutput(txid, i, tx.Outputs[i].Value.Satoshi, height > 0 ? height : null));
            }
        }
        return outputs;
    }

    async Task<List<(string TxId, long Height)>> GetHistoryAsync(string address, CancellationToken cancellationToken)
    {
        var history = await client.CallAsync("blockchain.scripthash.get_history", [ScriptHash(ScriptFor(address))], cancellationToken);
        // Height 0 (or -1 with unconfirmed parents) means unconfirmed.
        return history.OfType<JObject>()
            .Select(h => (h.Value<string>("tx_hash") ?? throw new ElectrumException($"{Name}: history entry without tx_hash"), h.Value<long>("height")))
            .ToList();
    }

    async Task<Transaction> GetTransactionAsync(string txid, CancellationToken cancellationToken)
    {
        if (_transactions.TryGetValue(txid, out var cached))
            return cached;
        var hex = (await client.CallAsync("blockchain.transaction.get", [txid], cancellationToken)).Value<string>()
                  ?? throw new ElectrumException($"{Name}: no transaction {txid}");
        var tx = Transaction.Parse(hex, network.AddressNetwork);
        // A server cannot make up outputs for a transaction id it doesn't really have.
        if (tx.GetHash().ToString() != txid)
            throw new ElectrumException($"{Name}: transaction {txid} does not match its id");
        if (_transactions.Count >= MaxCachedTransactions)
            _transactions.Clear();
        _transactions[txid] = tx;
        return tx;
    }

    Script ScriptFor(string address) => BitcoinAddress.Create(address, network.AddressNetwork).ScriptPubKey;

    /// <summary>Electrum's script hash: SHA-256 of the output script, byte-reversed, in hex.</summary>
    public static string ScriptHash(Script script)
    {
        var hash = SHA256.HashData(script.ToBytes());
        Array.Reverse(hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose() => client.Dispose();
}
