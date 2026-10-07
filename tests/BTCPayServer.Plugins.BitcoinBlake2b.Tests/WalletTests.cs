using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.BitcoinBlake2b.Payments;
using BTCPayServer.Plugins.BitcoinBlake2b.Wallet;
using NBitcoin;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Tests;

public class WalletTests
{
    // BIP84 test vectors: https://github.com/bitcoin/bips/blob/master/bip-0084.mediawiki
    const string Mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    const string Bip84Zpub = "zpub6rFR7y4Q2AijBEqTUquhVz398htDFrtymD9xYYfG1m4wAcvPhXNfE3EfH1r1ADqtfSdVCToUG868RvUUkgDKf31mGDtKsAYz2oz2AGutZYs";
    static readonly string[] Bip84Receive = ["bc1qcr8te4kr609gcawutmrza0j4xv80jy8z306fyu", "bc1qnjg0jd8228aq7egyzacy8cys3knf9xvrerkf9g"];

    static readonly Btcb2Network Mainnet = new(Btcb2Chain.Mainnet);
    static readonly Btcb2Network Testnet4 = new(Btcb2Chain.Testnet4);

    static ExtPubKey AccountKey(string path) => new Mnemonic(Mnemonic).DeriveExtKey().Derive(KeyPath.Parse(path)).Neuter();

    [Fact]
    public void DerivesBip84AddressesFromZpub()
    {
        var settings = WalletKey.Parse(Bip84Zpub, Mainnet);
        Assert.Equal(Bip84Receive, WalletKey.PreviewAddresses(settings.AccountDerivation, Mainnet, 2));
    }

    [Fact]
    public void DerivesSameAddressesFromDescriptor()
    {
        var xpub = AccountKey("84'/0'/0'").GetWif(Network.Main);
        var settings = WalletKey.Parse($"wpkh([73c5da0a/84'/0'/0']{xpub}/0/*)", Mainnet);
        Assert.Equal(Bip84Receive, WalletKey.PreviewAddresses(settings.AccountDerivation, Mainnet, 2));
    }

    [Fact]
    public void StoredDerivationRoundTrips()
    {
        var stored = WalletKey.Parse(Bip84Zpub, Mainnet).AccountDerivation.ToString();
        var strategy = WalletKey.ParseDerivation(stored, Mainnet);
        Assert.Equal(Bip84Receive[1], WalletKey.DeriveReceiveAddress(strategy, 1, Mainnet).ToString());
    }

    [Fact]
    public void Testnet4UsesTestnetKeysAndAddresses()
    {
        var tpub = AccountKey("84'/1'/0'").GetWif(Network.TestNet);
        var settings = WalletKey.Parse($"wpkh([73c5da0a/84'/1'/0']{tpub}/0/*)", Testnet4);
        Assert.All(WalletKey.PreviewAddresses(settings.AccountDerivation, Testnet4), a => Assert.StartsWith("tb1q", a));
    }

    [Fact]
    public void RejectsKeysForTheOtherChain()
    {
        Assert.Throws<FormatException>(() => WalletKey.Parse(Bip84Zpub, Testnet4));
        var tpub = AccountKey("84'/1'/0'").GetWif(Network.TestNet);
        Assert.Throws<FormatException>(() => WalletKey.Parse($"wpkh([73c5da0a/84'/1'/0']{tpub}/0/*)", Mainnet));
    }

    [Fact]
    public void RejectsPrivateKeysAndGarbage()
    {
        var xprv = new Mnemonic(Mnemonic).DeriveExtKey().Derive(KeyPath.Parse("84'/0'/0'")).GetWif(Network.Main).ToString();
        var ex = Assert.Throws<FormatException>(() => WalletKey.Parse(xprv, Mainnet));
        Assert.Contains("private key", ex.Message);
        Assert.Throws<FormatException>(() => WalletKey.Parse("", Mainnet));
        Assert.Throws<FormatException>(() => WalletKey.Parse("not a key", Mainnet));
    }

    [Theory]
    [InlineData("mainnet", null, Btcb2Chain.Mainnet)]
    [InlineData("testnet", null, Btcb2Chain.Testnet4)]
    [InlineData("regtest", null, Btcb2Chain.Testnet4)]
    [InlineData("regtest", "mainnet", Btcb2Chain.Mainnet)]
    [InlineData("mainnet", "testnet4", Btcb2Chain.Testnet4)]
    public void SelectsChain(string btcpayChain, string? configured, Btcb2Chain expected)
    {
        var chain = btcpayChain switch { "mainnet" => ChainName.Mainnet, "testnet" => ChainName.Testnet, _ => ChainName.Regtest };
        Assert.Equal(expected, Btcb2Network.SelectChain(chain, configured));
        Assert.Throws<BTCPayServer.Configuration.ConfigException>(() => Btcb2Network.SelectChain(chain, "signet"));
    }

    [Fact]
    public void ConfirmationDefaultsAreStricterThanBitcoin()
    {
        Assert.Equal(1, ConfirmationPolicy.Required(SpeedPolicy.HighSpeed, null));
        Assert.Equal(3, ConfirmationPolicy.Required(SpeedPolicy.MediumSpeed, null));
        Assert.Equal(6, ConfirmationPolicy.Required(SpeedPolicy.LowMediumSpeed, null));
        Assert.Equal(12, ConfirmationPolicy.Required(SpeedPolicy.LowSpeed, null));
        Assert.Equal(0, ConfirmationPolicy.Required(SpeedPolicy.LowSpeed, 0));
    }
}
