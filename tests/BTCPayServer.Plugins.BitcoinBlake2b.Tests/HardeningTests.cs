using System.Net;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.BitcoinBlake2b.Chain;
using BTCPayServer.Plugins.BitcoinBlake2b.Payments;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Tests;

public class HardeningTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void OnlyPublicAddressesAreAllowedForStoreSources(string ip, bool allowed) =>
        Assert.Equal(allowed, NetworkRestrictions.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public void SameServerWrittenTwoWaysIsOneSource()
    {
        Assert.Equal(ChainSources.Canonical("electrum.example.com:50002:s"), ChainSources.Canonical("ssl://electrum.example.com:50002"));
        Assert.Equal(ChainSources.Canonical("https://Mempool.Example.com/api/"), ChainSources.Canonical("https://mempool.example.com:443/api"));
        Assert.NotEqual(ChainSources.Canonical("ssl://e.example.com:50002"), ChainSources.Canonical("tcp://e.example.com:50002"));
    }

    [Theory]
    [InlineData("https://mempool.example.com/api?x=1")]
    [InlineData("https://mempool.example.com/api#frag")]
    [InlineData("https://user:pass@mempool.example.com/api")]
    public void ExplorerUrlsCantCarryQueriesOrCredentials(string url) =>
        Assert.Throws<FormatException>(() => ChainSources.ValidateSource(url));

    [Fact]
    public void EnvironmentSourcesDefaultToTwoMustAgree()
    {
        Assert.Null(ChainSources.ParseEnvironment("", null));
        Assert.Equal(1, ChainSources.ParseEnvironment("https://a.example.com/api", null)!.Value.RequiredAgreement);
        Assert.Equal(2, ChainSources.ParseEnvironment("https://a.example.com/api,https://b.example.com/api,tcp://c.example.com:50001", null)!.Value.RequiredAgreement);
        Assert.Equal(1, ChainSources.ParseEnvironment("https://a.example.com/api,https://b.example.com/api", "1")!.Value.RequiredAgreement);
        // The same server twice is still one source.
        Assert.Equal(1, ChainSources.ParseEnvironment("ssl://e.example.com:50002,e.example.com:50002:s", null)!.Value.RequiredAgreement);
        Assert.Throws<ConfigException>(() => ChainSources.ParseEnvironment("https://a.example.com/api", "3"));
        Assert.Throws<ConfigException>(() => ChainSources.ParseEnvironment("https://a.example.com/api", "two"));
        Assert.Throws<ConfigException>(() => ChainSources.ParseEnvironment("ftp://a.example.com", null));
    }

    [Theory]
    [InlineData("XBT", true)]
    [InlineData("tXBT", true)]
    [InlineData("Bitcoin B2", true)]
    [InlineData("XBT.test_1-a", true)]
    [InlineData("", false)]
    [InlineData(" XBT", false)]
    [InlineData("{{ alert(1) }}", false)]
    [InlineData("<b>XBT</b>", false)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU", false)]
    public void DisplayNamesArePlainText(string name, bool valid) => Assert.Equal(valid, Btcb2.IsValidDisplayName(name));

    [Fact]
    public void PollingSlowsDownForOldUnpaidInvoices()
    {
        var poll = TimeSpan.FromSeconds(15);
        Assert.Equal(poll, Btcb2Listener.NextCheckIn(poll, TimeSpan.FromMinutes(5), false, false));
        Assert.Equal(TimeSpan.FromMinutes(2), Btcb2Listener.NextCheckIn(poll, TimeSpan.FromHours(3), false, false));
        Assert.Equal(TimeSpan.FromMinutes(10), Btcb2Listener.NextCheckIn(poll, TimeSpan.FromDays(2), false, false));
        // Payments awaiting confirmations: every new block, otherwise every minute.
        Assert.Equal(poll, Btcb2Listener.NextCheckIn(poll, TimeSpan.FromDays(2), true, true));
        Assert.Equal(TimeSpan.FromMinutes(1), Btcb2Listener.NextCheckIn(poll, TimeSpan.FromDays(2), true, false));
    }

    [Fact]
    public void ChainErrorsShownToUsersNeverRepeatWhatTheSourceSent()
    {
        var ex = new ChainSourceException("example.com: the server returned an error for blockchain.block.header", "secret internal content");
        Assert.DoesNotContain("secret", ChainMonitor.Describe(ex));
        Assert.Equal("Unexpected answer", ChainMonitor.Describe(new Newtonsoft.Json.JsonReaderException("Unexpected character 'S' (secret page)")));
    }
}
