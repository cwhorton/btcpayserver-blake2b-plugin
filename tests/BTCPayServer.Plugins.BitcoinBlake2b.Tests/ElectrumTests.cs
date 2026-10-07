using BTCPayServer.Plugins.BitcoinBlake2b.Chain;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Tests;

public class ElectrumTests
{
    [Theory]
    [InlineData("electrum.lazarus-xbt.xyz:50002:s", "electrum.lazarus-xbt.xyz", 50002, true)]
    [InlineData("192.168.1.20:50001:t", "192.168.1.20", 50001, false)]
    [InlineData("ssl://fulcrum.local:50002", "fulcrum.local", 50002, true)]
    [InlineData("tcp://umbrel.local:50001", "umbrel.local", 50001, false)]
    public void ParsesElectrumAddresses(string url, string host, int port, bool tls)
    {
        Assert.True(ElectrumEndpoint.LooksLikeElectrum(url));
        var endpoint = ElectrumEndpoint.Parse(url);
        Assert.Equal((host, port, tls), (endpoint.Host, endpoint.Port, endpoint.UseTls));
        Assert.Null(endpoint.PinnedCertificateSha256);
    }

    [Fact]
    public void ParsesPinnedCertificate()
    {
        var fingerprint = string.Join(":", Enumerable.Range(0, 32).Select(i => i.ToString("X2")));
        var endpoint = ElectrumEndpoint.Parse($"ssl://node.local:50002?fingerprint={fingerprint}");
        Assert.Equal(Enumerable.Range(0, 32).Select(i => (byte)i), endpoint.PinnedCertificateSha256!);
        Assert.Throws<FormatException>(() => ElectrumEndpoint.Parse($"tcp://node.local:50001?fingerprint={fingerprint}"));
    }

    [Theory]
    [InlineData("node.local")]
    [InlineData("node.local:50002")]
    [InlineData("http2://node.local:50002")]
    public void RejectsBadElectrumAddresses(string url) => Assert.Throws<FormatException>(() => ElectrumEndpoint.Parse(url));

    [Fact]
    public void ExplorerUrlsAreNotElectrum()
    {
        Assert.False(ElectrumEndpoint.LooksLikeElectrum("https://mempool.guide/api"));
        Assert.False(ElectrumEndpoint.LooksLikeElectrum("http://192.168.1.20:3006/api"));
    }

    [Fact]
    public void ComputesElectrumScriptHash()
    {
        // Example from the Electrum protocol documentation.
        var script = BitcoinAddress.Create("1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa", Network.Main).ScriptPubKey;
        Assert.Equal("8b01df4e368ea28f8dc0423bcf7a4923e3a12d307c875e47a0cfbf90b5c39161", ElectrumChainSource.ScriptHash(script));
    }

    /// <summary>
    /// Talks to the real public mainnet sources. Run with LIVE_TESTS=1. Checks that the Electrum
    /// and explorer clients both pass the chain checkpoint and report identical outputs.
    /// </summary>
    [Fact]
    public async Task LiveElectrumAndExplorerAgree()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("LIVE_TESTS") == "1", "Set LIVE_TESTS=1 to run against the public mainnet sources");
        var network = new Btcb2Network(Btcb2Chain.Mainnet);
        using var http = new HttpClient();
        var factory = new SingleClientFactory(http);
        var esplora = new EsploraChainSource(factory, "https://mempool.guide/api");
        using var electrum = new ElectrumChainSource(new ElectrumClient(ElectrumEndpoint.Parse("electrum.lazarus-xbt.xyz:50002:s")), network);
        var ct = CancellationToken.None;

        Assert.Null(await esplora.CheckCheckpointAsync(network.Checkpoint, ct));
        Assert.Null(await electrum.CheckCheckpointAsync(network.Checkpoint, ct));
        Assert.InRange(await electrum.GetTipHeightAsync(ct) - await esplora.GetTipHeightAsync(ct), -2, 2);

        // Pick an address from a recent ordinary transaction and compare both views of it.
        var tipHash = (await http.GetStringAsync("https://mempool.guide/api/blocks/tip/hash")).Trim();
        var txids = JArray.Parse(await http.GetStringAsync($"https://mempool.guide/api/block/{tipHash}/txids"));
        foreach (var txid in txids.Skip(1).Take(10).Select(t => t.Value<string>()))
        {
            var tx = JObject.Parse(await http.GetStringAsync($"https://mempool.guide/api/tx/{txid}"));
            var address = tx["vout"]!.Select(o => o.Value<string>("scriptpubkey_address")).FirstOrDefault(a => !string.IsNullOrEmpty(a));
            if (address is null || await esplora.GetTransactionCountAsync(address, ct) > 20)
                continue;
            var fromEsplora = (await esplora.GetOutputsAsync(address, ct)).OrderBy(o => o.TxId).ThenBy(o => o.Vout).ToArray();
            var fromElectrum = (await electrum.GetOutputsAsync(address, ct)).OrderBy(o => o.TxId).ThenBy(o => o.Vout).ToArray();
            Assert.NotEmpty(fromEsplora);
            Assert.Equal(fromEsplora.Select(o => (o.TxId, o.Vout, o.ValueSats)), fromElectrum.Select(o => (o.TxId, o.Vout, o.ValueSats)));
            return;
        }
        Assert.Fail("No suitable address found in the tip block");
    }

    class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
