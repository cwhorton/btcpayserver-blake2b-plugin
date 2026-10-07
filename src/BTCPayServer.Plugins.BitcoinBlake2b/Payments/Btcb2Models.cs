#nullable enable
using BTCPayServer.Client.Models;

namespace BTCPayServer.Plugins.BitcoinBlake2b.Payments;

/// <summary>A store's XBT payment method configuration.</summary>
public class Btcb2PaymentMethodConfig
{
    /// <summary>Watch-only account key in NBXplorer's derivation scheme format.</summary>
    public string AccountDerivation { get; set; } = "";
    /// <summary>The key or descriptor exactly as the merchant entered it.</summary>
    public string? AccountOriginal { get; set; }
    /// <summary>The chain the key was set up for, so a mainnet key is never used on testnet4 or vice versa.</summary>
    public Btcb2Chain Chain { get; set; }
    /// <summary>Confirmations before an invoice is settled. Null follows the store's speed policy.</summary>
    public int? ConfirmationsRequired { get; set; }
}

/// <summary>What an invoice's XBT payment prompt remembers about its address.</summary>
public class Btcb2PromptDetails
{
    public int KeyIndex { get; set; }
    /// <summary>Stripped from the prompt for anyone but the store owner.</summary>
    public string? AccountDerivation { get; set; }
    /// <summary>Fixed when the invoice is created, so later setting changes do not alter it.</summary>
    public int ConfirmationsRequired { get; set; }
}

/// <summary>One received output. The payment ID is "txid-vout".</summary>
public class Btcb2PaymentData
{
    public string TxId { get; set; } = "";
    public int Vout { get; set; }
    public int KeyIndex { get; set; }
    public long? BlockHeight { get; set; }
    public long ConfirmationCount { get; set; }
    /// <summary>Which chain data sources reported this output on their last check.</summary>
    public string[] SeenBy { get; set; } = [];
}

public static class ConfirmationPolicy
{
    /// <summary>
    /// Stricter than BTCPay's Bitcoin defaults (0/1/2/6): the chain is young and its
    /// hashrate can be rented, so deep reorganizations are more plausible.
    /// </summary>
    public static int Required(SpeedPolicy speedPolicy, int? configured) => configured ?? speedPolicy switch
    {
        SpeedPolicy.HighSpeed => 1,
        SpeedPolicy.MediumSpeed => 3,
        SpeedPolicy.LowMediumSpeed => 6,
        SpeedPolicy.LowSpeed => 12,
        _ => 6
    };
}
