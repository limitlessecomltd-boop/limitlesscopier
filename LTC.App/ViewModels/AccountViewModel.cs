using CommunityToolkit.Mvvm.ComponentModel;
using LTC.Core.Connections;
using LTC.Core.Models;

namespace LTC.App.ViewModels;

/// <summary>
/// View model for one account row. Backed by a live <see cref="IBrokerConnection"/>;
/// updates connection status and balance fields when the engine raises events.
/// </summary>
public partial class AccountViewModel : ObservableObject
{
    public Account Model { get; }
    public IBrokerConnection? Connection { get; private set; }

    [ObservableProperty] private string displayName = "";
    [ObservableProperty] private ulong login;
    [ObservableProperty] private string brokerLabel = "";
    [ObservableProperty] private AccountRole role;
    [ObservableProperty] private ConnectionStatus status = ConnectionStatus.Disconnected;
    [ObservableProperty] private string statusText = "Disconnected";
    [ObservableProperty] private string statusColorKey = "TextDimBrush";

    /// <summary>Account balance in deposit currency (placeholder until equity events land).</summary>
    [ObservableProperty] private double balance;
    [ObservableProperty] private double dailyPnl;
    [ObservableProperty] private string symbolCountText = "—";
    [ObservableProperty] private bool isSelected;

    // Live account stats — populated by AccountStatsPoller via MainViewModel.
    // Until the first poll arrives these stay at default; HasStats gates the
    // stats line in XAML so we never show "—" rows that confuse the user.
    [ObservableProperty] private double equity;
    [ObservableProperty] private double margin;
    [ObservableProperty] private double freeMargin;
    [ObservableProperty] private double marginLevelPercent;
    [ObservableProperty] private double floatingProfit;
    [ObservableProperty] private string currency = "";
    [ObservableProperty] private bool hasStats;

    // ============================================================
    // PROP FIRM RISK STATE (Tier 2 — visible meters)
    // ============================================================
    //
    // RiskState is recomputed whenever equity changes for accounts that
    // have PropConfig set. Personal accounts skip the calculation and
    // leave RiskState=null (UI uses HasPropRisk to gate visibility).
    //
    // Daily anchor strategy: this initial release uses the CURRENT BALANCE
    // as the daily anchor proxy. This is intentionally permissive —
    // showing "you've used 60% of daily loss" when you actually have a
    // floating loss of 60% is better than showing nothing. A future Tier 3
    // will add proper PropDailyAnchor persistence that resets at the
    // firm's exact reset hour and survives app restarts.
    //
    // High-water mark for trailing drawdown: similarly uses balance as
    // a proxy. Real implementation needs end-of-day equity tracking,
    // which is Tier 3 too.

    /// <summary>True iff this account has a PropFirmConfig attached
    /// (i.e. Kind is PropChallenge or PropFunded). Drives visibility
    /// of the risk meter in the Accounts tab.</summary>
    public bool HasPropRisk => Model.Kind != LTC.Core.Models.AccountKind.Personal
                            && Model.PropConfig is not null;

    /// <summary>The most-recent risk calculation, or null if not a prop
    /// account / no stats yet. UI binds to derived properties below.</summary>
    public LTC.Core.Risk.PropRiskState? RiskState { get; private set; }

    /// <summary>e.g. "62.4%" — formatted daily-loss usage for display.</summary>
    public string DailyPercentText =>
        RiskState is null ? "—" : $"{RiskState.DailyPercent:0.0}%";

    /// <summary>e.g. "$1,420.55" — dollar headroom remaining today.</summary>
    public string DailyHeadroomText =>
        RiskState is null ? "—" : $"{RiskState.DailyHeadroom:N2} {Currency}".Trim();

    public string OverallPercentText =>
        RiskState is null ? "—" : $"{RiskState.OverallPercent:0.0}%";

    public string OverallHeadroomText =>
        RiskState is null ? "—" : $"{RiskState.OverallHeadroom:N2} {Currency}".Trim();

    /// <summary>Daily usage clamped to 0-100 so the progress bar never
    /// overshoots its track even on a freak negative-equity moment.</summary>
    public double DailyMeterValue =>
        RiskState is null ? 0 : Math.Clamp((double)RiskState.DailyPercent, 0, 100);

    public double OverallMeterValue =>
        RiskState is null ? 0 : Math.Clamp((double)RiskState.OverallPercent, 0, 100);

    /// <summary>Resource key for the daily meter fill color. Green under 50%,
    /// amber 50-75%, red 75-90%, deep red over 90%. Drives the "you're in
    /// danger" visual cue without needing text.</summary>
    public string DailyMeterColorKey => MeterColorForPercent(RiskState?.DailyPercent);
    public string OverallMeterColorKey => MeterColorForPercent(RiskState?.OverallPercent);

    private static string MeterColorForPercent(decimal? pct)
    {
        if (pct is null) return "TextDimBrush";
        if (pct >= 90m)  return "StatusErrBrush";     // red — critical
        if (pct >= 75m)  return "StatusWarnBrush";    // amber — warning
        if (pct >= 50m)  return "BrandBrush";         // brand — caution
        return "StatusOkBrush";                       // green — safe
    }

    /// <summary>Recompute risk state. Called from OnEquityChanged so the
    /// meter updates whenever the broker pushes new account stats.</summary>
    private void RecomputePropRisk()
    {
        if (!HasPropRisk || !HasStats || Model.PropConfig is null)
        {
            RiskState = null;
            NotifyPropMetricsChanged();
            return;
        }

        try
        {
            // Daily anchor: equity snapshot taken at the most recent reset
            // crossing (or at first observation if no crossing has happened
            // this session). This is what stays STABLE through the day so
            // that closing a winning trade improves the daily-loss cushion
            // — same way it improves the overall-loss cushion.
            //
            // We previously also used a history-derived anchor
            // (HistoryDailyAnchor = starting_balance + sum of P&L closed
            // before today's reset). It was off whenever the history feed
            // lagged the live balance — which is exactly what happens
            // right after a trade closes — and the user reported that
            // closing a profit didn't reduce daily-loss-used the way it
            // reduces overall-loss-used. Root cause: the live currentEquity
            // was up by the close's P&L, but `dailyAnchor - currentEquity`
            // didn't change because the anchor was also bumped up by the
            // same history-derived sum. Net effect: daily meter ignored
            // intra-day P&L. The fix is to use the recorder's snapshot,
            // which is taken ONCE at app start / reset crossing and held
            // stable thereafter — so closing a profit lifts equity above
            // anchor, reducing daily-loss-used immediately.
            //
            // Fallback: current balance, so a brand-new account with no
            // recorder snapshot yet doesn't show a broken meter.
            var snapshot = _anchorRecorder?.Get(Model.Login);
            var dailyAnchor =
                snapshot?.Equity
                ?? (decimal)Balance;
            var hwm = snapshot?.HighWaterMark ?? (decimal)Balance;

            RiskState = LTC.Core.Risk.PropRiskCalculator.Compute(
                Model.PropConfig,
                currentEquity:        (decimal)Equity,
                dailyAnchorEquity:    dailyAnchor,
                highWaterMarkEquity:  hwm);
        }
        catch
        {
            // If the calc throws (shouldn't, but defensively) just leave
            // the meter blank rather than showing wrong numbers.
            RiskState = null;
        }

        NotifyPropMetricsChanged();
    }

    private void NotifyPropMetricsChanged()
    {
        OnPropertyChanged(nameof(RiskState));
        OnPropertyChanged(nameof(DailyPercentText));
        OnPropertyChanged(nameof(DailyHeadroomText));
        OnPropertyChanged(nameof(OverallPercentText));
        OnPropertyChanged(nameof(OverallHeadroomText));
        OnPropertyChanged(nameof(DailyMeterValue));
        OnPropertyChanged(nameof(OverallMeterValue));
        OnPropertyChanged(nameof(DailyMeterColorKey));
        OnPropertyChanged(nameof(OverallMeterColorKey));
    }

    /// <summary>Apply a fresh stats snapshot. Called by MainViewModel on the UI thread.</summary>
    public void ApplyStats(LTC.Core.Connections.AccountStats s)
    {
        Balance              = s.Balance;
        Equity               = s.Equity;
        Margin               = s.Margin;
        FreeMargin           = s.FreeMargin;
        MarginLevelPercent   = s.MarginLevelPercent;
        FloatingProfit       = s.Profit;
        Currency             = s.Currency;
        HasStats             = true;
        // Equity setter above already triggers RecomputePropRisk via its
        // partial method handler, but we also call here explicitly so
        // accounts that get their FIRST stats poll (HasStats false→true)
        // pick up risk meters even if Equity itself didn't change value.
        RecomputePropRisk();
    }

    /// <summary>Reference to the app-wide daily anchor recorder, if available.
    /// MainViewModel passes its own recorder in. When null (e.g. tests), the
    /// VM falls back to using current balance as the anchor proxy, which was
    /// the v1 behavior — still correct for static-balance drawdown, slightly
    /// off for trailing.</summary>
    private readonly LTC.Core.Risk.DailyAnchorRecorder? _anchorRecorder;

    /// <summary>The daily anchor equity COMPUTED FROM CLOSED-DEAL HISTORY.
    /// Set by PropJournalViewModel after each successful history pull;
    /// null when history isn't available (no connection, no challenge
    /// start configured, etc.) or hasn't loaded yet.
    ///
    /// Anchor priority used by RecomputePropRisk():
    ///   1. HistoryDailyAnchor (BEST — from real deal sums up to reset hour)
    ///   2. _anchorRecorder snapshot (GOOD — equity at app start)
    ///   3. Current balance (FALLBACK — approximate, the original Tier 2 behavior)
    /// </summary>
    public decimal? HistoryDailyAnchor { get; private set; }

    /// <summary>Trading-days count from closed-deal history. Set by
    /// PropJournalViewModel after each history pull. The per-row prop
    /// meter doesn't display this directly (Accounts tab doesn't have
    /// a trading-days column) but the Prop Journal tab does.</summary>
    public int HistoryTradingDays { get; private set; }

    /// <summary>Realized P&amp;L sum across the entire challenge — used
    /// by the Profit Target meter.</summary>
    public decimal HistoryRealizedPnl { get; private set; }

    /// <summary>Set by PropJournalViewModel after a history pull.
    /// Pushes the deal-derived values onto the VM and triggers a meter
    /// recompute so the per-row meters and Prop Journal stay in sync.</summary>
    public void ApplyHistoryDerived(
        decimal? historyDailyAnchor,
        int     tradingDays,
        decimal realizedPnl)
    {
        HistoryDailyAnchor  = historyDailyAnchor;
        HistoryTradingDays  = tradingDays;
        HistoryRealizedPnl  = realizedPnl;
        OnPropertyChanged(nameof(HistoryDailyAnchor));
        OnPropertyChanged(nameof(HistoryTradingDays));
        OnPropertyChanged(nameof(HistoryRealizedPnl));
        RecomputePropRisk();
    }

    public AccountViewModel(Account model, LTC.Core.Risk.DailyAnchorRecorder? anchorRecorder = null)
    {
        Model = model;
        _anchorRecorder = anchorRecorder;
        DisplayName = model.DisplayName;
        Login = model.Login;
        BrokerLabel = model.BrokerLabel ?? "";
        Role = model.Role;
    }

    /// <summary>Bind a live connection so this view model receives status updates.</summary>
    public void AttachConnection(IBrokerConnection connection)
    {
        Connection = connection;
        Status = connection.Status;
        UpdateStatusDisplay();
        connection.StatusChanged += (_, s) =>
        {
            // The DLL fires StatusChanged on a worker thread; marshal to the UI thread
            // so the resulting INotifyPropertyChanged invocations don't cross threads.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                Status = s;
                UpdateStatusDisplay();
                SymbolCountText = connection.AvailableSymbols.Count > 0
                    ? $"{connection.AvailableSymbols.Count} symbols"
                    : "—";
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    Status = s;
                    UpdateStatusDisplay();
                    SymbolCountText = connection.AvailableSymbols.Count > 0
                        ? $"{connection.AvailableSymbols.Count} symbols"
                        : "—";
                }));
            }
        };
    }

    private void UpdateStatusDisplay()
    {
        (StatusText, StatusColorKey) = Status switch
        {
            ConnectionStatus.Connected     => ("Connected", "StatusOkBrush"),
            ConnectionStatus.Connecting    => ("Connecting…", "StatusWarnBrush"),
            ConnectionStatus.Reconnecting  => ("Reconnecting…", "StatusWarnBrush"),
            ConnectionStatus.Disconnected  => ("Disconnected", "TextDimBrush"),
            ConnectionStatus.Failed        => ("Failed", "StatusErrBrush"),
            _                              => ("Unknown", "TextDimBrush"),
        };
    }

    /// <summary>e.g. "+2.40%" or "−0.32%" with proper sign formatting.</summary>
    public string DailyPnlText
    {
        get
        {
            if (DailyPnl == 0) return "—";
            var sign = DailyPnl >= 0 ? "+" : "−";
            return $"{sign}{Math.Abs(DailyPnl):F2}%";
        }
    }

    /// <summary>Color key for P&L: green if positive, red if negative.</summary>
    public string DailyPnlColorKey =>
        DailyPnl > 0 ? "StatusOkBrush" : DailyPnl < 0 ? "StatusErrBrush" : "TextDimBrush";

    /// <summary>e.g. "1234.56 USD". Reads "—" until the first stats poll arrives.</summary>
    public string BalanceText =>
        HasStats ? $"{Balance:0.00} {Currency}".Trim() : "—";

    /// <summary>e.g. "1230.40 USD". Reads "—" until the first stats poll arrives.</summary>
    public string EquityText =>
        HasStats ? $"{Equity:0.00} {Currency}".Trim() : "—";

    /// <summary>Margin level as percentage. "—" when no positions are open
    /// (margin = 0 makes the percentage undefined).</summary>
    public string MarginLevelText =>
        !HasStats ? "—"
        : Margin > 0 ? $"{MarginLevelPercent:0}%"
        : "—";

    /// <summary>Floating P&L with explicit sign so positive values stand out.</summary>
    public string FloatingProfitText =>
        !HasStats ? "—"
        : (FloatingProfit >= 0 ? "+" : "") + FloatingProfit.ToString("0.00");

    /// <summary>Color key for floating P&L: green for positive, red for negative.</summary>
    public string FloatingProfitColorKey =>
        FloatingProfit > 0 ? "StatusOkBrush"
        : FloatingProfit < 0 ? "StatusErrBrush"
        : "TextDimBrush";

    public bool IsMaster => Role == AccountRole.Master;
    public bool IsSlave => Role == AccountRole.Slave;

    partial void OnDailyPnlChanged(double value)
    {
        OnPropertyChanged(nameof(DailyPnlText));
        OnPropertyChanged(nameof(DailyPnlColorKey));
    }

    // Recompute the formatted strings whenever any underlying field changes.
    // The MVVM source generator turns each [ObservableProperty] into a property
    // and lets us hook a partial method per field.
    partial void OnEquityChanged(double value)
    {
        OnPropertyChanged(nameof(EquityText));
        RecomputePropRisk();
    }

    partial void OnBalanceChanged(double value)
    {
        OnPropertyChanged(nameof(BalanceText));
        // Balance changes shift the daily anchor proxy (which IS balance
        // for now). Recompute so the meter stays consistent.
        RecomputePropRisk();
    }

    partial void OnMarginChanged(double value)
    {
        // Margin going to 0 changes MarginLevelText display logic.
        OnPropertyChanged(nameof(MarginLevelText));
    }

    partial void OnMarginLevelPercentChanged(double value)
    {
        OnPropertyChanged(nameof(MarginLevelText));
    }

    partial void OnFloatingProfitChanged(double value)
    {
        OnPropertyChanged(nameof(FloatingProfitText));
        OnPropertyChanged(nameof(FloatingProfitColorKey));
    }

    partial void OnCurrencyChanged(string value)
    {
        // Currency is part of the formatted Balance/Equity strings.
        OnPropertyChanged(nameof(BalanceText));
        OnPropertyChanged(nameof(EquityText));
    }

    partial void OnHasStatsChanged(bool value)
    {
        // Toggling HasStats flips every formatted string between "—" and a real value.
        OnPropertyChanged(nameof(BalanceText));
        OnPropertyChanged(nameof(EquityText));
        OnPropertyChanged(nameof(MarginLevelText));
        OnPropertyChanged(nameof(FloatingProfitText));
    }

    /// <summary>
    /// Re-fire PropertyChanged for every brush-key property so KeyToBrush
    /// converter bindings re-resolve against the current theme. Called by
    /// MainViewModel when the user toggles dark/light.
    /// </summary>
    public void NotifyColorKeysChanged()
    {
        OnPropertyChanged(nameof(StatusColorKey));
        OnPropertyChanged(nameof(DailyPnlColorKey));
        OnPropertyChanged(nameof(FloatingProfitColorKey));
    }
}
