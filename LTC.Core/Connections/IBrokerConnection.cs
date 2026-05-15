using LTC.Core.Models;

namespace LTC.Core.Connections;

/// <summary>
/// Abstraction over a single broker account connection. The real implementation wraps
/// mtapi.mt5.MT5API; tests can substitute a fake without touching the network.
/// </summary>
public interface IBrokerConnection : IAsyncDisposable
{
    Guid AccountId { get; }
    Account Account { get; }
    ConnectionStatus Status { get; }

    /// <summary>The most recent connection error message, or null if the connection
    /// is healthy or has never failed. Updated each time ConnectAsync throws.</summary>
    string? LastError { get; }

    /// <summary>Symbols the broker exposes, populated after a successful connect.</summary>
    IReadOnlyCollection<string> AvailableSymbols { get; }

    event EventHandler<ConnectionStatus>? StatusChanged;

    /// <summary>Raised when an order opens, closes, or is modified on this account.</summary>
    event EventHandler<MasterOrderEvent>? OrderUpdate;

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();

    /// <summary>Force a reconnect of this account. Useful when the MT5
    /// terminal restarts, switches to a different gateway IP, or otherwise
    /// gets into a state where our existing socket is dead. Implementation
    /// disconnects then immediately reconnects with the same credentials.
    /// Safe to call while connected; will tear down the existing session.</summary>
    Task ReconnectAsync(CancellationToken ct = default);

    /// <summary>Subscribe to live quotes for the given symbol so GetQuote returns instantly.</summary>
    void Subscribe(string symbol);

    /// <summary>Returns a recent ask/bid pair for the symbol, or null if not yet received.</summary>
    QuoteSnapshot? GetQuote(string symbol);

    /// <summary>Send an order (market or pending). Async fire-and-forget for hot-path use.</summary>
    Task<OrderSendResult> SendOrderAsync(OrderSendRequest request, CancellationToken ct = default);

    /// <summary>Close (or partially close) an existing position.</summary>
    Task<OrderSendResult> CloseOrderAsync(OrderCloseRequest request, CancellationToken ct = default);

    /// <summary>Modify SL/TP on an existing position or pending order.</summary>
    Task<OrderSendResult> ModifyOrderAsync(OrderModifyRequest request, CancellationToken ct = default);

    /// <summary>Snapshot of currently open positions on this broker account.
    /// Returns empty if not connected or if no positions are open.</summary>
    Task<IReadOnlyList<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default);

    /// <summary>Snapshot of account-level financial state. Returns null if not
    /// connected or if the broker has not yet reported account values.</summary>
    Task<AccountStats?> GetAccountStatsAsync(CancellationToken ct = default);

    /// <summary>Closed trade deals on this account from a given time onward.
    /// Used by the Prop Journal tab to compute trading days, today's P&amp;L,
    /// win rate, best/worst trade, and the consistency-rule history.
    ///
    /// MT5's deal history is a stream of individual deals where a trade has
    /// TWO deals: an "in" (open) and an "out" (close). This method returns
    /// only the OUT side (the actual realized trade events), so each entry
    /// corresponds to one closed position. The Profit field is the net P&amp;L
    /// for that trade including swap and commission.
    ///
    /// Returns empty list if the broker DLL doesn't support history access
    /// on this account, the connection is down, or no closed trades exist
    /// since the supplied datetime. The caller should NEVER assume the
    /// returned list is complete — it's a best-effort snapshot.</summary>
    Task<IReadOnlyList<TradeRecord>> GetClosedDealsAsync(
        DateTime sinceUtc, CancellationToken ct = default);

    /// <summary>Volume / point metadata for a symbol on this broker. Returns
    /// null if the symbol isn't in the broker's catalog. Brokers each define
    /// their own minimum lot, lot step, and max lot per symbol — an order
    /// whose volume isn't a multiple of the step is rejected with
    /// "invalid volume". The routing engine rounds computed slave volume to
    /// the nearest valid step before sending using these values.</summary>
    SymbolMetadata? GetSymbolMetadata(string symbol);
}

/// <summary>Volume + price-tick metadata for a single symbol on a broker.
/// Captured at connect time; assumed stable for the session.</summary>
public sealed record SymbolMetadata(
    string Symbol,
    double VolumeStep,   // Smallest volume increment, e.g. 0.01 lot
    double MinVolume,    // Smallest volume the broker will accept
    double MaxVolume,    // Largest volume the broker will accept
    int    Digits);      // Price decimal places (used for SL/TP rounding)

public sealed record QuoteSnapshot(string Symbol, double Bid, double Ask, DateTime TimestampUtc);

/// <summary>One open position as reported by the broker.</summary>
public sealed record OpenPosition(
    ulong Ticket,
    string Symbol,
    CopyOrderType OrderType,
    double Volume,
    double OpenPrice,
    double CurrentPrice,
    double StopLoss,
    double TakeProfit,
    double Profit,
    double Swap,
    double Commission,
    DateTime OpenTimeUtc);

/// <summary>Snapshot of account-level financial state.</summary>
public sealed record AccountStats(
    double Balance,
    double Equity,
    double Margin,
    double FreeMargin,
    double MarginLevelPercent,
    double Profit,        // total floating P&L from currently open positions
    string Currency);
