using FluentAssertions;
using LTC.Core.Models;
using LTC.Core.Risk;
using Xunit;

namespace LTC.Tests.Risk;

/// <summary>
/// Tests for the prop firm risk calculator. The calculations are pure
/// functions of inputs — no IO, no time — so they're fast and exhaustive.
///
/// We test the math, not any prop firm in particular. The tests use
/// FTMO-shaped numbers (5% daily / 10% max on a $100k account) because
/// those values produce easy-to-verify arithmetic, NOT because we ship
/// any FTMO-specific code. Customers enter their own numbers from their
/// own firm's dashboard.
/// </summary>
public class PropRiskCalculatorTests
{
    /// <summary>Convenience: a $100k account with 5% daily / 10% max
    /// limits, static drawdown. Matches what an FTMO 100k 2-Step trader
    /// would enter, but is just easy math for the tests.</summary>
    private static PropFirmConfig Fresh100k() => new()
    {
        FirmName = "Test Firm",
        Phase = PropFirmPhase.Phase2,
        StartingBalance = 100_000m,
        DailyLossLimit = 5_000m,
        MaxLossLimit = 10_000m,
        DrawdownType = DrawdownType.StaticBalance,
    };

    [Fact]
    public void FreshAccount_HasFullHeadroom()
    {
        var cfg = Fresh100k();
        var state = PropRiskCalculator.Compute(
            config: cfg,
            currentEquity: 100_000m,
            dailyAnchorEquity: 100_000m,
            highWaterMarkEquity: 100_000m);

        state.DailyLossUsed.Should().Be(0m);
        state.DailyHeadroom.Should().Be(5_000m);
        state.DailyPercent.Should().Be(0m);
        state.OverallLossUsed.Should().Be(0m);
        state.OverallHeadroom.Should().Be(10_000m,
            "static drawdown floor is starting balance - max loss = 90,000, current is 100,000");
    }

    [Fact]
    public void DownOnTheDay_TracksDailyLossCorrectly()
    {
        var cfg = Fresh100k();
        // Trader is at 99,500 from a 100,000 morning anchor: -$500 today.
        var state = PropRiskCalculator.Compute(cfg, 99_500m, 100_000m, 100_000m);

        state.DailyLossUsed.Should().Be(500m);
        state.DailyHeadroom.Should().Be(4_500m, "5,000 limit - 500 used = 4,500 remaining");
        state.DailyPercent.Should().Be(10m, "500 of 5,000 = 10%");
    }

    [Fact]
    public void UpOnTheDay_LossClampedToZero_HeadroomExtended()
    {
        var cfg = Fresh100k();
        // Trader is up $500 today (current 100,500 vs anchor 100,000).
        // The "loss used" stays clamped to zero — we don't show negative
        // loss in the UI.
        // BUT the headroom should EXPAND to reflect today's profit: it's
        // now limit + today's profit so far (the prop-firm "you can lose
        // X more today" number). A flat-rate limit would discourage
        // traders from holding winners; the rolling-cushion version
        // mirrors how every prop firm dashboard shows it.
        var state = PropRiskCalculator.Compute(cfg, 100_500m, 100_000m, 100_000m);
        state.DailyLossUsed.Should().Be(0m, "we don't show negative loss");
        state.DailyHeadroom.Should().Be(5_500m,
            "limit 5,000 + 500 banked today = 5,500 cushion");
    }

    [Fact]
    public void DailyMuchTighterThanOverall_FlagsDailyAsClosest()
    {
        var cfg = Fresh100k();
        // Down 4,500 on the day (90% of daily limit used), but only 4,500
        // total = 45% of overall. Daily is tighter.
        var state = PropRiskCalculator.Compute(cfg, 95_500m, 100_000m, 100_000m);

        state.ClosestIsDaily.Should().BeTrue();
        state.ClosestHeadroom.Should().Be(500m, "5,000 - 4,500 = 500");
    }

    [Fact]
    public void OverallTighter_FlagsOverallAsClosest()
    {
        var cfg = Fresh100k();
        // Anchor was 95k (trader was down 5k coming into today), now at
        // 91,300. Today's loss = 95,000 - 91,300 = 3,700 → daily headroom 1,300.
        // Overall loss from 100k = 8,700 → floor is 90k, overall headroom 1,300.
        // Wait — that's also a tie. Let me pick numbers that produce a real
        // gap: anchor 95k, current 91,700.
        //   Daily loss = 95,000 - 91,700 = 3,300 → daily headroom = 1,700
        //   Overall loss from 100k = 8,300 → floor 90k → overall headroom = 1,700
        // Still a tie because the math is symmetric when both anchors are
        // measuring from points equidistant to the floor. We need to break
        // that symmetry: trader was DOWN coming into today (anchor < start),
        // and went further down. Try anchor 96k, current 91k:
        //   Daily loss = 96,000 - 91,000 = 5,000 → at the daily limit, headroom 0
        //   Overall loss from 100k = 9,000 → overall headroom 1,000
        // That makes daily the tightest, not overall. To make OVERALL tighter
        // we need the trader to have been at a high before today and given
        // back less today than the cumulative loss from start. Try anchor
        // 93k, current 91k:
        //   Daily loss = 93k - 91k = 2,000 → daily headroom 5,000 - 2,000 = 3,000
        //   Overall loss = 100k - 91k = 9,000 → overall headroom 1,000
        // Now overall is genuinely tighter than daily. Use these numbers.
        var state = PropRiskCalculator.Compute(cfg, 91_000m, 93_000m, 100_000m);

        state.DailyLossUsed.Should().Be(2_000m);
        state.DailyHeadroom.Should().Be(3_000m);
        state.OverallLossUsed.Should().Be(9_000m);
        state.OverallHeadroom.Should().Be(1_000m,
            "current equity 91,000 above static floor of 90,000");
        state.ClosestIsDaily.Should().BeFalse("1,000 overall headroom < 3,000 daily headroom");
        state.ClosestHeadroom.Should().Be(1_000m);
    }

    [Fact]
    public void Trailing_FloorFollowsHighWaterMark()
    {
        // Trailing 10% off the high.
        var cfg = new PropFirmConfig
        {
            FirmName = "Test Trailing",
            StartingBalance = 100_000m,
            DailyLossLimit = 5_000m,
            MaxLossLimit = 10_000m,
            DrawdownType = DrawdownType.Trailing,
        };

        // Trader peaked at 108k, now at 102k. Floor = 108k - 10k = 98k.
        // Overall headroom = 102k - 98k = 4k.
        var state = PropRiskCalculator.Compute(cfg, 102_000m, 102_000m, 108_000m);
        state.OverallHeadroom.Should().Be(4_000m, "102k current - 98k floor");
    }

    [Fact]
    public void TradeFitsInHeadroom_AcceptsSafeTrade()
    {
        var cfg = Fresh100k();
        var state = PropRiskCalculator.Compute(cfg, 100_000m, 100_000m, 100_000m);
        PropRiskCalculator.TradeFitsInHeadroom(state, 300m).Should().BeTrue();
    }

    [Fact]
    public void TradeFitsInHeadroom_RejectsOverDaily()
    {
        var cfg = Fresh100k();
        // 100 left on daily, 5,000 on overall. A 200 risk trade exceeds daily.
        var state = PropRiskCalculator.Compute(cfg, 95_100m, 100_000m, 100_000m);
        PropRiskCalculator.TradeFitsInHeadroom(state, 200m).Should().BeFalse();
    }

    [Fact]
    public void TradeFitsInHeadroom_RejectsOverOverall()
    {
        var cfg = Fresh100k();
        // Anchor today = current = 91,500. Daily headroom is full 5k,
        // overall headroom only 1,500. A 2,000 trade exceeds overall.
        var state = PropRiskCalculator.Compute(cfg, 91_500m, 91_500m, 100_000m);
        state.DailyHeadroom.Should().Be(5_000m);
        state.OverallHeadroom.Should().Be(1_500m);
        PropRiskCalculator.TradeFitsInHeadroom(state, 2_000m).Should().BeFalse();
    }

    [Theory]
    [InlineData(50_000.0,  2_500.0, 6_000.0)]   // 5% / 12%
    [InlineData(100_000.0, 4_000.0, 5_000.0)]   // 4% / 5% — tight
    [InlineData(50_000.0,  2_500.0, 3_000.0)]   // 5% / 6%
    [InlineData(25_000.0,  500.0,   750.0)]     // 2% / 3%
    public void Calculator_HandlesVariousLimitShapes(double balance, double daily, double max)
    {
        var cfg = new PropFirmConfig
        {
            FirmName = "Generic",
            StartingBalance = (decimal)balance,
            DailyLossLimit = (decimal)daily,
            MaxLossLimit = (decimal)max,
            DrawdownType = DrawdownType.StaticBalance,
        };
        var state = PropRiskCalculator.Compute(cfg, (decimal)balance, (decimal)balance, (decimal)balance);
        state.DailyHeadroom.Should().Be((decimal)daily, "fresh account has full daily headroom");
        state.OverallHeadroom.Should().Be((decimal)max, "fresh account has full overall headroom");
    }
}
