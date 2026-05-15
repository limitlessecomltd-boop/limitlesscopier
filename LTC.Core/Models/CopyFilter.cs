namespace LTC.Core.Models;

/// <summary>
/// Filter rules that gate whether a given master trade is copied to a particular slave.
/// All filters are AND-ed: a trade must pass every active filter to be copied.
/// </summary>
public sealed class CopyFilter
{
    /// <summary>
    /// If non-empty, only symbols listed here are copied. Match is exact against the
    /// MASTER's symbol name (translation to slave naming happens in the symbol service).
    /// </summary>
    public HashSet<string> SymbolWhitelist { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Symbols listed here are blocked. Evaluated after the whitelist.</summary>
    public HashSet<string> SymbolBlacklist { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Direction filter: copy only buys, only sells, or both.
    /// Combined with ReverseCopy at the link level.
    /// </summary>
    public DirectionFilter Direction { get; set; } = DirectionFilter.Both;

    /// <summary>If &gt; 0, slave volume above this cap is clamped down to it.</summary>
    public double MaxLotPerTrade { get; set; } = 0;

    /// <summary>
    /// If &gt; 0, once the slave's realised P/L for the day drops below -DailyLossLimit (in deposit
    /// currency), no further copies are sent until the next broker day. 0 disables.
    /// </summary>
    public double DailyLossLimit { get; set; } = 0;
}

public enum DirectionFilter
{
    Both = 0,
    BuyOnly = 1,
    SellOnly = 2
}
