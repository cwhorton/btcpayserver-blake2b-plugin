using BTCPayServer.Plugins.BitcoinBlake2b.Chain;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Tests;

public class SourceRulesTests
{
    static readonly Btcb2Network Mainnet = new(Btcb2Chain.Mainnet);
    static readonly Btcb2Network Testnet4 = new(Btcb2Chain.Testnet4);

    [Fact]
    public void PublicUsesPublicExplorers()
    {
        var (sources, agreement) = ChainSourceRules.Resolve(ChainSourceMode.Public, ["https://ignored.example/api"], Mainnet);
        Assert.Equal(Mainnet.PublicEsploraUrls, sources);
        Assert.Equal(2, agreement);
    }

    [Fact]
    public void OwnSourcesAreTrustedAlone()
    {
        var (sources, agreement) = ChainSourceRules.Resolve(ChainSourceMode.Own, ["https://mempool.mynode.local/api\nssl://electrs.mynode.local:50002"], Mainnet);
        Assert.Equal(["https://mempool.mynode.local/api", "ssl://electrs.mynode.local:50002"], sources);
        Assert.Equal(1, agreement);
    }

    [Fact]
    public void CrossCheckAddsPublicExplorersAndNeedsTwo()
    {
        var (sources, agreement) = ChainSourceRules.Resolve(ChainSourceMode.OwnWithPublicCheck, ["tcp://umbrel.local:50001"], Testnet4);
        Assert.Equal(["tcp://umbrel.local:50001", .. Testnet4.PublicEsploraUrls], sources);
        Assert.Equal(2, agreement);
    }

    [Theory]
    [InlineData(ChainSourceMode.Own)]
    [InlineData(ChainSourceMode.OwnWithPublicCheck)]
    public void OwnModesNeedASource(ChainSourceMode mode) =>
        Assert.Throws<FormatException>(() => ChainSourceRules.Resolve(mode, ["  "], Mainnet));

    [Theory]
    [InlineData("https://mempool.mynode.local/api")]
    [InlineData("http://192.168.1.20:3006/api")]
    [InlineData("ssl://electrs.mynode.local:50002")]
    [InlineData("electrum.lazarus-xbt.xyz:50002:s")]
    public void AcceptsValidSources(string url) => ChainSources.ValidateSource(url);

    [Theory]
    [InlineData("ftp://mynode.local/api")]
    [InlineData("mynode.local")]
    [InlineData("ssl://mynode.local")]
    public void RejectsInvalidSources(string url) => Assert.Throws<FormatException>(() => ChainSources.ValidateSource(url));
}
