using BTCPayServer.Plugins.BitcoinBlake2b.Chain;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Tests;

public class ChainTests
{
    const string Address = "tb1qr782tef3lyj7d2k00tcuwkfr0zeqk8vjhxs2z3";
    static readonly ChainOutput Unconfirmed = new("aa", 1, 50_000, null);
    static readonly ChainOutput ConfirmedAt100 = new("aa", 1, 50_000, 100);

    static SourceObservation Source(string name, long tip, params ChainOutput[] outputs) => new(name, tip, outputs);

    [Fact]
    public void SingleTrustedSourceDecidesAlone()
    {
        var view = ObservationAggregator.Aggregate([Source("own", 102, ConfirmedAt100)], 1);
        Assert.True(view.Conclusive);
        var output = Assert.Single(view.Outputs);
        Assert.Equal(3, output.Confirmations);
        Assert.Equal("aa-1", output.PaymentId);
        Assert.Equal(["own"], output.SeenBy);
    }

    [Fact]
    public void OutputNeedsEnoughSourcesToAgree()
    {
        var oneOfThree = ObservationAggregator.Aggregate([Source("a", 100, Unconfirmed), Source("b", 100), Source("c", 100)], 2);
        Assert.True(oneOfThree.Conclusive);
        Assert.Empty(oneOfThree.Outputs);

        var twoOfThree = ObservationAggregator.Aggregate([Source("a", 100, Unconfirmed), Source("b", 100, Unconfirmed), Source("c", 100)], 2);
        Assert.Equal(["a", "b"], Assert.Single(twoOfThree.Outputs).SeenBy);
    }

    [Fact]
    public void TooFewAnswersIsInconclusive()
    {
        var view = ObservationAggregator.Aggregate([Source("a", 100, Unconfirmed)], 2);
        Assert.False(view.Conclusive);
        Assert.Empty(view.Outputs);
    }

    [Fact]
    public void ConfirmationsAreWhatEnoughSourcesSupport()
    {
        // a is ahead (5 confirmations), b and c see 3 and 2: two sources support only 3.
        var view = ObservationAggregator.Aggregate(
            [Source("a", 104, ConfirmedAt100), Source("b", 102, ConfirmedAt100), Source("c", 101, ConfirmedAt100)], 2);
        Assert.Equal(3, Assert.Single(view.Outputs).Confirmations);

        // One source sees it confirmed, the other still unconfirmed: not yet confirmed.
        var split = ObservationAggregator.Aggregate([Source("a", 100, ConfirmedAt100), Source("b", 100, Unconfirmed)], 2);
        Assert.Equal(0, Assert.Single(split.Outputs).Confirmations);
    }

    [Fact]
    public void ASourceListingAnOutputTwiceCountsOnce()
    {
        // A lying Electrum server repeats a made-up transaction in its history.
        var liar = Source("liar", 200, new ChainOutput("fake", 1, 50_000, 190), new ChainOutput("fake", 1, 50_000, 189));
        var view = ObservationAggregator.Aggregate([liar, Source("honest", 200)], 2);
        Assert.True(view.Conclusive);
        Assert.Empty(view.Outputs);
        // And the same source under two observations (same URL) is one source.
        var twice = ObservationAggregator.Aggregate([Source("a", 200, Unconfirmed), Source("a", 200, Unconfirmed)], 2);
        Assert.False(twice.Conclusive);
    }

    [Fact]
    public void CountsSourcesThatOmitAPayment()
    {
        var view = ObservationAggregator.Aggregate([Source("a", 100, Unconfirmed), Source("b", 100), Source("c", 100)], 2);
        Assert.Equal(2, view.OmittedBy("aa-1"));
        Assert.Equal(3, view.OmittedBy("other-0"));
    }

    [Fact]
    public void DisagreeingAmountsDoNotCombine()
    {
        var view = ObservationAggregator.Aggregate(
            [Source("a", 100, new ChainOutput("aa", 1, 50_000, null)), Source("b", 100, new ChainOutput("aa", 1, 99_999, null))], 2);
        Assert.Empty(view.Outputs);
    }

    [Fact]
    public void ConfirmedOutputHasAtLeastOneConfirmation()
    {
        Assert.Equal(1, ObservationAggregator.Confirmations(99, 100));
        Assert.Equal(0, ObservationAggregator.Confirmations(100, null));
        Assert.Equal(6, ObservationAggregator.Confirmations(105, 100));
    }

    [Fact]
    public void ParsesEsploraTransactions()
    {
        var txs = JArray.Parse($$$"""
            [
              {"txid":"t1","vout":[{"value":1000,"scriptpubkey_address":"other"},{"value":50000,"scriptpubkey_address":"{{{Address}}}"}],
               "status":{"confirmed":false}},
              {"txid":"t2","vout":[{"value":7000,"scriptpubkey_address":"{{{Address}}}"},{"value":0,"scriptpubkey_address":""},{"value":8000,"scriptpubkey_address":"{{{Address}}}"}],
               "status":{"confirmed":true,"block_height":152560,"block_hash":"00"}}
            ]
            """).OfType<JObject>();
        var outputs = EsploraChainSource.ParseOutputs(txs, Address);
        Assert.Equal([new ChainOutput("t1", 1, 50000, null), new ChainOutput("t2", 0, 7000, 152560), new ChainOutput("t2", 2, 8000, 152560)], outputs);
    }

    [Fact]
    public void ParsesEsploraTransactionCount()
    {
        var json = JObject.Parse("""{"address":"x","chain_stats":{"tx_count":3},"mempool_stats":{"tx_count":1},"electrum":true}""");
        Assert.Equal(4, EsploraChainSource.ParseTransactionCount(json));
    }

    [Fact]
    public void PublicDefaultsAndCheckpoints()
    {
        var mainnet = new Btcb2Network(Btcb2Chain.Mainnet);
        Assert.Equal(3, mainnet.PublicEsploraUrls.Length);
        Assert.Equal(2, mainnet.PublicRequiredAgreement);
        Assert.Equal(961_640, mainnet.Checkpoint.Height);
        var testnet4 = new Btcb2Network(Btcb2Chain.Testnet4);
        Assert.Equal(1, testnet4.PublicRequiredAgreement);
        Assert.Equal(150_308, testnet4.Checkpoint.Height);
    }
}
