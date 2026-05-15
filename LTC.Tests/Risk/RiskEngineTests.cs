using FluentAssertions;
using LTC.Core.Connections;
using LTC.Core.Models;
using LTC.Core.Risk;
using Xunit;

namespace LTC.Tests.Risk;

public class RiskEngineTests
{
    private static AccountSnapshot Master(double bal, double eq) => new(bal, eq);
    private static AccountSnapshot Slave(double bal, double eq) => new(bal, eq);
    private static SlTpContext NoSlTp() => new(0, 0);

    // -------- Fixed --------

    [Fact]
    public void Fixed_Mode_AlwaysReturnsConfiguredValue()
    {
        var cfg = new LotSizingConfig { Mode = LotSizingMode.Fixed, Value = 0.25 };
        var v = RiskEngine.ComputeSlaveVolume(cfg, masterVolume: 5.0,
            Master(10000, 10000), Slave(1000, 1000), NoSlTp());
        v.Should().Be(0.25);
    }

    [Fact]
    public void Fixed_Mode_ClampedByMaxLot()
    {
        var cfg = new LotSizingConfig { Mode = LotSizingMode.Fixed, Value = 50, MaxLot = 10 };
        RiskEngine.ComputeSlaveVolume(cfg, 1, Master(0,0), Slave(0,0), NoSlTp())
            .Should().Be(10);
    }

    [Fact]
    public void Fixed_Mode_BelowMinFloor_ReturnsZero()
    {
        var cfg = new LotSizingConfig { Mode = LotSizingMode.Fixed, Value = 0.005, MinLot = 0.01 };
        RiskEngine.ComputeSlaveVolume(cfg, 1, Master(0,0), Slave(0,0), NoSlTp())
            .Should().Be(0);
    }

    // -------- Multiplier --------

    [Theory]
    [InlineData(1.0, 1.0, 1.0)]
    [InlineData(1.0, 0.5, 0.5)]
    [InlineData(2.0, 1.5, 3.0)]
    [InlineData(0.1, 1.0, 0.1)]
    public void Multiplier_Mode_ScalesMasterVolume(double masterVol, double mult, double expected)
    {
        var cfg = new LotSizingConfig { Mode = LotSizingMode.Multiplier, Value = mult };
        RiskEngine.ComputeSlaveVolume(cfg, masterVol, Master(0,0), Slave(0,0), NoSlTp())
            .Should().BeApproximately(expected, 1e-9);
    }

    // -------- Equity / Balance ratio --------

    [Fact]
    public void EquityRatio_Mode_ScalesByEquityRatio()
    {
        // master=100k slave=10k, master trades 1.0 lot -> slave should trade 0.1
        var cfg = new LotSizingConfig { Mode = LotSizingMode.EquityRatio, Value = 0 };
        var v = RiskEngine.ComputeSlaveVolume(cfg, 1.0,
            Master(0, 100000), Slave(0, 10000), NoSlTp());
        v.Should().BeApproximately(0.1, 1e-9);
    }

    [Fact]
    public void BalanceRatio_Mode_ScalesByBalanceRatio()
    {
        var cfg = new LotSizingConfig { Mode = LotSizingMode.BalanceRatio, Value = 0 };
        var v = RiskEngine.ComputeSlaveVolume(cfg, 2.0,
            Master(50000, 0), Slave(5000, 0), NoSlTp());
        v.Should().BeApproximately(0.2, 1e-9);
    }

    [Fact]
    public void EquityRatio_ZeroMasterEquity_ReturnsZero()
    {
        var cfg = new LotSizingConfig { Mode = LotSizingMode.EquityRatio, Value = 0 };
        var v = RiskEngine.ComputeSlaveVolume(cfg, 1.0,
            Master(0, 0), Slave(0, 1000), NoSlTp());
        v.Should().Be(0);
    }

    // -------- RiskPercent --------

    [Fact]
    public void RiskPercent_RequiresSL_ReturnsZeroIfMissing()
    {
        var cfg = new LotSizingConfig { Mode = LotSizingMode.RiskPercent, Value = 1.0 };
        RiskEngine.ComputeSlaveVolume(cfg, 1.0, Master(0,0), Slave(0, 10000), NoSlTp())
            .Should().Be(0);
    }

    [Fact]
    public void RiskPercent_BasicMath()
    {
        // 1% of 10,000 equity = $100 risk
        // SL distance = 50 units, value per unit per lot = $1
        // expected volume = 100 / (50 * 1) = 2.0 lots
        var cfg = new LotSizingConfig { Mode = LotSizingMode.RiskPercent, Value = 1.0, MaxLot = 100 };
        var v = RiskEngine.ComputeSlaveVolume(cfg, 1.0,
            Master(0,0), Slave(0, 10000),
            new SlTpContext(StopLossDistanceInPriceUnits: 50, PointValuePerLot: 1));
        v.Should().BeApproximately(2.0, 1e-9);
    }

    // -------- Reverse copy --------

    [Theory]
    [InlineData(CopyOrderType.Buy,           CopyOrderType.Sell)]
    [InlineData(CopyOrderType.Sell,          CopyOrderType.Buy)]
    [InlineData(CopyOrderType.BuyLimit,      CopyOrderType.SellLimit)]
    [InlineData(CopyOrderType.SellStop,      CopyOrderType.BuyStop)]
    [InlineData(CopyOrderType.BuyStopLimit,  CopyOrderType.SellStopLimit)]
    public void ApplyReverse_FlipsAllOrderTypes(CopyOrderType input, CopyOrderType expected)
    {
        RiskEngine.ApplyReverse(input, reverse: true).Should().Be(expected);
    }

    [Fact]
    public void ApplyReverse_DisabledReturnsOriginal()
    {
        RiskEngine.ApplyReverse(CopyOrderType.Buy, reverse: false).Should().Be(CopyOrderType.Buy);
    }

    // -------- Filters --------

    [Fact]
    public void Filter_EmptyWhitelist_AllowsAnything()
    {
        var f = new CopyFilter();
        var d = RiskEngine.EvaluateFilter(f, "EURUSD", CopyOrderEffectiveDirection.Buy, 0.5, 0);
        d.Action.Should().Be(FilterAction.Accept);
    }

    [Fact]
    public void Filter_NonEmptyWhitelist_BlocksOthers()
    {
        var f = new CopyFilter { SymbolWhitelist = { "EURUSD", "GBPUSD" } };
        var d = RiskEngine.EvaluateFilter(f, "USDJPY", CopyOrderEffectiveDirection.Buy, 0.5, 0);
        d.Action.Should().Be(FilterAction.Reject);
    }

    [Fact]
    public void Filter_Blacklist_RejectsListedSymbol()
    {
        var f = new CopyFilter { SymbolBlacklist = { "BTCUSD" } };
        var d = RiskEngine.EvaluateFilter(f, "BTCUSD", CopyOrderEffectiveDirection.Buy, 0.5, 0);
        d.Action.Should().Be(FilterAction.Reject);
        d.Reason.Should().Contain("blacklist");
    }

    [Fact]
    public void Filter_BuyOnly_BlocksSells()
    {
        var f = new CopyFilter { Direction = DirectionFilter.BuyOnly };
        RiskEngine.EvaluateFilter(f, "EURUSD", CopyOrderEffectiveDirection.Sell, 0.5, 0)
            .Action.Should().Be(FilterAction.Reject);
        RiskEngine.EvaluateFilter(f, "EURUSD", CopyOrderEffectiveDirection.Buy, 0.5, 0)
            .Action.Should().Be(FilterAction.Accept);
    }

    [Fact]
    public void Filter_MaxLotPerTrade_ClampsRatherThanRejects()
    {
        var f = new CopyFilter { MaxLotPerTrade = 0.5 };
        var d = RiskEngine.EvaluateFilter(f, "EURUSD", CopyOrderEffectiveDirection.Buy, 2.0, 0);
        d.Action.Should().Be(FilterAction.Clamp);
        d.VolumeOverride.Should().Be(0.5);
    }

    [Fact]
    public void Filter_DailyLossLimit_BlocksWhenLimitReached()
    {
        var f = new CopyFilter { DailyLossLimit = 100 };
        // current daily P/L is -150, limit is 100 → blocked
        RiskEngine.EvaluateFilter(f, "EURUSD", CopyOrderEffectiveDirection.Buy, 0.5, -150)
            .Action.Should().Be(FilterAction.Reject);
        // current daily P/L is -50, limit is 100 → allowed
        RiskEngine.EvaluateFilter(f, "EURUSD", CopyOrderEffectiveDirection.Buy, 0.5, -50)
            .Action.Should().Be(FilterAction.Accept);
    }
}
