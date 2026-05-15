namespace LTC.Core.Models;

/// <summary>
/// A directed link from one master account to one slave account, carrying all the rules
/// that govern how trades are translated. A slave following multiple masters has multiple
/// CopyLink rows, one per master.
/// </summary>
public sealed class CopyLink
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid MasterAccountId { get; set; }
    public Guid SlaveAccountId { get; set; }

    /// <summary>If false, this link is paused — trades from the master are ignored for this slave.</summary>
    public bool Enabled { get; set; } = true;

    public LotSizingConfig LotSizing { get; set; } = new();
    public CopyFilter Filter { get; set; } = new();

    /// <summary>If true, master Buy → slave Sell and vice versa (mirroring strategies).</summary>
    public bool ReverseCopy { get; set; } = false;

    /// <summary>If true, pending orders (limits, stops) are copied. If false, only market orders.</summary>
    public bool CopyPending { get; set; } = true;

    /// <summary>If true, SL and TP from the master are mirrored onto the slave.</summary>
    public bool CopySLTP { get; set; } = true;

    /// <summary>
    /// If true, modifications to the master's SL/TP after the trade is open are propagated
    /// to the slave. If false, slave SL/TP is set once at open and left alone.
    /// </summary>
    public bool CopyModifications { get; set; } = true;

    /// <summary>
    /// User-supplied symbol overrides that take precedence over the auto-mapper.
    /// Key = master symbol, Value = slave symbol.
    /// </summary>
    public Dictionary<string, string> SymbolMapOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum slippage (in points) allowed when sending the slave order.</summary>
    public ulong MaxSlippagePoints { get; set; } = 100;

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
