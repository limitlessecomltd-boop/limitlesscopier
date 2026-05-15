namespace LTC.Core.Models;

/// <summary>
/// How master volume is converted into slave volume.
/// </summary>
public enum LotSizingMode
{
    /// <summary>Always use the same fixed lot size on the slave, regardless of master.</summary>
    Fixed = 1,

    /// <summary>Slave lot = master lot × multiplier.</summary>
    Multiplier = 2,

    /// <summary>Slave lot scales so the slave risks the configured % of equity per trade (requires SL).</summary>
    RiskPercent = 3,

    /// <summary>Slave lot = master lot × (slave equity / master equity).</summary>
    EquityRatio = 4,

    /// <summary>Slave lot = master lot × (slave balance / master balance).</summary>
    BalanceRatio = 5
}

/// <summary>
/// Configuration for the lot-sizing calculation on a CopyLink.
/// </summary>
public sealed class LotSizingConfig
{
    public LotSizingMode Mode { get; set; } = LotSizingMode.Multiplier;

    /// <summary>
    /// The numeric parameter for the chosen mode:
    /// Fixed → lots; Multiplier → factor; RiskPercent → percent (e.g. 1.0 = 1%);
    /// EquityRatio / BalanceRatio → optional cap (0 = no cap).
    /// </summary>
    public double Value { get; set; } = 1.0;

    /// <summary>Minimum lot to ever send. 0 disables the floor.</summary>
    public double MinLot { get; set; } = 0.01;

    /// <summary>Maximum lot to ever send. 0 disables the ceiling.</summary>
    public double MaxLot { get; set; } = 100.0;
}
