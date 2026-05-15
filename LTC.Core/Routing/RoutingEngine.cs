using LTC.Core.Connections;
using LTC.Core.Diagnostics;
using LTC.Core.Logging;
using LTC.Core.Models;
using LTC.Core.Risk;
using LTC.Core.Symbols;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTC.Core.Routing;

/// <summary>
/// The brain. Consumes <see cref="MasterOrderEvent"/>s from the bus, looks up which
/// slaves are subscribed to the originating master, applies sizing+filters per link,
/// and fires the slave order via the connection manager.
/// </summary>
/// <remarks>
/// The hot path between EventBus.Handle -> slave.SendOrderAsync is allocation-light
/// and lock-free. Latency is measured with <see cref="LatencyClock"/> using the
/// <c>ReceivedAtTicks</c> stamped at the moment the connection's OrderUpdate fired.
/// </remarks>
public sealed class RoutingEngine
{
    private readonly ConnectionManager _connections;
    private readonly CopySubscriptionRegistry _subscriptions;
    private readonly ISymbolMapper _symbolMapper;
    private readonly IActivityLog _activity;
    private readonly ILogger _logger;

    /// <summary>Map of (masterTicket, slaveAccountId) -> slaveTicket, for routing close events.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong masterTicket, Guid slaveId), ulong> _slaveTickets = new();

    /// <summary>
    /// Map of (masterTicket, slaveAccountId) -> slave volume that was sent at open.
    /// Used by close path so we never depend on the broker's post-close order
    /// snapshot (which often reports Lots=0). When the close event arrives, we
    /// look up the original volume and pass it to the slave's CloseOrder call.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong masterTicket, Guid slaveId), double> _openVolumes = new();

    /// <summary>
    /// Modify events that arrived BEFORE the slave open completed. The slave
    /// ticket isn't known yet so we can't fire the modify; instead we stash
    /// the latest SL/TP per (masterTicket, slaveAccountId) and replay it as
    /// soon as the open succeeds. Latest-wins semantics: if 3 modifies arrive
    /// during the open window we only need the final state.
    ///
    /// This fixes the very common race where a trader opens a market order
    /// with SL/TP attached: many MT5 brokers fire MarketOpen first (SL/TP=0)
    /// and then MarketModify with the real SL/TP a few hundred milliseconds
    /// later. Without buffering, the modify lookup misses (slave ticket not
    /// yet populated) and the slave silently runs without protection.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong masterTicket, Guid slaveId), (double sl, double tp)> _pendingModifies = new();

    /// <summary>
    /// Latest known account snapshot per account (balance + equity). Populated by
    /// <see cref="UpdateAccountSnapshot"/>, which the engine wires up to the
    /// <c>AccountStatsPoller</c>. Risk modes that scale by balance or equity
    /// (RiskPercent, EquityRatio, BalanceRatio) read from here at open time.
    ///
    /// If a snapshot hasn't arrived yet (account just connected), we fall back
    /// to a (0,0) snapshot — those modes will skip the open with a clear error
    /// rather than placing a wrong-sized trade.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, AccountSnapshot> _snapshots = new();

    /// <summary>Called by CopierEngine when the AccountStatsPoller produces fresh data.</summary>
    public void UpdateAccountSnapshot(Guid accountId, double balance, double equity)
    {
        _snapshots[accountId] = new AccountSnapshot(balance, equity);
    }

    private AccountSnapshot SnapshotFor(Guid accountId) =>
        _snapshots.TryGetValue(accountId, out var s) ? s : new AccountSnapshot(0, 0);

    /// <summary>Per-link rolling counters: trades attempted/succeeded/skipped/failed
    /// since this engine instance started. Resets on app restart, which is fine for
    /// the "today" stats UI since users typically restart the app daily.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, LinkCounters> _linkCounters = new();

    /// <summary>Raised whenever a link's counters change. UI subscribes to update
    /// the per-link stats badge.</summary>
    public event EventHandler<LinkCountersSnapshot>? LinkCountersChanged;

    public LinkCounters GetCounters(Guid linkId) =>
        _linkCounters.TryGetValue(linkId, out var c) ? c : new LinkCounters();

    public RoutingEngine(
        ConnectionManager connections,
        CopySubscriptionRegistry subscriptions,
        ISymbolMapper symbolMapper,
        IActivityLog activity,
        ILogger? logger = null)
    {
        _connections = connections;
        _subscriptions = subscriptions;
        _symbolMapper = symbolMapper;
        _activity = activity;
        _logger = logger ?? NullLogger.Instance;

        // Per-link counter tracking. EntryChanged fires on BOTH initial Append
        // (status=InFlight) and any subsequent Update (status=Success/Failed/Skipped).
        // We bump counters only on terminal states so we count each event once.
        _activity.EntryChanged += (_, e) => UpdateCountersFor(e);
    }

    /// <summary>
    /// Increment per-link counters based on an activity entry. Looks up the
    /// link by master/slave id pair and updates its rolling counter.
    /// </summary>
    private void UpdateCountersFor(ActivityEntry entry)
    {
        // We only care about terminal states — InFlight is transient.
        if (entry.Status == ActivityStatus.InFlight) return;
        if (entry.MasterAccountId is not Guid masterId) return;
        if (entry.SlaveAccountId  is not Guid slaveId) return;

        // Find the link this activity belongs to.
        var links = _subscriptions.LinksForMaster(masterId);
        Guid? linkId = null;
        foreach (var l in links)
        {
            if (l.SlaveAccountId == slaveId) { linkId = l.Id; break; }
        }
        if (linkId is null) return;

        var counters = _linkCounters.AddOrUpdate(linkId.Value,
            _ => StartCounter(entry.Status),
            (_, prev) => prev with
            {
                Total      = prev.Total + 1,
                Successful = prev.Successful + (entry.Status == ActivityStatus.Success ? 1 : 0),
                Skipped    = prev.Skipped    + (entry.Status == ActivityStatus.Skipped ? 1 : 0),
                Failed     = prev.Failed     + (entry.Status == ActivityStatus.Failed  ? 1 : 0),
            });

        LinkCountersChanged?.Invoke(this, new LinkCountersSnapshot(linkId.Value, counters));
    }

    private static LinkCounters StartCounter(ActivityStatus status) => new LinkCounters(
        Total:      1,
        Successful: status == ActivityStatus.Success ? 1 : 0,
        Skipped:    status == ActivityStatus.Skipped ? 1 : 0,
        Failed:     status == ActivityStatus.Failed  ? 1 : 0);

    /// <summary>
    /// Hot-path entry point. Called by the EventBus dispatcher for every master event.
    /// Returns immediately; slave orders are dispatched fire-and-forget.
    /// </summary>
    public ValueTask HandleMasterEventAsync(MasterOrderEvent ev)
    {
        // First lookup is lock-free over the immutable map.
        var links = _subscriptions.LinksForMaster(ev.MasterAccountId);
        if (links.Length == 0) return default;

        var masterConn = _connections.Get(ev.MasterAccountId);

        // Iterate slaves and fire each one. Each is a fire-and-forget Task — we
        // never await them in this method so a slow broker on one slave can't
        // block another.
        for (int i = 0; i < links.Length; i++)
        {
            var link = links[i];
            DispatchToSlave(ev, link, masterConn);
        }

        return default;
    }

    private void DispatchToSlave(MasterOrderEvent ev, CopyLink link, IBrokerConnection? masterConn)
    {
        // ---------- 1. Resolve slave connection ----------
        var slave = _connections.Get(link.SlaveAccountId);
        if (slave is null || slave.Status != ConnectionStatus.Connected)
        {
            LogActivity(ev, link, ActivityKind.Filtered, ActivityStatus.Skipped,
                error: "slave not connected");
            return;
        }

        // ---------- 2. Translate symbol ----------
        // Bidirectional mapper: strip the source (master) account's affixes from
        // the incoming symbol, then apply the target (slave) account's affixes.
        // Catalog from the slave verifies the result actually exists.
        var slaveAccount = slave.Account;
        var masterAccount = masterConn?.Account;
        var slaveSymbol = _symbolMapper.Resolve(
            sourceSymbol:  ev.Symbol,
            sourcePrefix:  masterAccount?.SymbolPrefix,
            sourceSuffix:  masterAccount?.SymbolSuffix,
            targetPrefix:  slaveAccount.SymbolPrefix,
            targetSuffix:  slaveAccount.SymbolSuffix,
            targetCatalog: slave.AvailableSymbols);
        if (string.IsNullOrEmpty(slaveSymbol))
        {
            LogActivity(ev, link, ActivityKind.Filtered, ActivityStatus.Skipped,
                error: $"no symbol mapping for {ev.Symbol}");
            return;
        }

        // ---------- 3. Apply reverse-copy ----------
        var slaveOrderType = RiskEngine.ApplyReverse(ev.OrderType, link.ReverseCopy);
        var direction = RiskEngine.DirectionOf(slaveOrderType);

        // ---------- 4. Pending-order filter ----------
        bool isPending = slaveOrderType is not (CopyOrderType.Buy or CopyOrderType.Sell);
        if (isPending && !link.CopyPending)
        {
            LogActivity(ev, link, ActivityKind.Filtered, ActivityStatus.Skipped,
                error: "pending orders disabled");
            return;
        }

        // ---------- 5. Build entry & dispatch by event kind ----------
        // Volume is only computed for opens. Closes use the slave's own position
        // volume (looked up via _slaveTickets in DispatchClose). Modifies don't
        // touch volume at all. This is critical: opens with 0 volume should be
        // skipped, but closes must NEVER be skipped just because the placeholder
        // risk math returns 0.
        switch (ev.Kind)
        {
            case MasterEventKind.MarketOpen:
            case MasterEventKind.PendingPlace:
            case MasterEventKind.PendingFilled:
            {
                // Look up the latest balance/equity for both master and slave.
                // Populated by AccountStatsPoller -> CopierEngine -> RoutingEngine.
                // For Fixed/Multiplier modes these are unused; for ratio modes
                // they're authoritative.
                var masterSnap = SnapshotFor(ev.MasterAccountId);
                var slaveSnap  = SnapshotFor(link.SlaveAccountId);
                double slaveVolume = RiskEngine.ComputeSlaveVolume(
                    link.LotSizing, ev.Volume, masterSnap, slaveSnap, new SlTpContext(0, 0));

                if (slaveVolume <= 0)
                {
                    // Be specific in the error so the user knows whether it's a
                    // risk-math issue (waiting for stats) vs floor clamp.
                    var reason = (link.LotSizing.Mode != LotSizingMode.Fixed
                                  && link.LotSizing.Mode != LotSizingMode.Multiplier
                                  && (slaveSnap.Balance == 0 || slaveSnap.Equity == 0))
                        ? "waiting for slave account balance/equity (stats poll hasn't arrived yet)"
                        : "computed lot size is below broker minimum";
                    LogActivity(ev, link, ActivityKind.Filtered, ActivityStatus.Skipped,
                        error: reason);
                    return;
                }

                // ---------- Round to broker's volume step ----------
                // The risk engine produces a real-valued lot size like 0.0824.
                // Brokers reject any volume that isn't an exact multiple of the
                // symbol's VolumeStep (typically 0.01 on FX, 0.10 on indices,
                // 1.0 on some crypto). MT5's error is "invalid_volume".
                //
                // Strategy:
                //   1. Look up the slave's symbol metadata.
                //   2. Round DOWN to the nearest step (so we never exceed what
                //      the user's risk math computed).
                //   3. If the rounded result is below MinVolume, clamp UP to
                //      MinVolume — that's standard behaviour: a 0.005 desired
                //      lot on a min-0.01 broker becomes 0.01. Skipping the
                //      trade entirely would be more frustrating than a small
                //      over-size on a tiny account.
                //   4. If above MaxVolume, clamp down.
                //   5. If the symbol metadata is missing, fall back to the
                //      0.01 default (covers most FX brokers safely).
                var meta = slave.GetSymbolMetadata(slaveSymbol);
                double step = meta?.VolumeStep ?? 0.01;
                double minVol = meta?.MinVolume ?? 0.01;
                double maxVol = meta?.MaxVolume ?? 100.0;

                double rounded = Math.Floor(slaveVolume / step) * step;
                // Re-snap to the broker's decimal precision: floating-point
                // arithmetic can leave us with 0.30000000000004 which the broker
                // treats as 0.30 + epsilon and rejects. Round to the digit count
                // implied by the step.
                int stepDigits = step >= 1 ? 0
                               : step >= 0.1 ? 1
                               : step >= 0.01 ? 2
                               : 3;
                rounded = Math.Round(rounded, stepDigits);

                if (rounded < minVol)
                {
                    // Below broker minimum: clamp up to min. This is the
                    // "0.0824 desired -> 0.01 sent" path the user just hit.
                    rounded = minVol;
                }
                if (rounded > maxVol)
                {
                    rounded = maxVol;
                }

                DispatchOpen(ev, link, slave, slaveSymbol, slaveOrderType, rounded);
                break;
            }

            case MasterEventKind.MarketClose:
            case MasterEventKind.PartialClose:
            case MasterEventKind.PendingCancel:
                // Closes never compute slave volume — they use the slave's existing
                // position volume looked up from _slaveTickets.
                DispatchClose(ev, link, slave, slaveSymbol, slaveOrderType);
                break;

            case MasterEventKind.Modify:
                if (link.CopyModifications)
                    DispatchModify(ev, link, slave, slaveSymbol);
                break;
        }
    }

    private void DispatchOpen(MasterOrderEvent ev, CopyLink link, IBrokerConnection slave,
        string slaveSymbol, CopyOrderType orderType, double volume)
    {
        // Ensure the slave is subscribed for quotes on this symbol so its broker
        // accepts the trade with current prices.
        try { slave.Subscribe(slaveSymbol); } catch { }

        var quote = slave.GetQuote(slaveSymbol);
        double price = orderType switch
        {
            CopyOrderType.Buy  => quote?.Ask ?? 0,
            CopyOrderType.Sell => quote?.Bid ?? 0,
            _                  => ev.Price  // pending orders use the master's specified price
        };

        // Round SL/TP to the slave symbol's decimal precision. Mismatched
        // digits (e.g. master uses USDJPY 3-digit pricing, slave uses 2-digit)
        // can cause the broker to reject the SL/TP silently while accepting
        // the order — leaving the slave running with no stop. Always round
        // before passing to the broker.
        var slaveMeta = slave.GetSymbolMetadata(slaveSymbol);
        int priceDigits = slaveMeta?.Digits ?? 5; // 5-digit forex is a safe default
        double sl = link.CopySLTP ? Math.Round(ev.StopLoss,   priceDigits) : 0;
        double tp = link.CopySLTP ? Math.Round(ev.TakeProfit, priceDigits) : 0;

        _logger.LogInformation(
            "Routing open: link {LinkId} master#{MasterTicket} {Type} {Symbol} vol={Volume} price={Price} sl={SL} tp={TP} copySLTP={CopySLTP}",
            link.Id, ev.Ticket, orderType, slaveSymbol, volume, price, sl, tp, link.CopySLTP);

        var req = new OrderSendRequest(
            Symbol: slaveSymbol,
            Volume: volume,
            Price: price,
            OrderType: orderType,
            StopLoss: sl,
            TakeProfit: tp,
            MaxSlippagePoints: link.MaxSlippagePoints,
            Comment: $"LTC:{ev.Ticket}");

        var entry = LogActivity(ev, link, ActivityKind.Open, ActivityStatus.InFlight,
            symbol: slaveSymbol, volume: volume, price: price, orderType: orderType.ToString());

        _ = SendAndRecordAsync(slave, req, ev, link, entry);
    }

    private async Task SendAndRecordAsync(IBrokerConnection slave, OrderSendRequest req,
        MasterOrderEvent ev, CopyLink link, ActivityEntry entry)
    {
        try
        {
            var result = await slave.SendOrderAsync(req).ConfigureAwait(false);

            // Latency = master event arrival -> here. (For internal-only latency,
            // use the time we set entry.InternalLatencyMicros at LogActivity below.)
            long elapsed = LatencyClock.ElapsedMicros(ev.ReceivedAtTicks);


            _activity.Update(entry.Id, e =>
            {
                e.InternalLatencyMicros = elapsed;
                if (result.Success)
                {
                    e.Status = ActivityStatus.Success;
                    e.SlaveTicket = result.Ticket;
                }
                else
                {
                    e.Status = ActivityStatus.Failed;
                    e.ErrorMessage = result.ErrorMessage;
                }
            });

            if (result.Success && result.Ticket != 0)
            {
                _slaveTickets[(ev.Ticket, link.SlaveAccountId)] = result.Ticket;
                // Capture the volume sent at open. The close path looks this up
                // instead of trusting the master's close-event volume (which the
                // DLL may report as 0 once the position is gone).
                _openVolumes[(ev.Ticket, link.SlaveAccountId)] = req.Volume;

                // Replay any SL/TP modify events that arrived between this open
                // being dispatched and now. Common case: the broker reports the
                // open and the SL/TP-attached-on-open as two separate events
                // (MarketOpen with SL/TP=0, then MarketModify with the real
                // values). Without this replay the slave would run unprotected.
                if (link.CopyModifications &&
                    _pendingModifies.TryRemove((ev.Ticket, link.SlaveAccountId), out var pending) &&
                    (pending.sl != req.StopLoss || pending.tp != req.TakeProfit))
                {
                    // Re-round to slave precision: the values stashed by the
                    // earlier DispatchModify call are master-precision raw.
                    var slaveMetaReplay = slave.GetSymbolMetadata(req.Symbol);
                    int replayDigits = slaveMetaReplay?.Digits ?? 5;
                    double replaySl = Math.Round(pending.sl, replayDigits);
                    double replayTp = Math.Round(pending.tp, replayDigits);

                    // Fire-and-forget modify. We don't await it because the
                    // outer SendAndRecordAsync should return quickly so the
                    // next master event isn't blocked behind us.
                    var modifyReq = new OrderModifyRequest(
                        Ticket: result.Ticket,
                        Symbol: req.Symbol,            // use slave symbol from the original open
                        StopLoss: replaySl,
                        TakeProfit: replayTp,
                        PendingPrice: null);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var modResult = await slave.ModifyOrderAsync(modifyReq).ConfigureAwait(false);
                            if (!modResult.Success)
                            {
                                _logger.LogWarning(
                                    "Replayed SL/TP modify failed on slave (link {LinkId}, master ticket {MasterTicket}): {Err}",
                                    link.Id, ev.Ticket, modResult.ErrorMessage);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex,
                                "Replayed SL/TP modify threw on slave (link {LinkId}, master ticket {MasterTicket})",
                                link.Id, ev.Ticket);
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _activity.Update(entry.Id, e =>
            {
                e.Status = ActivityStatus.Failed;
                e.ErrorMessage = ex.Message;
            });
            _logger.LogError(ex, "Could not send a copied open trade to the slave for link {LinkId}.", link.Id);
        }
    }

    private void DispatchClose(MasterOrderEvent ev, CopyLink link, IBrokerConnection slave,
        string slaveSymbol, CopyOrderType masterOrderType)
    {
        if (!_slaveTickets.TryGetValue((ev.Ticket, link.SlaveAccountId), out var slaveTicket))
        {
            LogActivity(ev, link, ActivityKind.Close, ActivityStatus.Skipped,
                error: "no matching slave ticket");
            return;
        }

        // Volume strategy:
        //  1. First preference — the volume we recorded at OPEN time. This is
        //     authoritative because we know exactly what we sent the slave, and
        //     the slave's broker accepted that number.
        //  2. Fallback — master's close-event volume, but only if non-zero. The
        //     DLL sometimes reports Lots=0 for closed orders since the position
        //     no longer exists on disk; using 0 produces the "Request Lots = 0"
        //     reject we saw in the field.
        //  3. Last resort — bail with a clear error. Better than sending 0.
        double volume;
        if (_openVolumes.TryGetValue((ev.Ticket, link.SlaveAccountId), out var openVol) && openVol > 0)
        {
            volume = openVol;
        }
        else if (ev.Volume > 0)
        {
            volume = ev.Volume;
        }
        else
        {
            LogActivity(ev, link, ActivityKind.Close, ActivityStatus.Skipped,
                error: "could not determine close volume (open record missing and event volume was 0)");
            return;
        }

        var quote = slave.GetQuote(slaveSymbol);
        double price = masterOrderType switch
        {
            CopyOrderType.Buy  => quote?.Bid ?? 0, // closing a long means selling at bid
            CopyOrderType.Sell => quote?.Ask ?? 0,
            _                  => 0
        };

        var req = new OrderCloseRequest(
            Ticket: slaveTicket,
            Symbol: slaveSymbol,
            Volume: volume,
            OriginalOrderType: masterOrderType,
            Price: price,
            MaxSlippagePoints: link.MaxSlippagePoints);

        var entry = LogActivity(ev, link, ActivityKind.Close, ActivityStatus.InFlight,
            symbol: slaveSymbol, volume: volume, price: price);

        _ = CloseAndRecordAsync(slave, req, ev, link, entry);
    }

    private async Task CloseAndRecordAsync(IBrokerConnection slave, OrderCloseRequest req,
        MasterOrderEvent ev, CopyLink link, ActivityEntry entry)
    {
        try
        {
            var result = await slave.CloseOrderAsync(req).ConfigureAwait(false);
            long elapsed = LatencyClock.ElapsedMicros(ev.ReceivedAtTicks);


            _activity.Update(entry.Id, e =>
            {
                e.InternalLatencyMicros = elapsed;
                e.Status = result.Success ? ActivityStatus.Success : ActivityStatus.Failed;
                e.ErrorMessage = result.ErrorMessage;
            });

            if (result.Success)
            {
                _slaveTickets.TryRemove((ev.Ticket, link.SlaveAccountId), out _);
                _openVolumes.TryRemove((ev.Ticket, link.SlaveAccountId), out _);
                _pendingModifies.TryRemove((ev.Ticket, link.SlaveAccountId), out _);
            }
        }
        catch (Exception ex)
        {
            _activity.Update(entry.Id, e =>
            {
                e.Status = ActivityStatus.Failed;
                e.ErrorMessage = ex.Message;
            });
            _logger.LogError(ex, "Could not send a copied close to the slave for link {LinkId}.", link.Id);
        }
    }

    private void DispatchModify(MasterOrderEvent ev, CopyLink link, IBrokerConnection slave, string slaveSymbol)
    {
        // Buffer the latest SL/TP unconditionally — if the slave open is still
        // in flight we'll replay this after open completes; if it's done we
        // overwrite the stashed value with the same one and fire immediately.
        // Latest-wins keeps memory bounded (one entry per copy link until the
        // master ticket closes).
        _pendingModifies[(ev.Ticket, link.SlaveAccountId)] =
            (link.CopySLTP ? ev.StopLoss : 0, link.CopySLTP ? ev.TakeProfit : 0);

        if (!_slaveTickets.TryGetValue((ev.Ticket, link.SlaveAccountId), out var slaveTicket))
        {
            // Slave open still in flight. The replay in DispatchOpen's success
            // continuation will fire the modify once we know the ticket.
            return;
        }

        // slaveSymbol is the already-mapped symbol that HandleEvent resolved
        // upstream — we don't redo the prefix/suffix dance here.

        // Round SL/TP to the slave's price precision. See DispatchOpen for
        // the rationale — mismatched digit counts between master and slave
        // can cause silent broker rejection of SL/TP values.
        var slaveMeta = slave.GetSymbolMetadata(slaveSymbol);
        int priceDigits = slaveMeta?.Digits ?? 5;
        double sl = link.CopySLTP ? Math.Round(ev.StopLoss,   priceDigits) : 0;
        double tp = link.CopySLTP ? Math.Round(ev.TakeProfit, priceDigits) : 0;

        _logger.LogInformation(
            "Routing modify: link {LinkId} master#{MasterTicket}->slave#{SlaveTicket} {Symbol} sl={SL} tp={TP}",
            link.Id, ev.Ticket, slaveTicket, slaveSymbol, sl, tp);

        var req = new OrderModifyRequest(
            Ticket: slaveTicket,
            Symbol: slaveSymbol,
            StopLoss: sl,
            TakeProfit: tp,
            PendingPrice: null);

        var entry = LogActivity(ev, link, ActivityKind.Modify, ActivityStatus.InFlight,
            symbol: slaveSymbol);

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await slave.ModifyOrderAsync(req).ConfigureAwait(false);
                long elapsed = LatencyClock.ElapsedMicros(ev.ReceivedAtTicks);

                _activity.Update(entry.Id, e =>
                {
                    e.InternalLatencyMicros = elapsed;
                    e.Status = result.Success ? ActivityStatus.Success : ActivityStatus.Failed;
                    e.ErrorMessage = result.ErrorMessage;
                });

                // If the modify succeeded we can drop the pending entry; if it
                // failed (e.g. broker rejected a too-close SL) leave it so a
                // subsequent re-modify carries the latest value.
                if (result.Success)
                    _pendingModifies.TryRemove((ev.Ticket, link.SlaveAccountId), out _);
            }
            catch (Exception ex)
            {
                _activity.Update(entry.Id, e =>
                {
                    e.Status = ActivityStatus.Failed;
                    e.ErrorMessage = ex.Message;
                });
                _logger.LogError(ex, "Could not send a stop-loss/take-profit update to the slave for link {LinkId}.", link.Id);
            }
        });
    }

    private ActivityEntry LogActivity(
        MasterOrderEvent ev, CopyLink link,
        ActivityKind kind, ActivityStatus status,
        string? symbol = null, double volume = 0, double price = 0,
        string? orderType = null, string? error = null)
    {
        var masterAcc = _connections.Get(ev.MasterAccountId)?.Account;
        var slaveAcc  = _connections.Get(link.SlaveAccountId)?.Account;

        var entry = new ActivityEntry
        {
            Kind = kind,
            Status = status,
            MasterAccountId = ev.MasterAccountId,
            SlaveAccountId = link.SlaveAccountId,
            MasterAccountLabel = masterAcc?.DisplayName,
            SlaveAccountLabel  = slaveAcc?.DisplayName,
            Symbol = symbol ?? ev.Symbol,
            OrderType = orderType,
            Volume = volume,
            Price = price,
            MasterTicket = ev.Ticket,
            // Initial latency stamp = synchronous portion (event -> log entry).
            // SendAndRecordAsync overwrites this with the full event->dispatched-result latency.
            InternalLatencyMicros = LatencyClock.ElapsedMicros(ev.ReceivedAtTicks),
            ErrorMessage = error,
        };
        _activity.Append(entry);
        return entry;
    }
}
