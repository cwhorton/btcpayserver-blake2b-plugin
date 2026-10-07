#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Chain;

/// <summary>An output paying to a watched address. BlockHeight is null while unconfirmed.</summary>
public record ChainOutput(string TxId, int Vout, long ValueSats, long? BlockHeight);

/// <summary>A source of XBT chain data, such as a block explorer or an Electrum server.</summary>
public interface IChainSource
{
    /// <summary>Short name shown to merchants, such as the host name.</summary>
    string Name { get; }
    string Url { get; }
    Task<long> GetTipHeightAsync(CancellationToken cancellationToken);
    /// <summary>
    /// Null if the source reports the checkpoint block, proving it follows Bitcoin BLAKE2b;
    /// otherwise what it reported instead.
    /// </summary>
    Task<string?> CheckCheckpointAsync(ChainCheckpoint checkpoint, CancellationToken cancellationToken);
    /// <summary>Confirmed plus unconfirmed transactions involving the address.</summary>
    Task<int> GetTransactionCountAsync(string address, CancellationToken cancellationToken);
    /// <summary>Every output paying to the address, confirmed or not.</summary>
    Task<IReadOnlyList<ChainOutput>> GetOutputsAsync(string address, CancellationToken cancellationToken);
}
