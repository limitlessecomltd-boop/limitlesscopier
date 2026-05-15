namespace LTC.Core.Connections;

/// <summary>
/// Direction / pending-order type. Mirrors mtapi.mt5.OrderType but kept independent
/// so the Core layer doesn't leak the DLL types into ViewModels and tests.
/// </summary>
public enum CopyOrderType
{
    Buy = 0,
    Sell = 1,
    BuyLimit = 2,
    SellLimit = 3,
    BuyStop = 4,
    SellStop = 5,
    BuyStopLimit = 6,
    SellStopLimit = 7
}

public sealed record OrderSendRequest(
    string Symbol,
    double Volume,
    double Price,
    CopyOrderType OrderType,
    double StopLoss = 0,
    double TakeProfit = 0,
    ulong MaxSlippagePoints = 100,
    string? Comment = null);

public sealed record OrderCloseRequest(
    ulong Ticket,
    string Symbol,
    double Volume,
    CopyOrderType OriginalOrderType,
    double Price = 0,
    ulong MaxSlippagePoints = 100);

public sealed record OrderModifyRequest(
    ulong Ticket,
    string Symbol,
    double StopLoss,
    double TakeProfit,
    double? PendingPrice = null);

public sealed record OrderSendResult(
    bool Success,
    ulong Ticket,
    double FillPrice,
    string? ErrorMessage = null);

/// <summary>
/// Event raised by a master account when something happens to one of its orders.
/// This is the trigger that fans out to slaves.
/// </summary>
public sealed record MasterOrderEvent(
    Guid MasterAccountId,
    MasterEventKind Kind,
    ulong Ticket,
    string Symbol,
    CopyOrderType OrderType,
    double Volume,
    double Price,
    double StopLoss,
    double TakeProfit,
    DateTime ServerTimeUtc,
    long ReceivedAtTicks);  // Stopwatch.GetTimestamp() at receipt — used for latency math

public enum MasterEventKind
{
    MarketOpen,
    MarketClose,
    PartialClose,
    Modify,
    PendingPlace,
    PendingCancel,
    PendingFilled
}
