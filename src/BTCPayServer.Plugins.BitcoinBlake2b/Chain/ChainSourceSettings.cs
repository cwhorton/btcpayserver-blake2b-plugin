#nullable enable
using System;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Chain;

public enum ChainSourceMode
{
    /// <summary>Independent public explorers; on mainnet two of three must agree.</summary>
    Public,
    /// <summary>Only your own sources, trusted on their own.</summary>
    Own,
    /// <summary>Your own sources and the public ones together; any two must agree.</summary>
    OwnWithPublicCheck
}

/// <summary>Server-wide settings, managed by the server admin.</summary>
public class Btcb2ServerSettings
{
    public const string SettingName = "BitcoinBlake2b";

    public string? DisplayName { get; set; }
    public ChainSourceMode Mode { get; set; } = ChainSourceMode.Public;
    /// <summary>Explorer API URLs and Electrum server addresses.</summary>
    public string[] OwnSources { get; set; } = [];
    /// <summary>
    /// Lets store owners use their own chain data sources. Off by default: the server connects
    /// to whatever they enter, which on a shared server could reach its internal network.
    /// </summary>
    public bool AllowStoreSources { get; set; }
}

/// <summary>A store's own chain data sources, used only if the server admin allows it.</summary>
public class Btcb2StoreSourceSettings
{
    public const string SettingName = "BitcoinBlake2b.Sources";

    /// <summary>Public means "use the server's sources".</summary>
    public ChainSourceMode Mode { get; set; } = ChainSourceMode.Public;
    public string[] OwnSources { get; set; } = [];
}

public static class ChainSourceRules
{
    /// <summary>The sources and required agreement for a mode, or why that's impossible.</summary>
    /// <exception cref="FormatException">With a message suitable for the user.</exception>
    public static (string[] Sources, int RequiredAgreement) Resolve(ChainSourceMode mode, string[] ownSources, Btcb2Network network)
    {
        var own = ChainSources.ParseUrls(string.Join(",", ownSources));
        if (mode != ChainSourceMode.Public && own.Length == 0)
            throw new FormatException("Enter at least one explorer API URL or Electrum server.");
        return mode switch
        {
            ChainSourceMode.Public => (network.PublicEsploraUrls, network.PublicRequiredAgreement),
            ChainSourceMode.Own => (own, 1),
            ChainSourceMode.OwnWithPublicCheck => ([.. own, .. network.PublicEsploraUrls], 2),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }
}
