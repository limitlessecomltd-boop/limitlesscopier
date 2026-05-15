using LTC.Core.Connections;
using LTC.Core.Models;

namespace LTC.Core.Risk;

/// <summary>
/// Pure functions converting master volume into slave volume according to a CopyLink's
/// LotSizingConfig, plus filter evaluation. No I/O, no broker calls — fully testable.
/// </summary>
public static class RiskEngine
{
    /// <summary>
    /// Compute the slave volume for one trade, given the link config and the account snapshots.
    /// Returns 0 if the trade should be skipped (under floor, or risk math impossible).
    /// </summary>
    public static double ComputeSlaveVolume(
        LotSizingConfig config,
        double masterVolume,
        AccountSnapshot masterSnapshot,
        AccountSnapshot slaveSnapshot,
        SlTpContext slTp)
    {
        if (masterVolume <= 0) return 0;

        double raw = config.Mode switch
        {
            LotSizingMode.Fixed         => config.Value,
            LotSizingMode.Multiplier    => masterVolume * config.Value,
            LotSizingMode.RiskPercent   => RiskPercentVolume(config.Value, slaveSnapshot, slTp),
            LotSizingMode.EquityRatio   => SafeRatio(slaveSnapshot.Equity, masterSnapshot.Equity) * masterVolume,
            LotSizingMode.BalanceRatio  => SafeRatio(slaveSnapshot.Balance, masterSnapshot.Balance) * masterVolume,
            _ => 0
        };

        if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw))
            return 0;

        // Apply min/max bounds from the config
        if (config.MinLot > 0 && raw < config.MinLot) return 0;     // below floor → skip
        if (config.MaxLot > 0 && raw > config.MaxLot) raw = config.MaxLot;

        return raw;
    }

    private static double RiskPercentVolume(double percent, AccountSnapshot slave, SlTpContext slTp)
    {
        // Only meaningful when the master trade has a stop loss. We compute the slave volume
        // such that hitting the slave's SL costs roughly (percent / 100) of slave equity.
        if (percent <= 0 || slave.Equity <= 0) return 0;
        if (slTp.StopLossDistanceInPriceUnits <= 0) return 0;
        if (slTp.PointValuePerLot <= 0) return 0;

        double riskCurrency = slave.Equity * (percent / 100.0);
        // riskCurrency = volume * SL_distance * value_per_unit_per_lot
        double volume = riskCurrency / (slTp.StopLossDistanceInPriceUnits * slTp.PointValuePerLot);
        return volume;
    }

    private static double SafeRatio(double numer, double denom)
        => denom <= 0 ? 0 : numer / denom;

    /// <summary>
    /// Decide whether a trade event passes a CopyFilter. Return value indicates the outcome
    /// and (when filtered) why — useful for the activity log.
    /// </summary>
    public static FilterDecision EvaluateFilter(
        CopyFilter filter,
        string masterSymbol,
        CopyOrderEffectiveDirection direction,
        double slaveVolumeAfterRisk,
        double slaveDailyRealisedPL)
    {
        if (filter.SymbolWhitelist.Count > 0 && !filter.SymbolWhitelist.Contains(masterSymbol))
            return FilterDecision.Reject("symbol not whitelisted");

        if (filter.SymbolBlacklist.Contains(masterSymbol))
            return FilterDecision.Reject("symbol blacklisted");

        if (filter.Direction == DirectionFilter.BuyOnly && direction == CopyOrderEffectiveDirection.Sell)
            return FilterDecision.Reject("buy-only filter");
        if (filter.Direction == DirectionFilter.SellOnly && direction == CopyOrderEffectiveDirection.Buy)
            return FilterDecision.Reject("sell-only filter");

        if (filter.MaxLotPerTrade > 0 && slaveVolumeAfterRisk > filter.MaxLotPerTrade)
            // Note: caller decides whether to clamp or reject. Here we just signal.
            return FilterDecision.Clamp(filter.MaxLotPerTrade, "max-lot cap");

        if (filter.DailyLossLimit > 0 && slaveDailyRealisedPL <= -filter.DailyLossLimit)
            return FilterDecision.Reject("daily loss limit reached");

        return FilterDecision.Accept();
    }

    /// <summary>
    /// Apply reverse-copy: if enabled, Buy-family becomes Sell-family and vice versa.
    /// </summary>
    public static CopyOrderType ApplyReverse(CopyOrderType original, bool reverse)
    {
        if (!reverse) return original;
        return original switch
        {
            CopyOrderType.Buy           => CopyOrderType.Sell,
            CopyOrderType.Sell          => CopyOrderType.Buy,
            CopyOrderType.BuyLimit      => CopyOrderType.SellLimit,
            CopyOrderType.SellLimit     => CopyOrderType.BuyLimit,
            CopyOrderType.BuyStop       => CopyOrderType.SellStop,
            CopyOrderType.SellStop      => CopyOrderType.BuyStop,
            CopyOrderType.BuyStopLimit  => CopyOrderType.SellStopLimit,
            CopyOrderType.SellStopLimit => CopyOrderType.BuyStopLimit,
            _ => original
        };
    }

    /// <summary>
    /// Reduce an order type to its directional family (buy or sell).
    /// </summary>
    public static CopyOrderEffectiveDirection DirectionOf(CopyOrderType t) => t switch
    {
        CopyOrderType.Buy or CopyOrderType.BuyLimit or CopyOrderType.BuyStop or CopyOrderType.BuyStopLimit
            => CopyOrderEffectiveDirection.Buy,
        _   => CopyOrderEffectiveDirection.Sell
    };
}

/// <summary>Snapshot of an account's equity/balance for risk calculations.</summary>
public sealed record AccountSnapshot(double Balance, double Equity);

/// <summary>
/// Stop-loss / take-profit context for risk-percent sizing.
/// StopLossDistanceInPriceUnits = absolute distance from open price to SL.
/// PointValuePerLot = currency value per 1 unit of price movement per 1 lot.
/// </summary>
public sealed record SlTpContext(double StopLossDistanceInPriceUnits, double PointValuePerLot);

public enum CopyOrderEffectiveDirection { Buy, Sell }

/// <summary>Outcome of a filter evaluation.</summary>
public sealed record FilterDecision(FilterAction Action, double? VolumeOverride, string? Reason)
{
    public static FilterDecision Accept() => new(FilterAction.Accept, null, null);
    public static FilterDecision Reject(string reason) => new(FilterAction.Reject, null, reason);
    public static FilterDecision Clamp(double newVolume, string reason)
        => new(FilterAction.Clamp, newVolume, reason);
}

public enum FilterAction { Accept, Reject, Clamp }
