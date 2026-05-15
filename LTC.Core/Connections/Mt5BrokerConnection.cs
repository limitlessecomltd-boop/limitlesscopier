using System.Collections.Concurrent;
using System.Linq;
using LTC.Core.Diagnostics;
using LTC.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mtapi.mt5;

namespace LTC.Core.Connections;

/// <summary>
/// Real implementation of <see cref="IBrokerConnection"/> that wraps mtapi.mt5.MT5API.
/// One instance per <see cref="Account"/>. Owns the connection lifecycle, translates
/// between DLL types and our public abstractions, and forwards order updates to subscribers.
/// </summary>
public sealed class Mt5BrokerConnection : IBrokerConnection
{
    private readonly Account _account;
    private readonly ILogger _logger;
    private readonly object _statusLock = new();

    private MT5API? _api;
    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    private IReadOnlyCollection<string> _availableSymbols = Array.Empty<string>();
    private readonly ConcurrentDictionary<string, QuoteSnapshot> _quoteCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-symbol volume/digit metadata. Cached lazily on first lookup
    /// because each query into the DLL's Symbols.Infos dictionary is cheap but
    /// not free (and routing hits this on every trade open).</summary>
    private readonly ConcurrentDictionary<string, SymbolMetadata?> _symbolMeta =
        new(StringComparer.OrdinalIgnoreCase);

    // ===================================================================
    // CLOSED-DEAL HISTORY CACHE
    // ===================================================================
    // Pulling deal history from MT5 is moderately expensive — the DLL
    // serializes a request to the terminal which round-trips to the broker.
    // The Prop Journal tab needs this data frequently but it doesn't change
    // second-to-second (closed trades only appear when positions close).
    // So we cache results for ~30 seconds. A force-refresh path (e.g. when
    // a position-close event arrives) could invalidate sooner; for now the
    // tab refreshes its own bound state every second and a 30-second cache
    // is the right balance.
    private readonly object _dealCacheLock = new();
    private DateTime _dealCacheSinceUtc;
    private DateTime _dealCacheRetrievedAtUtc;
    private IReadOnlyList<TradeRecord>? _dealCachePayload;
    private static readonly TimeSpan DealCacheTtl = TimeSpan.FromSeconds(30);

    public Mt5BrokerConnection(Account account, ILogger? logger = null)
    {
        _account = account;
        _logger = logger ?? NullLogger.Instance;
    }

    public Guid AccountId => _account.Id;
    public Account Account => _account;
    public ConnectionStatus Status
    {
        get { lock (_statusLock) return _status; }
    }
    public IReadOnlyCollection<string> AvailableSymbols => _availableSymbols;

    private volatile string? _lastError;
    public string? LastError => _lastError;

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<MasterOrderEvent>? OrderUpdate;

    // -------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (Status is ConnectionStatus.Connected or ConnectionStatus.Connecting)
            return;

        SetStatus(ConnectionStatus.Connecting);

        try
        {
            _api = new MT5API(_account.Login, _account.Password, _account.Server, _account.Port);
            _api.OnQuote += HandleQuote;
            _api.OnOrderUpdate += HandleOrderUpdate;

            // Connect runs synchronously in the DLL — push to a worker thread so
            // we never block the caller. ct supports cancellation but the underlying
            // call doesn't, so the best we can do is observe it post-hoc.
            await Task.Run(() => _api.Connect(), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Snapshot symbols
            try
            {
                var keys = _api.Symbols?.Infos?.Keys;
                _availableSymbols = keys is null
                    ? Array.Empty<string>()
                    : keys.ToArray();
                _logger.LogInformation("Account {Login} is now connected. Loaded {Count} symbols from the broker.",
                    _account.Login, _availableSymbols.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Account {Login} connected but we could not read the broker's symbol list. Trades may fail until reconnect.",
                    _account.Login);
                _availableSymbols = Array.Empty<string>();
            }

            _account.LastConnectedAt = DateTime.UtcNow;
            _lastError = null;
            SetStatus(ConnectionStatus.Connected);
        }
        catch (OperationCanceledException)
        {
            await SafeDisposeApiAsync().ConfigureAwait(false);
            SetStatus(ConnectionStatus.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            // Capture the most useful error message we can. ServerException carries
            // a broker-side error code that often explains the failure ("Invalid
            // login", "No connection", etc).
            _lastError = ex switch
            {
                mtapi.mt5.ServerException se => $"Broker rejected connection: {se.Message} (code {se.Code})",
                System.Net.Sockets.SocketException se => $"Network error: {se.Message}",
                TimeoutException             => "Connection timed out — check IP, port, and your firewall",
                _                            => ex.Message
            };
            _logger.LogError(ex, "Account {Login} could not connect. Reason: {Reason}", _account.Login, _lastError);
            await SafeDisposeApiAsync().ConfigureAwait(false);
            SetStatus(ConnectionStatus.Failed);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (Status == ConnectionStatus.Disconnected) return;
        await SafeDisposeApiAsync().ConfigureAwait(false);
        SetStatus(ConnectionStatus.Disconnected);
    }

    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        // Tear down the current session (if any) and immediately stand up a
        // fresh one. This is the cleanest way to recover from a stale socket
        // or a terminal that changed gateway IP — the MT5 client picks up
        // the current terminal config on Connect(), so a full cycle is
        // equivalent to "restart the app for this account only".
        //
        // Status visible to the UI moves: connected -> disconnected ->
        // connecting -> connected (or failed). The Accounts tab refresh
        // button shows the spinner-state during the brief gap.
        _logger.LogInformation("Account {Login}: manual reconnect requested.", _account.Login);
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Account {Login}: reconnect disconnect step threw; proceeding anyway.", _account.Login);
        }
        // Brief pause so the broker DLL fully releases its socket before
        // we open a new one. Without it some MT5 builds throw "already
        // connected" on the immediate Connect() call.
        await Task.Delay(250, ct).ConfigureAwait(false);
        await ConnectAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(SafeDisposeApiAsync());
    }

    private async Task SafeDisposeApiAsync()
    {
        var api = Interlocked.Exchange(ref _api, null);
        if (api is null) return;

        try
        {
            api.OnQuote -= HandleQuote;
            api.OnOrderUpdate -= HandleOrderUpdate;
        }
        catch { /* tolerate */ }

        await Task.Run(() =>
        {
            try { api.Disconnect(); } catch { /* tolerate */ }
        }).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    // Subscribe & quotes
    // -------------------------------------------------------------------
    public void Subscribe(string symbol)
    {
        var api = _api;
        if (api is null || string.IsNullOrWhiteSpace(symbol)) return;
        try { api.Subscribe(symbol); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Account {Login}: could not subscribe to live quotes for {Symbol}.",
                _account.Login, symbol);
        }
    }

    public QuoteSnapshot? GetQuote(string symbol)
    {
        if (_quoteCache.TryGetValue(symbol, out var cached)) return cached;

        var api = _api;
        if (api is null) return null;

        try
        {
            var q = api.GetQuote(symbol);
            if (q is null) return null;
            var snap = new QuoteSnapshot(q.Symbol, q.Bid, q.Ask, DateTime.UtcNow);
            _quoteCache[symbol] = snap;
            return snap;
        }
        catch
        {
            return null;
        }
    }

    private void HandleQuote(MT5API api, Quote q)
    {
        if (q is null) return;
        try
        {
            _quoteCache[q.Symbol] = new QuoteSnapshot(q.Symbol, q.Bid, q.Ask, DateTime.UtcNow);
        }
        catch { /* never throw from event handler */ }
    }

    // -------------------------------------------------------------------
    // Orders (slave path)
    // -------------------------------------------------------------------
    public Task<OrderSendResult> SendOrderAsync(OrderSendRequest req, CancellationToken ct = default)
    {
        return Task.Run<OrderSendResult>(() =>
        {
            var api = _api;
            if (api is null)
                return new OrderSendResult(false, 0, 0, "Not connected");

            try
            {
                var orderType = MapOrderType(req.OrderType);
                var fillPolicy = ResolveFillPolicy(req.OrderType);
                var order = api.OrderSend(
                    req.Symbol,
                    req.Volume,
                    req.Price,
                    orderType,
                    req.StopLoss,
                    req.TakeProfit,
                    req.MaxSlippagePoints,
                    req.Comment,
                    0,
                    fillPolicy);

                return new OrderSendResult(true, (ulong)order.Ticket, order.OpenPrice, null);
            }
            catch (ServerException ex)
            {
                return new OrderSendResult(false, 0, 0, $"Server: {ex.Code} {ex.Message}");
            }
            catch (Exception ex)
            {
                return new OrderSendResult(false, 0, 0, ex.Message);
            }
        }, ct);
    }

    public Task<OrderSendResult> CloseOrderAsync(OrderCloseRequest req, CancellationToken ct = default)
    {
        return Task.Run<OrderSendResult>(() =>
        {
            var api = _api;
            if (api is null)
                return new OrderSendResult(false, 0, 0, "Not connected");

            try
            {
                var orderType = MapOrderType(req.OriginalOrderType);
                var order = api.OrderClose(
                    (long)req.Ticket,
                    req.Symbol,
                    req.Price,
                    req.Volume,
                    orderType,
                    req.MaxSlippagePoints);

                return new OrderSendResult(true, (ulong)order.Ticket, order.ClosePrice, null);
            }
            catch (ServerException ex)
            {
                return new OrderSendResult(false, 0, 0, $"Server: {ex.Code} {ex.Message}");
            }
            catch (Exception ex)
            {
                return new OrderSendResult(false, 0, 0, ex.Message);
            }
        }, ct);
    }

    public Task<OrderSendResult> ModifyOrderAsync(OrderModifyRequest req, CancellationToken ct = default)
    {
        return Task.Run<OrderSendResult>(() =>
        {
            var api = _api;
            if (api is null)
                return new OrderSendResult(false, 0, 0, "Not connected");

            try
            {
                // The DLL exposes a synchronous OrderModify. Signature:
                //   OrderModify(ticket, symbol, lots, price, type, sl, tp, ...) -> void
                // Throws ServerException on failure. We synthesize an OrderSendResult with
                // the requested ticket on success since modify doesn't return a new ticket.
                api.OrderModify(
                    (long)req.Ticket,
                    req.Symbol,
                    0,                              // lots: 0 = keep current
                    req.PendingPrice ?? 0,          // price: 0 = keep current (for non-pending)
                    OrderType.Buy,                  // type: ignored when modifying SL/TP only
                    req.StopLoss,
                    req.TakeProfit);
                return new OrderSendResult(true, req.Ticket, req.PendingPrice ?? 0, null);
            }
            catch (ServerException ex)
            {
                return new OrderSendResult(false, 0, 0, $"Server: {ex.Code} {ex.Message}");
            }
            catch (Exception ex)
            {
                return new OrderSendResult(false, 0, 0, ex.Message);
            }
        }, ct);
    }

    // -------------------------------------------------------------------
    // Live position + account stats snapshots
    // -------------------------------------------------------------------
    public Task<IReadOnlyList<OpenPosition>> GetOpenPositionsAsync(CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<OpenPosition>>(() =>
        {
            var api = _api;
            if (api is null) return Array.Empty<OpenPosition>();

            try
            {
                var raw = api.GetOpenedOrders();
                if (raw is null) return Array.Empty<OpenPosition>();

                var list = new List<OpenPosition>(raw.Length);
                foreach (var o in raw)
                {
                    // Map DLL OrderType -> our CopyOrderType
                    var ourType = MapToCopyType(o.OrderType);

                    // Current price: best effort from cached quotes. If unavailable
                    // (no subscription yet), fall back to OpenPrice so the column
                    // doesn't display 0.
                    var quote = GetQuote(o.Symbol);
                    double current = ourType switch
                    {
                        CopyOrderType.Buy  => quote?.Bid ?? o.OpenPrice,  // longs close at bid
                        CopyOrderType.Sell => quote?.Ask ?? o.OpenPrice,
                        _                  => o.OpenPrice
                    };

                    list.Add(new OpenPosition(
                        Ticket: (ulong)o.Ticket,
                        Symbol: o.Symbol,
                        OrderType: ourType,
                        Volume: o.Lots,
                        OpenPrice: o.OpenPrice,
                        CurrentPrice: current,
                        StopLoss: o.StopLoss,
                        TakeProfit: o.TakeProfit,
                        // Profit/Swap/Commission are standard MT5 position fields.
                        // The DLL exposes them on Order; we read defensively in case
                        // a particular build doesn't.
                        Profit: TryReadDouble(o, "Profit"),
                        Swap: TryReadDouble(o, "Swap"),
                        Commission: TryReadDouble(o, "Commission"),
                        OpenTimeUtc: o.OpenTime));
                }
                return list;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Account {Login}: could not fetch open positions.", _account.Login);
                return Array.Empty<OpenPosition>();
            }
        }, ct);
    }

    public Task<AccountStats?> GetAccountStatsAsync(CancellationToken ct = default)
    {
        return Task.Run<AccountStats?>(() =>
        {
            var api = _api;
            if (api is null) return null;

            try
            {
                double equity   = api.AccountEquity;
                double margin   = api.AccountMargin;
                double profit   = api.AccountProfit;
                double balance  = TryReadDouble(api.Account, "Balance");
                string currency = api.AccountCurrency ?? "";

                // Free margin and margin level are derived from equity/margin.
                // When margin is 0 (no positions), margin level is undefined; we
                // surface 0 and let the UI display "—" in that case.
                double freeMargin   = equity - margin;
                double marginLevel  = margin > 0 ? equity / margin * 100.0 : 0;

                return new AccountStats(
                    Balance: balance,
                    Equity: equity,
                    Margin: margin,
                    FreeMargin: freeMargin,
                    MarginLevelPercent: marginLevel,
                    Profit: profit,
                    Currency: currency);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Account {Login}: could not fetch account stats.", _account.Login);
                return null;
            }
        }, ct);
    }

    // ===================================================================
    // CLOSED-DEAL HISTORY
    // ===================================================================
    // Approach: the mtapi.mt5 DLL surface for history varies across builds.
    // We probe via reflection for any of several plausible method names,
    // call whichever exists, and translate the result into TradeRecord
    // objects. If no candidate is found we return empty — the UI degrades
    // gracefully ("trading days: 0", placeholders stay empty) rather than
    // crashing.
    //
    // The realized P&L per deal is what FTMO and similar firms care about.
    // MT5 records each closed trade as TWO deals (an entry and an exit);
    // we keep only the exit deals because their Profit field carries the
    // final realized number including swap and commission.

    public Task<IReadOnlyList<TradeRecord>> GetClosedDealsAsync(
        DateTime sinceUtc, CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<TradeRecord>>(() =>
        {
            // Check cache first. Same since-time + within TTL = serve cached.
            lock (_dealCacheLock)
            {
                if (_dealCachePayload is not null
                    && _dealCacheSinceUtc == sinceUtc
                    && DateTime.UtcNow - _dealCacheRetrievedAtUtc < DealCacheTtl)
                {
                    return _dealCachePayload;
                }
            }

            var api = _api;
            if (api is null) return Array.Empty<TradeRecord>();

            try
            {
                var deals = QueryHistoryDeals(api, sinceUtc);
                lock (_dealCacheLock)
                {
                    _dealCacheSinceUtc       = sinceUtc;
                    _dealCacheRetrievedAtUtc = DateTime.UtcNow;
                    _dealCachePayload        = deals;
                }
                return deals;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Account {Login}: history fetch threw; returning empty.", _account.Login);
                return Array.Empty<TradeRecord>();
            }
        }, ct);
    }

    /// <summary>One-time flag: have we written the MT5API surface dump
    /// to disk yet for this process? We do it once on the first history
    /// call so the file is available for diagnosis without spamming.</summary>
    private static int _surfaceDumped;

    /// <summary>
    /// Dump every public method + property on the MT5API class to a text
    /// file in the app's log folder. Lets the developer see exactly what
    /// the DLL exposes so we can wire the right method names without
    /// guessing. Runs ONCE per process.
    /// </summary>
    private static void DumpMt5ApiSurfaceOnce(MT5API api, ILogger logger)
    {
        if (System.Threading.Interlocked.Exchange(ref _surfaceDumped, 1) != 0) return;
        try
        {
            var folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LimitlessTradeCopier", "logs");
            System.IO.Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, "mt5api-surface.txt");

            var t = api.GetType();
            using var sw = new System.IO.StreamWriter(path, append: false);
            sw.WriteLine($"# MT5API surface dump — {DateTime.UtcNow:O}");
            sw.WriteLine($"# Type: {t.AssemblyQualifiedName}");
            sw.WriteLine();
            sw.WriteLine("## Methods");
            foreach (var m in t.GetMethods()
                .Where(mi => mi.DeclaringType == t && !mi.IsSpecialName)
                .OrderBy(mi => mi.Name))
            {
                var ps = string.Join(", ", m.GetParameters()
                    .Select(p => $"{p.ParameterType.Name} {p.Name}"));
                sw.WriteLine($"  {m.ReturnType.Name} {m.Name}({ps})");
            }
            sw.WriteLine();
            sw.WriteLine("## Properties");
            foreach (var p in t.GetProperties()
                .Where(pi => pi.DeclaringType == t)
                .OrderBy(pi => pi.Name))
            {
                sw.WriteLine($"  {p.PropertyType.Name} {p.Name}");
            }
            logger.LogInformation("MT5API surface written to {Path}", path);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not dump MT5API surface.");
        }
    }

    /// <summary>
    /// Pull closed-deal history from the DLL. Strategy:
    ///
    ///   1. First call ever: write a dump of every public method + property
    ///      on MT5API to mt5api-surface.txt. This is the diagnostic for
    ///      "what does the DLL actually expose?" — read it once and pick
    ///      the right method name.
    ///
    ///   2. Try a generous list of common history-method names, with three
    ///      parameter variants:
    ///        - (DateTime, DateTime)  — most common
    ///        - (DateTime)            — "since" only
    ///        - ()                    — all history
    ///
    ///   3. Once a method returns data, dump the field names of its first
    ///      row to the log so we know what to map.
    ///
    ///   4. Map fields permissively (multiple name candidates per field).
    /// </summary>
    private int _historyPrimed;

    /// <summary>
    /// MT5's `ClosedOrders()` returns events the terminal has observed live
    /// since this session started — NOT historical data. To get the actual
    /// historical deal list we use `DownloadOrderHistory(from, to, sort, asc)`
    /// which returns `OrderHistoryEventArgs`, a wrapper object that contains
    /// the array of Orders as a property.
    ///
    /// We reflect into the returned event-args, find the FIRST Array-typed
    /// public property/field, and treat that as our deal list. This works
    /// without us hard-binding to OrderHistoryEventArgs (which lives in the
    /// mtapi.mt5 namespace and can change between builds).
    ///
    /// Returns null if anything fails — the caller falls back to
    /// `ClosedOrders()` and the rest of the probe chain.
    /// </summary>
    private Array? DownloadHistoryViaReflection(MT5API api, DateTime fromUtc)
    {
        try
        {
            var apiType = api.GetType();
            var methods = apiType.GetMethods()
                .Where(m => m.Name == "DownloadOrderHistory" && m.GetParameters().Length == 4)
                .ToArray();
            if (methods.Length == 0) return null;

            var method = methods[0];
            var ps = method.GetParameters();
            var sortType = ps[2].ParameterType;
            // Default-zero enum value — typically "None" or natural first ordinal.
            var sortDefault = Enum.ToObject(sortType, 0);
            var until = DateTime.UtcNow.AddDays(1);

            // Only LOG the prime on the first attempt — the call itself is
            // cheap on subsequent invocations because the terminal caches.
            var firstCall = System.Threading.Interlocked.CompareExchange(ref _historyPrimed, 1, 0) == 0;
            if (firstCall)
            {
                _logger.LogInformation(
                    "Account {Login}: pulling history via DownloadOrderHistory from {From:yyyy-MM-dd} to {Until:yyyy-MM-dd}.",
                    _account.Login, fromUtc, until);
            }

            var result = method.Invoke(api, new object[] { fromUtc, until, sortDefault, true });
            if (result is null) return null;

            // Dump the wrapper's shape on first call so we can see what's inside
            var resultType = result.GetType();
            if (firstCall)
            {
                var props = string.Join(", ", resultType.GetProperties().Select(p => $"{p.PropertyType.Name} {p.Name}"));
                var fields = string.Join(", ", resultType.GetFields().Select(f => $"{f.FieldType.Name} {f.Name}"));
                _logger.LogInformation(
                    "Account {Login}: DownloadOrderHistory returned {Type}. Properties: [{Props}]. Fields: [{Fields}].",
                    _account.Login, resultType.Name, props, fields);
            }

            // Look for a collection-typed property/field on the event-args
            // wrapper. mtapi.mt5 5.3677.x uses List<Order> (not Array), and
            // other builds may use Array or IEnumerable<Order>. We accept
            // any IEnumerable and materialize to Array for downstream code.
            string[] preferredNames = { "Orders", "History", "OrdersHistory", "DealsHistory", "Deals", "Items" };

            foreach (var name in preferredNames)
            {
                var pi = resultType.GetProperty(name);
                if (pi is not null)
                {
                    var got = TryReadCollection(pi.GetValue(result));
                    if (got is not null)
                    {
                        if (firstCall)
                            _logger.LogInformation("Account {Login}: history collection found on property '{Name}' (length {Len}).",
                                _account.Login, name, got.Length);
                        return got;
                    }
                }
                var fi = resultType.GetField(name);
                if (fi is not null)
                {
                    var got = TryReadCollection(fi.GetValue(result));
                    if (got is not null)
                    {
                        if (firstCall)
                            _logger.LogInformation("Account {Login}: history collection found on field '{Name}' (length {Len}).",
                                _account.Login, name, got.Length);
                        return got;
                    }
                }
            }

            // Fallback: scan ALL members for the first collection-typed one
            foreach (var pi in resultType.GetProperties())
            {
                try
                {
                    var got = TryReadCollection(pi.GetValue(result));
                    if (got is not null)
                    {
                        if (firstCall)
                            _logger.LogInformation("Account {Login}: history collection found on (fallback) property '{Name}' (length {Len}).",
                                _account.Login, pi.Name, got.Length);
                        return got;
                    }
                }
                catch { /* tolerate */ }
            }
            foreach (var fi in resultType.GetFields())
            {
                try
                {
                    var got = TryReadCollection(fi.GetValue(result));
                    if (got is not null)
                    {
                        if (firstCall)
                            _logger.LogInformation("Account {Login}: history collection found on (fallback) field '{Name}' (length {Len}).",
                                _account.Login, fi.Name, got.Length);
                        return got;
                    }
                }
                catch { /* tolerate */ }
            }

            if (firstCall)
                _logger.LogWarning(
                    "Account {Login}: DownloadOrderHistory returned {Type} but no usable collection property/field was found inside it.",
                    _account.Login, resultType.Name);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Account {Login}: DownloadOrderHistory threw.",
                _account.Login);
            // Allow retry on next call
            System.Threading.Interlocked.Exchange(ref _historyPrimed, 0);
            return null;
        }
    }

    /// <summary>
    /// Accept an Array, an IList (e.g. List&lt;Order&gt;), or any other
    /// IEnumerable, and materialize it into an object[] that the rest of
    /// the row-mapping pipeline can iterate over. Returns null for strings
    /// (which are technically IEnumerable but never what we want here),
    /// for empty/skipped values, or for value types like int/Bool that
    /// shouldn't be treated as collections.
    /// </summary>
    private static Array? TryReadCollection(object? value)
    {
        if (value is null) return null;
        // Strings are IEnumerable<char> — never what we want
        if (value is string) return null;
        // Primitives + value types that aren't collections
        var vt = value.GetType();
        if (vt.IsPrimitive || vt == typeof(decimal) || vt == typeof(DateTime)) return null;

        // Already an Array
        if (value is Array a) return a;

        // IList path — most efficient for List<T>
        if (value is System.Collections.IList list)
        {
            var arr = new object?[list.Count];
            for (int i = 0; i < list.Count; i++) arr[i] = list[i];
            return arr;
        }

        // Generic IEnumerable path
        if (value is System.Collections.IEnumerable enumerable)
        {
            var buf = new System.Collections.Generic.List<object?>();
            foreach (var item in enumerable) buf.Add(item);
            return buf.ToArray();
        }

        return null;
    }

    private List<TradeRecord> QueryHistoryDeals(MT5API api, DateTime sinceUtc)
    {
        DumpMt5ApiSurfaceOnce(api, _logger);

        // PRIMARY PATH: DownloadOrderHistory returns the actual historical
        // deal array. ClosedOrders() (the fallback below) only returns
        // events the terminal has observed live this session, which is
        // usually empty when the app starts.
        var primary = DownloadHistoryViaReflection(api, sinceUtc);
        if (primary is not null && primary.Length > 0)
        {
            return MapRowsToTradeRecords(primary, "DownloadOrderHistory");
        }

        var until = DateTime.UtcNow.AddDays(1);   // pad future for clock skew
        var apiType = api.GetType();

        // Wide net of method-name candidates. Order matters: more specific
        // / more-likely-to-exist first.
        //
        // ClosedOrders() is the confirmed method on mtapi.mt5 5.3677.x:
        // returns Order[] with no arguments, full account history.
        // The rest stay as fallbacks for other DLL builds.
        var candidateNames = new[]
        {
            "ClosedOrders",
            "GetOrdersHistory",
            "GetHistoryOrders",
            "GetDealsHistory",
            "GetHistoryDeals",
            "GetClosedOrders",
            "GetClosedDeals",
            "OrderHistory",
            "OrdersHistory",
            "HistoryOrders",
            "HistoryDeals",
            "History",
            "GetHistoricalOrders",
            "GetHistoricalDeals",
        };

        Array? raw = null;
        string? usedMethod = null;
        string? usedSignature = null;

        // Three parameter-shape probes per name, in order of likelihood.
        var shapes = new (Type[] types, object[] args)[]
        {
            (new[] { typeof(DateTime), typeof(DateTime) }, new object[] { sinceUtc, until }),
            (new[] { typeof(DateTime) },                   new object[] { sinceUtc }),
            (Array.Empty<Type>(),                          Array.Empty<object>()),
        };

        foreach (var name in candidateNames)
        {
            foreach (var (types, args) in shapes)
            {
                var m = apiType.GetMethod(name, types);
                if (m is null) continue;
                try
                {
                    var result = m.Invoke(api, args);
                    if (result is Array a && a.Length > 0)
                    {
                        raw = a;
                        usedMethod = name;
                        usedSignature = types.Length == 0 ? "()"
                            : "(" + string.Join(", ", types.Select(tp => tp.Name)) + ")";
                        break;
                    }
                    // empty array still counts as "found", but keep looking
                    // through other signatures in case one returns rows.
                    if (result is Array && raw is null)
                    {
                        raw = result as Array;
                        usedMethod = name;
                        usedSignature = types.Length == 0 ? "()"
                            : "(" + string.Join(", ", types.Select(tp => tp.Name)) + ")";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "Account {Login}: history probe {Name}{Sig} threw; trying next.",
                        _account.Login, name,
                        types.Length == 0 ? "()" : "(" + string.Join(", ", types.Select(tp => tp.Name)) + ")");
                }
            }
            if (raw is not null && raw.Length > 0) break;
        }

        if (raw is null)
        {
            _logger.LogWarning(
                "Account {Login}: no history method found on the MT5API surface. " +
                "Check %LOCALAPPDATA%\\LimitlessTradeCopier\\logs\\mt5api-surface.txt " +
                "and paste any history-looking method names into Limitless support.",
                _account.Login);
            return new List<TradeRecord>();
        }
        if (raw.Length == 0)
        {
            _logger.LogInformation(
                "Account {Login}: history method {Name}{Sig} returned 0 rows (account may have no trades since {Since:yyyy-MM-dd}).",
                _account.Login, usedMethod, usedSignature, sinceUtc);
            return new List<TradeRecord>();
        }

        return MapRowsToTradeRecords(raw, usedMethod ?? "(unknown)");
    }

    /// <summary>
    /// Map a broker-supplied array of Order/Deal objects to our TradeRecord
    /// shape. Permissive about field names — different DLL builds expose
    /// slightly different property names so we try several candidates per
    /// field. Skips rows that are clearly invalid (all-zero placeholders).
    /// </summary>
    private List<TradeRecord> MapRowsToTradeRecords(Array raw, string sourceLabel)
    {
        DumpFirstRowFieldsOnce(raw, sourceLabel);

        var result_list = new List<TradeRecord>(raw.Length);
        foreach (var item in raw)
        {
            if (item is null) continue;
            try
            {
                var symbol = ReadFirstString(item, "Symbol", "SymbolName") ?? "";
                var profit = ReadFirstDouble(item, "Profit", "ProfitLoss", "PnL");
                var volume = ReadFirstDouble(item, "Lots", "Volume", "VolumeClosed", "VolumeInitial");
                var closeTime = ReadFirstDateTime(item,
                    "CloseTime", "CloseTimeUtc", "Time", "TimeUtc",
                    "TimeClose", "ClosedAt", "ExecutionTime");

                var typeRaw = ReadFirstInt(item,
                    "OrderType", "Type", "Direction", "DealType", "Side");
                var direction = typeRaw switch
                {
                    0 => "BUY",
                    1 => "SELL",
                    _ => ""
                };

                // Conservatively skip rows that are clearly empty/invalid:
                // zero volume AND zero profit AND no close time. A real
                // trade always has at least one of these populated.
                if (profit == 0 && volume == 0 && closeTime == default) continue;

                result_list.Add(new TradeRecord
                {
                    Symbol      = symbol,
                    Direction   = direction,
                    Volume      = volume,
                    Profit      = (decimal)profit,
                    ClosedAtUtc = closeTime,
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Account {Login}: skipping a history row that didn't deserialize cleanly.",
                    _account.Login);
            }
        }

        _logger.LogInformation(
            "Account {Login}: {Source} returned {Total} rows, kept {Kept} after entry-leg filter.",
            _account.Login, sourceLabel, raw.Length, result_list.Count);

        return result_list;
    }

    private int _firstRowDumped;
    private void DumpFirstRowFieldsOnce(Array rows, string? usedMethod)
    {
        if (System.Threading.Interlocked.Exchange(ref _firstRowDumped, 1) != 0) return;
        try
        {
            if (rows.Length == 0) return;
            var first = rows.GetValue(0);
            if (first is null) return;
            var t = first.GetType();
            var props = string.Join(", ", t.GetProperties()
                .Select(p => $"{p.PropertyType.Name} {p.Name}"));
            var fields = string.Join(", ", t.GetFields()
                .Select(f => $"{f.FieldType.Name} {f.Name}"));
            _logger.LogInformation(
                "Account {Login}: history rows from {Method} have type {TypeName}. " +
                "Properties: [{Props}]. Fields: [{Fields}].",
                _account.Login, usedMethod, t.Name, props, fields);
        }
        catch { /* tolerate */ }
    }

    private static string? ReadFirstString(object obj, params string[] names)
    {
        var t = obj.GetType();
        foreach (var name in names)
        {
            try
            {
                var p = t.GetProperty(name);
                if (p?.GetValue(obj) is object v) return v.ToString();
                var f = t.GetField(name);
                if (f?.GetValue(obj) is object fv) return fv.ToString();
            }
            catch { /* next */ }
        }
        return null;
    }

    private static int ReadFirstInt(object obj, params string[] names)
    {
        var t = obj.GetType();
        foreach (var name in names)
        {
            try
            {
                var p = t.GetProperty(name);
                if (p?.GetValue(obj) is object v)
                    return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { /* next */ }
        }
        return -1;
    }

    private static DateTime ReadFirstDateTime(object obj, params string[] names)
    {
        var t = obj.GetType();
        foreach (var name in names)
        {
            try
            {
                var p = t.GetProperty(name);
                if (p?.GetValue(obj) is DateTime dt && dt != default)
                {
                    return dt.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                        : dt.ToUniversalTime();
                }
            }
            catch { /* next */ }
        }
        return default;   // caller can detect by checking == default(DateTime)
    }

    /// <summary>
    /// Look up volume/digits metadata for a symbol on this broker. The first
    /// call for a given symbol asks the DLL's Symbols catalog; subsequent
    /// calls hit the in-memory cache (broker symbol metadata is static for a
    /// connected session). Returns null if the symbol isn't in the catalog
    /// — the routing engine treats null as "skip and log a clear error" so
    /// the user knows why the trade didn't open.
    /// </summary>
    public SymbolMetadata? GetSymbolMetadata(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return null;

        // Cache is keyed case-insensitively. We store nulls too (negative cache)
        // so a missing symbol doesn't repeatedly hit the DLL on every trade.
        if (_symbolMeta.TryGetValue(symbol, out var cached)) return cached;

        var api = _api;
        if (api is null) return null;

        SymbolMetadata? result = null;
        try
        {
            // The DLL surface is api.Symbols.Infos[symbol], where each value
            // exposes VolumeStep / MinVolume / MaxVolume / Digits as doubles.
            // We use reflection because the exact runtime type isn't a public
            // contract we want to compile against — different mtapi builds
            // sometimes rename properties (LotsStep vs VolumeStep, MinLots vs
            // MinVolume etc.). The reflection block tolerates both.
            object? infos = api.Symbols?.Infos;
            if (infos is System.Collections.IDictionary dict && dict.Contains(symbol))
            {
                object? sym = dict[symbol];
                if (sym is not null)
                {
                    double step  = ReadFirstDouble(sym, "VolumeStep", "LotsStep", "Step");
                    double mn    = ReadFirstDouble(sym, "MinVolume", "MinLots", "Min");
                    double mx    = ReadFirstDouble(sym, "MaxVolume", "MaxLots", "Max");
                    int digits   = (int)ReadFirstDouble(sym, "Digits");

                    // Sane defaults if the broker reports zero/negative values
                    // (some demo brokers do): 0.01 step, 0.01 min, 100 max are
                    // FX-typical and let the trade through rather than block it.
                    if (step <= 0) step = 0.01;
                    if (mn   <= 0) mn   = step;
                    if (mx   <= 0) mx   = 100.0;
                    if (digits < 0) digits = 5;

                    result = new SymbolMetadata(symbol, step, mn, mx, digits);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Account {Login}: could not read symbol metadata for {Symbol}.",
                _account.Login, symbol);
        }

        _symbolMeta[symbol] = result;
        return result;
    }

    /// <summary>Try a list of property names in order; first one that returns
    /// a finite double wins. Returns 0 if none matched.</summary>
    private static double ReadFirstDouble(object obj, params string[] names)
    {
        var t = obj.GetType();
        foreach (var name in names)
        {
            var p = t.GetProperty(name);
            if (p is null) continue;
            try
            {
                var v = p.GetValue(obj);
                if (v is null) continue;
                double d = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
                if (!double.IsNaN(d) && !double.IsInfinity(d)) return d;
            }
            catch { /* try next name */ }
        }
        return 0;
    }

    /// <summary>
    /// Defensive reflection-based double accessor. The DLL surface exposes Profit/
    /// Swap/Commission as standard fields, but a particular build might not have
    /// them; rather than refusing to load a position at all we return 0.
    /// </summary>
    private static double TryReadDouble(object obj, string name)
    {
        if (obj is null) return 0;
        try
        {
            var t = obj.GetType();
            var p = t.GetProperty(name);
            if (p is not null) { var v = p.GetValue(obj); return v is null ? 0 : Convert.ToDouble(v); }
            var f = t.GetField(name);
            if (f is not null) { var v = f.GetValue(obj); return v is null ? 0 : Convert.ToDouble(v); }
            return 0;
        }
        catch { return 0; }
    }

    /// <summary>Map DLL OrderType enum to our CopyOrderType. Inverse of MapOrderType.</summary>
    private static CopyOrderType MapToCopyType(OrderType t) => t switch
    {
        OrderType.Buy            => CopyOrderType.Buy,
        OrderType.Sell           => CopyOrderType.Sell,
        OrderType.BuyLimit       => CopyOrderType.BuyLimit,
        OrderType.SellLimit      => CopyOrderType.SellLimit,
        OrderType.BuyStop        => CopyOrderType.BuyStop,
        OrderType.SellStop       => CopyOrderType.SellStop,
        _                        => CopyOrderType.Buy,
    };

    // -------------------------------------------------------------------
    // OrderUpdate -> MasterOrderEvent (master path)
    // -------------------------------------------------------------------
    private void HandleOrderUpdate(MT5API api, OrderUpdate update)
    {
        // CRITICAL: This is the entry point of the hot path. Capture the timestamp
        // immediately so latency math measures from the event's arrival, not from
        // whenever the routing engine gets around to processing it.
        var receivedAt = LatencyClock.Now();

        try
        {
            var order = update.Order;
            if (order is null) return;

            MasterEventKind kind;
            switch (update.Type)
            {
                case UpdateType.MarketOpen:    kind = MasterEventKind.MarketOpen; break;
                case UpdateType.MarketClose:   kind = MasterEventKind.MarketClose; break;
                case UpdateType.MarketModify:  kind = MasterEventKind.Modify; break;
                case UpdateType.PendingOpen:   kind = MasterEventKind.PendingPlace; break;
                case UpdateType.PendingClose:  kind = MasterEventKind.PendingCancel; break;
                case UpdateType.PendingModify: kind = MasterEventKind.Modify; break;
                case UpdateType.OnStopLoss:
                case UpdateType.OnTakeProfit:  kind = MasterEventKind.MarketClose; break;
                default:
                    // We ignore other update types for now (deposits, balance ops, etc).
                    return;
            }

            // CRITICAL for Prop Journal correctness:
            // When a trade closes, invalidate the deal-history cache so the
            // freshly-closed trade is visible on the very next GetClosedDealsAsync
            // call instead of waiting up to 30 seconds (the cache TTL).
            //
            // Without this, the Prop Journal profit-target meter momentarily
            // shows 0% when a trade closes: realized hasn't been picked up yet,
            // and floating already dropped to 0. The user perceives this as
            // "the target reset from $8,000 back to zero".
            if (kind is MasterEventKind.MarketClose
                or MasterEventKind.PartialClose)
            {
                lock (_dealCacheLock)
                {
                    _dealCachePayload = null;
                    _dealCacheRetrievedAtUtc = default;
                }
            }

            var ev = new MasterOrderEvent(
                MasterAccountId: _account.Id,
                Kind: kind,
                Ticket: (ulong)order.Ticket,
                Symbol: order.Symbol ?? string.Empty,
                OrderType: MapBackOrderType(order.OrderType),
                Volume: order.Lots,
                Price: kind == MasterEventKind.MarketClose ? order.ClosePrice : order.OpenPrice,
                StopLoss: order.StopLoss,
                TakeProfit: order.TakeProfit,
                ServerTimeUtc: ResolveServerTime(order, kind),
                ReceivedAtTicks: receivedAt);

            OrderUpdate?.Invoke(this, ev);
        }
        catch (Exception ex)
        {
            // NEVER throw out of an event handler — the DLL doesn't expect it.
            _logger.LogError(ex, "Account {Login}: a trade event arrived from the broker but we could not process it.", _account.Login);
        }
    }

    // -------------------------------------------------------------------
    // Mapping helpers (DLL types <-> our types)
    // -------------------------------------------------------------------
    private static OrderType MapOrderType(CopyOrderType t) => t switch
    {
        CopyOrderType.Buy           => OrderType.Buy,
        CopyOrderType.Sell          => OrderType.Sell,
        CopyOrderType.BuyLimit      => OrderType.BuyLimit,
        CopyOrderType.SellLimit     => OrderType.SellLimit,
        CopyOrderType.BuyStop       => OrderType.BuyStop,
        CopyOrderType.SellStop      => OrderType.SellStop,
        CopyOrderType.BuyStopLimit  => OrderType.BuyStopLimit,
        CopyOrderType.SellStopLimit => OrderType.SellStopLimit,
        _ => OrderType.Buy
    };

    private static CopyOrderType MapBackOrderType(OrderType t) => t switch
    {
        OrderType.Buy           => CopyOrderType.Buy,
        OrderType.Sell          => CopyOrderType.Sell,
        OrderType.BuyLimit      => CopyOrderType.BuyLimit,
        OrderType.SellLimit     => CopyOrderType.SellLimit,
        OrderType.BuyStop       => CopyOrderType.BuyStop,
        OrderType.SellStop      => CopyOrderType.SellStop,
        OrderType.BuyStopLimit  => CopyOrderType.BuyStopLimit,
        OrderType.SellStopLimit => CopyOrderType.SellStopLimit,
        _ => CopyOrderType.Buy
    };

    private static FillPolicy ResolveFillPolicy(CopyOrderType t)
    {
        // For market orders, IOC is the safe universal choice. For pending orders
        // brokers usually require Return/FlashFill. We use Any so the DLL picks
        // a permissible policy for the symbol.
        return t is CopyOrderType.Buy or CopyOrderType.Sell
            ? FillPolicy.ImmediateOrCancel
            : FillPolicy.Any;
    }

    private static DateTime ResolveServerTime(Order order, MasterEventKind kind)
    {
        // Order.OpenTime / CloseTime are exposed as DateTime by the DLL.
        // Default to UtcNow if the broker hasn't populated them yet (fresh open events
        // sometimes arrive before close-time is meaningful).
        try
        {
            var t = kind == MasterEventKind.MarketClose ? order.CloseTime : order.OpenTime;
            return t == default ? DateTime.UtcNow : t;
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private void SetStatus(ConnectionStatus newStatus)
    {
        ConnectionStatus old;
        lock (_statusLock)
        {
            if (_status == newStatus) return;
            old = _status;
            _status = newStatus;
        }
        try { StatusChanged?.Invoke(this, newStatus); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Account {Login}: an error happened while telling the rest of the app about a status change ({Old} → {New}).",
                _account.Login, old, newStatus);
        }
    }
}
