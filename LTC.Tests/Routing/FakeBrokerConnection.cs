using LTC.Core.Connections;
using LTC.Core.Models;

namespace LTC.Tests.Routing;

/// <summary>
/// Test double for IBrokerConnection. Records all calls and lets tests
/// trigger order updates as if from a real broker.
/// </summary>
internal sealed class FakeBrokerConnection : IBrokerConnection
{
    public Account Account { get; }
    public Guid AccountId => Account.Id;
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;
    public string? LastError { get; set; }
    public IReadOnlyCollection<string> AvailableSymbols { get; set; } = new[] { "EURUSD", "GBPUSD", "USDJPY", "XAUUSD" };

    public List<OrderSendRequest> SentOrders { get; } = new();
    public List<OrderCloseRequest> CloseRequests { get; } = new();
    public List<OrderModifyRequest> ModifyRequests { get; } = new();
    public List<string> Subscriptions { get; } = new();
    private readonly object _listGate = new();

    /// <summary>Quote returned by GetQuote(symbol). Default: bid=1.0, ask=1.0001.</summary>
    public Func<string, QuoteSnapshot?> QuoteSource { get; set; } =
        s => new QuoteSnapshot(s, 1.0, 1.0001, DateTime.UtcNow);

    /// <summary>Result returned by SendOrderAsync. Default: success with ticket = a counter.</summary>
    public Func<OrderSendRequest, OrderSendResult> SendBehavior { get; set; }
        = req => new OrderSendResult(true, (ulong)Random.Shared.Next(1000, 999999), req.Price, null);

    public Func<OrderCloseRequest, OrderSendResult> CloseBehavior { get; set; }
        = req => new OrderSendResult(true, req.Ticket, req.Price, null);

    public FakeBrokerConnection(Account account) { Account = account; }

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<MasterOrderEvent>? OrderUpdate;

    public Task ConnectAsync(CancellationToken ct = default)
    {
        SetStatus(ConnectionStatus.Connecting);
        SetStatus(ConnectionStatus.Connected);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        SetStatus(ConnectionStatus.Disconnected);
        return Task.CompletedTask;
    }

    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);
        await ConnectAsync(ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<LTC.Core.Models.TradeRecord>> GetClosedDealsAsync(
        DateTime sinceUtc, CancellationToken ct = default)
    {
        IReadOnlyList<LTC.Core.Models.TradeRecord> empty = Array.Empty<LTC.Core.Models.TradeRecord>();
        return Task.FromResult(empty);
    }

    public ValueTask DisposeAsync()
    {
        Status = ConnectionStatus.Disconnected;
        return default;
    }

    public void Subscribe(string symbol) => Subscriptions.Add(symbol);
    public QuoteSnapshot? GetQuote(string symbol) => QuoteSource(symbol);

    public Task<OrderSendResult> SendOrderAsync(OrderSendRequest req, CancellationToken ct = default)
    {
        lock (_listGate) SentOrders.Add(req);
        return Task.FromResult(SendBehavior(req));
    }

    public Task<OrderSendResult> CloseOrderAsync(OrderCloseRequest req, CancellationToken ct = default)
    {
        lock (_listGate) CloseRequests.Add(req);
        return Task.FromResult(CloseBehavior(req));
    }

    public Task<OrderSendResult> ModifyOrderAsync(OrderModifyRequest req, CancellationToken ct = default)
    {
        lock (_listGate) ModifyRequests.Add(req);
        return Task.FromResult(new OrderSendResult(true, req.Ticket, 0, null));
    }

    /// <summary>Test-controllable list of open positions returned by GetOpenPositionsAsync.</summary>
    public List<OpenPosition> Positions { get; } = new();

    public Task<IReadOnlyList<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default)
    {
        lock (_listGate)
            return Task.FromResult<IReadOnlyList<OpenPosition>>(Positions.ToList());
    }

    /// <summary>Test-controllable account stats. Null until a test sets it.</summary>
    public AccountStats? Stats { get; set; }

    public Task<AccountStats?> GetAccountStatsAsync(CancellationToken ct = default)
        => Task.FromResult(Stats);

    /// <summary>Thread-safe count of SentOrders (use in stress tests where many threads append).</summary>
    public int SentOrdersCount { get { lock (_listGate) return SentOrders.Count; } }

    /// <summary>Test helper: simulate an order update arriving from the broker.</summary>
    public void RaiseOrderUpdate(MasterOrderEvent ev) => OrderUpdate?.Invoke(this, ev);

    public void SetStatus(ConnectionStatus s)
    {
        if (Status == s) return;
        Status = s;
        StatusChanged?.Invoke(this, s);
    }

    /// <summary>Per-symbol metadata used by routing's volume-rounding step.
    /// Tests can override; default returns FX-typical 0.01-step / 0.01-min /
    /// 100-max for any symbol so existing tests don't have to be aware of
    /// volume rounding.</summary>
    public Func<string, SymbolMetadata?> SymbolMetadataSource { get; set; } =
        s => new SymbolMetadata(s, VolumeStep: 0.01, MinVolume: 0.01, MaxVolume: 100.0, Digits: 5);

    public SymbolMetadata? GetSymbolMetadata(string symbol)
        => SymbolMetadataSource(symbol);
}
