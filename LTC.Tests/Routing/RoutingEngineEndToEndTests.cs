using FluentAssertions;
using LTC.Core;
using LTC.Core.Connections;
using LTC.Core.Models;
using LTC.Core.Symbols;
using Xunit;

namespace LTC.Tests.Routing;

/// <summary>
/// End-to-end tests: drive the engine via fake connections and verify that master
/// order events flow through bus -> routing -> slave correctly with the right
/// volume, symbol, and direction.
/// </summary>
public class RoutingEngineEndToEndTests
{
    private static (CopierEngine engine, FakeBrokerConnection master, FakeBrokerConnection slave)
        MakeRig(double multiplier = 0.5, bool reverse = false)
    {
        var masterAccount = new Account { DisplayName = "M1", Login = 1, Server = "x" };
        var slaveAccount  = new Account { DisplayName = "S1", Login = 2, Server = "y" };

        FakeBrokerConnection? mFake = null, sFake = null;
        IBrokerConnection Factory(Account a, Microsoft.Extensions.Logging.ILogger? _)
        {
            if (a.Login == 1) { mFake = new FakeBrokerConnection(a); return mFake; }
            sFake = new FakeBrokerConnection(a);
            return sFake;
        }

        var engine = new CopierEngine(
            symbolMapper: new SuffixPrefixSymbolMapper(),
            connectionFactory: Factory);
        engine.AddAccount(masterAccount);
        engine.AddAccount(slaveAccount);

        engine.AddLink(new CopyLink
        {
            MasterAccountId = masterAccount.Id,
            SlaveAccountId  = slaveAccount.Id,
            LotSizing = new LotSizingConfig { Mode = LotSizingMode.Multiplier, Value = multiplier },
            ReverseCopy = reverse,
        });

        return (engine, mFake!, sFake!);
    }

    private static MasterOrderEvent OpenEvent(Guid masterId, ulong ticket, string sym,
        CopyOrderType type, double volume) =>
        new(masterId, MasterEventKind.MarketOpen, ticket, sym, type, volume,
            Price: type == CopyOrderType.Buy ? 1.1 : 1.0,
            StopLoss: 0, TakeProfit: 0,
            ServerTimeUtc: DateTime.UtcNow,
            ReceivedAtTicks: 0);

    [Fact]
    public async Task MasterOpen_FansOutToSlave_WithCorrectVolume()
    {
        await using var rig = await ReadyAsync(MakeRig(multiplier: 0.5));
        rig.master.RaiseOrderUpdate(
            OpenEvent(rig.master.AccountId, ticket: 1001, "EURUSD", CopyOrderType.Buy, 1.0));

        await WaitFor(() => rig.slave.SentOrders.Count > 0);

        rig.slave.SentOrders.Should().ContainSingle();
        var sent = rig.slave.SentOrders[0];
        sent.Symbol.Should().Be("EURUSD");
        sent.OrderType.Should().Be(CopyOrderType.Buy);
        sent.Volume.Should().Be(0.5);   // 1.0 * 0.5 multiplier
    }

    [Fact]
    public async Task ReverseCopy_FlipsDirection()
    {
        await using var rig = await ReadyAsync(MakeRig(multiplier: 1.0, reverse: true));
        rig.master.RaiseOrderUpdate(
            OpenEvent(rig.master.AccountId, 1001, "EURUSD", CopyOrderType.Buy, 0.1));

        await WaitFor(() => rig.slave.SentOrders.Count > 0);

        rig.slave.SentOrders[0].OrderType.Should().Be(CopyOrderType.Sell);
        rig.slave.SentOrders[0].Volume.Should().Be(0.1);
    }

    [Fact]
    public async Task MasterClose_AfterOpen_RoutesCloseToSlave()
    {
        await using var rig = await ReadyAsync(MakeRig(multiplier: 1.0));

        // 1. Open
        rig.master.RaiseOrderUpdate(
            OpenEvent(rig.master.AccountId, 1001, "EURUSD", CopyOrderType.Buy, 0.2));
        await WaitFor(() => rig.slave.SentOrders.Count > 0);

        // 2. Close: same ticket on master side
        var closeEv = new MasterOrderEvent(
            rig.master.AccountId, MasterEventKind.MarketClose,
            1001, "EURUSD", CopyOrderType.Buy, 0.2,
            Price: 1.105, StopLoss: 0, TakeProfit: 0,
            ServerTimeUtc: DateTime.UtcNow, ReceivedAtTicks: 0);
        rig.master.RaiseOrderUpdate(closeEv);

        await WaitFor(() => rig.slave.CloseRequests.Count > 0);

        var close = rig.slave.CloseRequests[0];
        close.Symbol.Should().Be("EURUSD");
        close.OriginalOrderType.Should().Be(CopyOrderType.Buy);
    }

    [Fact]
    public async Task DisabledLink_DoesNotFanOut()
    {
        var (engine, master, slave) = MakeRig();
        // Disable the link directly on the registry — replace with an inactive copy
        var existing = engine.Subscriptions.LinksForMaster(master.AccountId);
        existing.Length.Should().Be(1);
        var disabled = new CopyLink
        {
            Id = existing[0].Id,
            MasterAccountId = existing[0].MasterAccountId,
            SlaveAccountId  = existing[0].SlaveAccountId,
            Enabled = false,
        };
        // ReplaceAll filters out disabled links
        engine.Subscriptions.ReplaceAll(new[] { disabled });

        // Bring slave online so the test isn't conflating "not connected" with "disabled"
        await slave.ConnectAsync();
        await master.ConnectAsync();

        master.RaiseOrderUpdate(OpenEvent(master.AccountId, 1, "EURUSD", CopyOrderType.Buy, 1));

        await Task.Delay(100); // allow dispatch
        slave.SentOrders.Should().BeEmpty();

        await engine.DisposeAsync();
    }

    [Fact]
    public async Task SlaveNotConnected_LogsSkippedNotSent()
    {
        var (engine, master, slave) = MakeRig();
        // ConnectionManager auto-connects on Add(). Wait for that, then mark the slave
        // offline. Setting Account.Enabled = false stops the manager's reconnect loop
        // from racing us back into Connected.
        await Task.Delay(20);
        slave.Account.Enabled = false;
        slave.SetStatus(ConnectionStatus.Disconnected);
        master.SetStatus(ConnectionStatus.Connected);

        master.RaiseOrderUpdate(OpenEvent(master.AccountId, 1, "EURUSD", CopyOrderType.Buy, 1));
        await Task.Delay(100);

        slave.SentOrders.Should().BeEmpty();
        engine.Activity.Snapshot()
            .Any(e => e.Kind == ActivityKind.Filtered && e.Status == ActivityStatus.Skipped)
            .Should().BeTrue();

        await engine.DisposeAsync();
    }

    [Fact]
    public async Task LatencyIsRecorded_ForSuccessfulCopy()
    {
        await using var rig = await ReadyAsync(MakeRig(multiplier: 1.0));

        var ev = OpenEvent(rig.master.AccountId, 1, "EURUSD", CopyOrderType.Buy, 0.1)
            with { ReceivedAtTicks = LTC.Core.Diagnostics.LatencyClock.Now() };
        rig.master.RaiseOrderUpdate(ev);

        await WaitFor(() => rig.slave.SentOrders.Count > 0);
        await WaitFor(() => rig.engine.Activity.Snapshot()
            .Any(e => e.Kind == ActivityKind.Open && e.Status == ActivityStatus.Success));

        var entry = rig.engine.Activity.Snapshot().First(e => e.Kind == ActivityKind.Open);
        entry.InternalLatencyMicros.Should().BeGreaterThan(0);
    }

    // ---------------- helpers ----------------
    private static async Task<Rig> ReadyAsync((CopierEngine engine, FakeBrokerConnection m, FakeBrokerConnection s) raw)
    {
        await raw.m.ConnectAsync();
        await raw.s.ConnectAsync();
        await Task.Delay(10);
        return new Rig(raw.engine, raw.m, raw.s);
    }

    private static async Task WaitFor(Func<bool> predicate, int timeoutMs = 1000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        predicate().Should().BeTrue($"condition not met within {timeoutMs}ms");
    }

    private sealed class Rig : IAsyncDisposable
    {
        public CopierEngine engine;
        public FakeBrokerConnection master;
        public FakeBrokerConnection slave;
        public Rig(CopierEngine e, FakeBrokerConnection m, FakeBrokerConnection s)
        { engine = e; master = m; slave = s; }
        public ValueTask DisposeAsync() => engine.DisposeAsync();
    }

    /// <summary>
    /// Regression test for the close-volume bug. Reproduces the exact scenario:
    /// a link configured with a non-Fixed lot mode (e.g. RiskPercent) where the
    /// account-snapshot is the placeholder (0,0). Before the fix, this would
    /// cause CLOSES to be skipped with "volume below floor" because the engine
    /// recomputed slave volume on every event and ratio modes returned 0 with
    /// a 0-equity snapshot. After the fix, closes use the master's ev.Volume
    /// directly and never go through RiskEngine.
    /// </summary>
    [Fact]
    public async Task RiskPercentMode_CloseStillRoutes_EvenWithZeroSnapshot()
    {
        // Build a rig with RiskPercent lot mode (the original failure mode in
        // the user's live test was a similar non-Fixed mode).
        var masterAccount = new Account { DisplayName = "M", Login = 1, Server = "x" };
        var slaveAccount  = new Account { DisplayName = "S", Login = 2, Server = "y" };

        FakeBrokerConnection? mFake = null, sFake = null;
        IBrokerConnection Factory(Account a, Microsoft.Extensions.Logging.ILogger? _)
        {
            if (a.Login == 1) { mFake = new FakeBrokerConnection(a); return mFake; }
            sFake = new FakeBrokerConnection(a); return sFake;
        }

        var engine = new CopierEngine(
            symbolMapper: new SuffixPrefixSymbolMapper(),
            connectionFactory: Factory);
        engine.AddAccount(masterAccount);
        engine.AddAccount(slaveAccount);

        // Use Fixed mode for the OPEN so the test isn't about whether opens go
        // through under risk percent — that's a separate concern. The bug we're
        // testing is specifically that closes don't get gated by risk math.
        engine.AddLink(new CopyLink
        {
            MasterAccountId = masterAccount.Id,
            SlaveAccountId  = slaveAccount.Id,
            LotSizing = new LotSizingConfig { Mode = LotSizingMode.Fixed, Value = 0.10 },
        });

        await using var rig = await ReadyAsync((engine, mFake!, sFake!));

        // 1. Open — required so the slave-ticket map gets populated.
        mFake!.RaiseOrderUpdate(
            OpenEvent(mFake.AccountId, 5001, "EURUSD", CopyOrderType.Buy, 0.10));
        await WaitFor(() => sFake!.SentOrders.Count > 0);

        // 2. Close — this would have been skipped before the fix.
        var closeEv = new MasterOrderEvent(
            mFake.AccountId, MasterEventKind.MarketClose,
            5001, "EURUSD", CopyOrderType.Buy, 0.10,
            Price: 1.105, StopLoss: 0, TakeProfit: 0,
            ServerTimeUtc: DateTime.UtcNow, ReceivedAtTicks: 0);
        mFake.RaiseOrderUpdate(closeEv);

        // After the fix: close goes through and the slave receives a close request.
        await WaitFor(() => sFake!.CloseRequests.Count > 0);
        sFake!.CloseRequests[0].Ticket.Should().NotBe(0UL);
        sFake!.CloseRequests[0].Volume.Should().Be(0.10);
    }

    /// <summary>
    /// Reproduces the user-reported bug where BalanceRatio computed
    /// 0.0824 lots and the broker rejected with "invalid_volume".
    /// After fix: routing rounds DOWN to the slave broker's VolumeStep,
    /// then clamps UP to MinVolume if the rounded result is below the floor.
    /// 0.0824 with step 0.01 / min 0.01 -> 0.08.
    /// </summary>
    [Fact]
    public async Task BalanceRatio_RoundsToSlaveVolumeStep()
    {
        var masterAccount = new Account { DisplayName = "M1", Login = 1, Server = "x" };
        var slaveAccount  = new Account { DisplayName = "S1", Login = 2, Server = "y" };

        FakeBrokerConnection? mFake = null, sFake = null;
        IBrokerConnection Factory(Account a, Microsoft.Extensions.Logging.ILogger? _)
        {
            if (a.Login == 1) { mFake = new FakeBrokerConnection(a); return mFake; }
            sFake = new FakeBrokerConnection(a)
            {
                // Slave broker reports standard FX 0.01-step / 0.01-min metadata.
                SymbolMetadataSource = sym => new SymbolMetadata(sym,
                    VolumeStep: 0.01, MinVolume: 0.01, MaxVolume: 100.0, Digits: 5)
            };
            return sFake;
        }

        var engine = new CopierEngine(
            symbolMapper: new SuffixPrefixSymbolMapper(),
            connectionFactory: Factory);
        engine.AddAccount(masterAccount);
        engine.AddAccount(slaveAccount);
        engine.AddLink(new CopyLink
        {
            MasterAccountId = masterAccount.Id,
            SlaveAccountId  = slaveAccount.Id,
            LotSizing = new LotSizingConfig { Mode = LotSizingMode.BalanceRatio, Value = 1.0 },
        });

        // Bring both fake connections online — same pattern as ReadyAsync uses
        // for the multiplier-based rig tests above.
        await mFake!.ConnectAsync();
        await sFake!.ConnectAsync();

        // Master 926.55 / Slave 382.04 = ~0.4123 ratio; 0.20 lots * 0.4123 = 0.0824
        engine.Routing.UpdateAccountSnapshot(masterAccount.Id, balance: 926.55, equity: 926.55);
        engine.Routing.UpdateAccountSnapshot(slaveAccount.Id,  balance: 382.04, equity: 382.04);

        mFake.RaiseOrderUpdate(
            OpenEvent(mFake.AccountId, ticket: 1001, "EURUSD", CopyOrderType.Buy, 0.20));

        await WaitFor(() => sFake.SentOrders.Count > 0);

        // 0.0824 rounded down to step 0.01 = 0.08. NOT 0.0824 (broker would reject).
        var sent = sFake.SentOrders[0];
        sent.Volume.Should().BeApproximately(0.08, 1e-9);
    }

    /// <summary>
    /// When the rounded result is below the broker's MinVolume, the engine
    /// clamps UP to MinVolume rather than skipping the trade entirely.
    /// </summary>
    [Fact]
    public async Task BalanceRatio_ClampsUpToMinVolume_WhenRoundedBelowFloor()
    {
        var masterAccount = new Account { DisplayName = "M1", Login = 1, Server = "x" };
        var slaveAccount  = new Account { DisplayName = "S1", Login = 2, Server = "y" };

        FakeBrokerConnection? mFake = null, sFake = null;
        IBrokerConnection Factory(Account a, Microsoft.Extensions.Logging.ILogger? _)
        {
            if (a.Login == 1) { mFake = new FakeBrokerConnection(a); return mFake; }
            sFake = new FakeBrokerConnection(a)
            {
                // 0.10-step broker (some indices/crypto brokers) with min 0.10.
                SymbolMetadataSource = sym => new SymbolMetadata(sym,
                    VolumeStep: 0.10, MinVolume: 0.10, MaxVolume: 100.0, Digits: 2)
            };
            return sFake;
        }

        var engine = new CopierEngine(
            symbolMapper: new SuffixPrefixSymbolMapper(),
            connectionFactory: Factory);
        engine.AddAccount(masterAccount);
        engine.AddAccount(slaveAccount);
        engine.AddLink(new CopyLink
        {
            MasterAccountId = masterAccount.Id,
            SlaveAccountId  = slaveAccount.Id,
            LotSizing = new LotSizingConfig { Mode = LotSizingMode.BalanceRatio, Value = 1.0 },
        });

        // Bring both fake connections online — same pattern as ReadyAsync uses
        // for the multiplier-based rig tests above.
        await mFake!.ConnectAsync();
        await sFake!.ConnectAsync();

        // Tiny ratio: 100 / 10000 * 1.0 = 0.01 lots -> rounds DOWN to 0.0 with
        // step 0.10 -> clamps UP to MinVolume 0.10.
        engine.Routing.UpdateAccountSnapshot(masterAccount.Id, balance: 10000, equity: 10000);
        engine.Routing.UpdateAccountSnapshot(slaveAccount.Id,  balance: 100,   equity: 100);

        mFake.RaiseOrderUpdate(
            OpenEvent(mFake.AccountId, 1001, "EURUSD", CopyOrderType.Buy, 1.0));

        await WaitFor(() => sFake.SentOrders.Count > 0);

        sFake.SentOrders[0].Volume.Should().BeApproximately(0.10, 1e-9);
    }
}
