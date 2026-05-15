using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LTC.Core.Connections;
using LTC.Core.Models;
using LTC.Core.Risk;

namespace LTC.App.ViewModels;

/// <summary>
/// View model for the Prop Journal tab. Maintains:
///
///   - PropAccounts: collection of all prop firm accounts across master + slave
///     (filtered from MainViewModel's Masters + Slaves)
///   - SelectedAccount: currently-viewed account (drives all the right-side cards)
///   - StatusHeadline / StatusBody / StatusSeverity: top banner content
///   - Stats: aggregated trade stats for the selected account (today/challenge/consistency)
///   - ResetCountdownText: live countdown to next daily reset
///
/// The VM listens to MainViewModel's collection changes so adding/removing prop
/// accounts shows up immediately, and listens to the selected account's
/// PropertyChanged so when the broker pushes new stats the banner/cards refresh.
///
/// Threading: all updates happen on the UI thread (this VM is always accessed
/// from XAML). The countdown DispatcherTimer ticks every second on the UI
/// thread. AccountViewModel.PropertyChanged events also fire on the UI thread
/// (MainViewModel marshals them via _uiDispatcher).
/// </summary>
public partial class PropJournalViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _countdownTimer;
    private readonly DispatcherTimer _historyTimer;

    /// <summary>Track in-flight history fetches per account to avoid stacking
    /// requests if MT5 is slow to respond. Key = account Id.</summary>
    private readonly System.Collections.Generic.HashSet<Guid> _historyInFlight = new();

    /// <summary>Accounts whose CloseAllOnTargetHit has already fired this
    /// session. Prevents the close-all from firing repeatedly while
    /// positions are being closed and the equity dances around target.</summary>
    private readonly System.Collections.Generic.HashSet<Guid> _autoCloseFiredOnTarget = new();

    public ObservableCollection<AccountViewModel> PropAccounts { get; } = new();

    [ObservableProperty] private AccountViewModel? selectedAccount;

    // ---------- Status banner ----------
    [ObservableProperty] private string statusHeadline = "Loading…";
    [ObservableProperty] private string statusBody = "";
    /// <summary>"ok" | "caution" | "danger" — drives the banner color theme.</summary>
    [ObservableProperty] private string statusSeverity = "ok";

    // ---------- Live countdown ----------
    [ObservableProperty] private string resetCountdownText = "—";
    [ObservableProperty] private string resetCountdownLabel = "until next daily reset";

    // ---------- Trade stats (Today / Challenge / Consistency) ----------
    [ObservableProperty] private PropTradeStats? stats;

    partial void OnStatsChanged(PropTradeStats? value)
    {
        OnPropertyChanged(nameof(MinTradingDaysText));
        OnPropertyChanged(nameof(TradingDaysProgress));
        OnPropertyChanged(nameof(ProfitTargetPercentText));
        OnPropertyChanged(nameof(ProfitTargetDescription));
        OnPropertyChanged(nameof(ProfitTargetProgress));
        OnPropertyChanged(nameof(ProfitTargetFooterEarned));
        OnPropertyChanged(nameof(ProfitTargetFooterTarget));
        RefreshConsistency();
    }

    public PropJournalViewModel(MainViewModel main)
    {
        _main = main;

        // Keep PropAccounts in sync with master/slave collections.
        _main.Masters.CollectionChanged += OnSourceChanged;
        _main.Slaves.CollectionChanged += OnSourceChanged;
        RebuildPropAccounts();
        AutoSelectIfNeeded();

        // 1-second timer for the reset countdown. Cheap, always on,
        // also a convenient hook to refresh status banner text in case
        // we missed a stats event somewhere.
        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += (_, _) => RefreshLiveFields();
        _countdownTimer.Start();

        // 10-second timer for the history poll. Pulling closed-deal history
        // from MT5 is moderately expensive (round-trip to the broker), so
        // we poll on the slow path and let the 1-second UI tick read the
        // already-stored Stats. The Mt5BrokerConnection has its own 30-second
        // cache so this 10s tick is mostly free except every 3rd time.
        _historyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _historyTimer.Tick += (_, _) => FireHistoryPullForAllAccounts();
        _historyTimer.Start();

        // Kick off the first pull immediately so the user doesn't wait 10s
        // for trading-days / profit-target to show real numbers.
        FireHistoryPullForAllAccounts();
    }

    /// <summary>Schedule one async history fetch per prop account. Each
    /// completes independently; if any one is still in-flight we skip it
    /// (don't queue duplicates). UI updates happen via ApplyHistoryDerived
    /// on the captured account VM.</summary>
    private void FireHistoryPullForAllAccounts()
    {
        foreach (var acct in PropAccounts)
        {
            FireHistoryPullForAccount(acct);
        }
    }

    private async void FireHistoryPullForAccount(AccountViewModel acct)
    {
        if (acct.Model.PropConfig is null) return;

        var key = acct.Model.Id;
        lock (_historyInFlight)
        {
            if (!_historyInFlight.Add(key)) return;   // already running
        }

        try
        {
            var conn = _main.Engine.Connections.Get(acct.Model.Id);
            if (conn is null) return;
            if (conn.Status != ConnectionStatus.Connected) return;

            // Stable since-date so the broker connection's cache key
            // doesn't change on every call. We always query from the
            // challenge start (if set), or from 30 days ago rounded to
            // midnight UTC. Without rounding, DateTime.UtcNow.AddDays(-30)
            // changes every microsecond which busts the 30s cache.
            var since = acct.Model.PropConfig.ChallengeStartDateUtc?.Date
                        ?? DateTime.UtcNow.Date.AddDays(-30);
            var deals = await conn.GetClosedDealsAsync(since).ConfigureAwait(true);

            // Aggregate
            var agg = PropStatsAggregator.Aggregate(
                deals,
                acct.Model.PropConfig.ChallengeStartDateUtc,
                acct.Model.PropConfig.DailyResetUtc);

            // Compute the proper daily anchor from deal sums
            var anchor = PropStatsAggregator.ComputeDailyAnchorEquity(
                deals,
                acct.Model.PropConfig.StartingBalance,
                acct.Model.PropConfig.DailyResetUtc);

            // Push into AccountViewModel — triggers RecomputePropRisk
            // which updates per-row meters and the Prop Journal UI.
            acct.ApplyHistoryDerived(
                historyDailyAnchor: anchor,
                tradingDays:        agg.ChallengeTradingDays,
                realizedPnl:        agg.ChallengeRealizedPnl);

            // If this is the currently-selected account, store the aggregate
            // so the Prop Journal cards bind to it.
            if (SelectedAccount?.Model.Id == acct.Model.Id)
            {
                Stats = agg;
                // Banner depends on RiskState which depends on the new anchor,
                // and ApplyHistoryDerived above already triggered the cascade.
                RefreshStatusBanner();
            }
        }
        catch (Exception ex)
        {
            // Don't surface to the UI — history is best-effort.
            System.Diagnostics.Debug.WriteLine($"history pull failed for {acct.Model.Login}: {ex.Message}");
        }
        finally
        {
            lock (_historyInFlight) { _historyInFlight.Remove(key); }
        }
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildPropAccounts();
        AutoSelectIfNeeded();
    }

    private void RebuildPropAccounts()
    {
        // Snapshot the current selected so we can preserve selection across rebuilds
        var prevSelectedLogin = SelectedAccount?.Model.Login;

        PropAccounts.Clear();
        foreach (var a in _main.Masters)
            if (a.HasPropRisk) PropAccounts.Add(a);
        foreach (var a in _main.Slaves)
            if (a.HasPropRisk) PropAccounts.Add(a);

        if (prevSelectedLogin is ulong login)
        {
            SelectedAccount = PropAccounts.FirstOrDefault(a => a.Model.Login == login);
        }
    }

    private void AutoSelectIfNeeded()
    {
        if (SelectedAccount is null && PropAccounts.Count > 0)
        {
            SelectedAccount = PropAccounts[0];
        }
    }

    partial void OnSelectedAccountChanged(AccountViewModel? oldValue, AccountViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnAccountChanged;
        if (newValue is not null)
        {
            newValue.PropertyChanged += OnAccountChanged;
            RefreshAllForCurrentAccount();
        }
        else
        {
            // Nothing selected (e.g. user deleted last prop account)
            StatusHeadline = "No prop firm accounts.";
            StatusBody = "Add a Prop Challenge or Prop Funded account to start tracking limits.";
            StatusSeverity = "ok";
            Stats = null;
        }
    }

    private void OnAccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        // When the broker pushes new stats, AccountViewModel raises a bunch
        // of property change events EVERY SECOND. We MUST NOT trigger a
        // history pull on each — that's network-expensive and the cache
        // can't help because the timing is identical to the 30s window.
        //
        // History pulls happen only via:
        //   1. The 10-second _historyTimer (FireHistoryPullForAllAccounts)
        //   2. Account selection change (RefreshStats inside OnSelectedAccountChanged)
        //   3. BALANCE CHANGES on the selected account — see below
        //
        // For non-balance changes, we just keep the banner + live fields fresh.
        if (e.PropertyName is nameof(AccountViewModel.Balance)
            && sender is AccountViewModel changed)
        {
            // Balance only moves when a trade closes (or on deposit/withdrawal,
            // which we don't filter — a fresh history pull is fine either way).
            // Mt5BrokerConnection invalidates its deal cache on close events
            // BEFORE balance hits us, so this pull will see the freshly-closed
            // trade and the profit target / consistency / trading-days numbers
            // update within ~1 second of the close instead of waiting 10s.
            FireHistoryPullForAccount(changed);
            return;
        }

        if (e.PropertyName is nameof(AccountViewModel.RiskState)
            or nameof(AccountViewModel.Equity)
            or nameof(AccountViewModel.FloatingProfit))
        {
            RefreshStatusBanner();
            RefreshLiveFields();
        }
    }

    private void RefreshAllForCurrentAccount()
    {
        RefreshStatusBanner();
        RefreshStats();
        RefreshLiveFields();
        // Account-level switch: HasMaxLossPerTradeConfigured + the dollar
        // limit can change. Force a re-bind even if the live fields tick
        // hasn't fired yet.
        OnPropertyChanged(nameof(HasMaxLossPerTradeConfigured));
        OnPropertyChanged(nameof(MaxLossPerTradeDollars));
    }

    /// <summary>Per-second tick — updates countdown + occasionally re-derives status.</summary>
    private void RefreshLiveFields()
    {
        if (SelectedAccount?.Model.PropConfig is not null)
        {
            var resetHour = SelectedAccount.Model.PropConfig.DailyResetUtc
                            ?? new TimeOnly(0, 0);
            ResetCountdownText  = ComputeResetCountdownText(resetHour);
            ResetCountdownLabel = $"until daily reset at {resetHour:HH:mm} UTC";
        }

        // Re-evaluate per-trade-risk meter every tick. The worst-position
        // loss changes continuously as floating P&L moves on each tick, so
        // we tell WPF to re-bind these every second. Cheap — just N
        // PropertyChanged notifications per second.
        OnPropertyChanged(nameof(WorstOpenPositionLoss));
        OnPropertyChanged(nameof(MaxLossPerTradePercentText));
        OnPropertyChanged(nameof(MaxLossPerTradeColorKey));
        OnPropertyChanged(nameof(MaxLossPerTradeDescription));
        OnPropertyChanged(nameof(MaxLossPerTradeProgress));

        // Same for the profit target — it now includes floating P&L
        // (which changes every tick as positions move).
        OnPropertyChanged(nameof(ProfitTargetTotalPnl));
        OnPropertyChanged(nameof(ProfitTargetPercentText));
        OnPropertyChanged(nameof(ProfitTargetDescription));
        OnPropertyChanged(nameof(ProfitTargetProgress));
        OnPropertyChanged(nameof(ProfitTargetFooterEarned));
        OnPropertyChanged(nameof(IsTargetReached));

        // Consistency rule — today's projection includes floating P&L, so
        // the description updates as floating moves. The bar chart itself
        // only changes when closed deals come in (10s history poll), but
        // RefreshConsistency() is cheap enough at 20 bars max to call
        // every second.
        RefreshConsistency();

        // Check auto-close-on-target trigger (one-shot per session)
        CheckAutoCloseOnTarget();
    }

    private void RefreshStatusBanner()
    {
        var risk = SelectedAccount?.RiskState;
        if (risk is null)
        {
            StatusHeadline = "Awaiting account data…";
            StatusBody = "Waiting for the first equity reading. This usually takes a few seconds after the account connects.";
            StatusSeverity = "ok";
            return;
        }

        var ccy = SelectedAccount?.Currency ?? "";

        // Decide severity from the most-at-risk meter
        var worstPct = Math.Max(risk.DailyPercent, risk.OverallPercent);
        var headroom = risk.ClosestHeadroom;

        if (worstPct >= 90m)
        {
            StatusSeverity = "danger";
            StatusHeadline = "CRITICAL — you're at the edge of breach.";
            StatusBody = risk.ClosestIsDaily
                ? $"Daily loss is at {risk.DailyPercent:0.0}%. Any additional loss likely breaches the rule and closes the account. " +
                  $"Headroom: {risk.DailyHeadroom:N2} {ccy}."
                : $"Overall drawdown is at {risk.OverallPercent:0.0}%. Closing positions now is the safest move. " +
                  $"Headroom: {risk.OverallHeadroom:N2} {ccy}.";
        }
        else if (worstPct >= 60m)
        {
            StatusSeverity = "caution";
            StatusHeadline = risk.ClosestIsDaily
                ? "You're approaching today's daily-loss limit."
                : "You're approaching the overall drawdown limit.";
            StatusBody = risk.ClosestIsDaily
                ? $"You've used {risk.DailyPercent:0.0}% of your daily allowance. " +
                  $"If you lose another {risk.DailyHeadroom:N2} {ccy}, the account breaches today's rule. " +
                  $"Consider stepping back from new trades."
                : $"Overall drawdown is at {risk.OverallPercent:0.0}%. " +
                  $"You're {risk.OverallHeadroom:N2} {ccy} from the floor. Tighten stops or reduce size.";
        }
        else
        {
            StatusSeverity = "ok";
            StatusHeadline = "You're well within your limits.";
            StatusBody = $"Daily loss used: {risk.DailyPercent:0.0}%. " +
                         $"Overall drawdown used: {risk.OverallPercent:0.0}%. " +
                         $"Trade with confidence and respect your plan.";
        }
    }

    private void RefreshStats()
    {
        if (SelectedAccount?.Model.PropConfig is null)
        {
            Stats = null;
            return;
        }
        // Trigger a fresh history pull for the currently-selected account.
        // The async handler will set Stats when the result lands. We don't
        // wait for it — the existing Stats (or null) shows in the meantime.
        FireHistoryPullForAccount(SelectedAccount);
    }

    /// <summary>
    /// Auto-close-on-target safety check. Called every 1s tick. If the
    /// currently-selected account has CloseAllOnTargetHit enabled AND
    /// the realized+floating P&amp;L has met or exceeded the target AND
    /// we haven't already fired this session, kick off CloseAllOnAccount.
    ///
    /// One-shot per session — once fired, this account is added to a
    /// HashSet so we don't loop on every tick while positions close.
    /// </summary>
    /// <summary>Called by the view when an in-meter setting (e.g.
    /// the auto-close-on-target checkbox) is toggled. Persists the
    /// SelectedAccount's PropConfig changes to disk via MainViewModel.
    /// Also resets the one-shot guard so the new setting can fire.</summary>
    public void PersistSelectedAccount()
    {
        if (SelectedAccount is null) return;
        _main.UpdateAccountInPlace(SelectedAccount);
        // If the user just unchecked the setting, allow re-firing if they
        // re-enable. If they enabled, allow firing now.
        _autoCloseFiredOnTarget.Remove(SelectedAccount.Model.Id);
    }

    private void CheckAutoCloseOnTarget()
    {
        var acct = SelectedAccount;
        if (acct is null) return;
        var cfg = acct.Model.PropConfig;
        if (cfg is null) return;
        if (!cfg.CloseAllOnTargetHit) return;
        if (cfg.ProfitTarget is not decimal target || target <= 0) return;
        if (!IsTargetReached) return;
        if (_autoCloseFiredOnTarget.Contains(acct.Model.Id)) return;

        _autoCloseFiredOnTarget.Add(acct.Model.Id);
        _ = _main.CloseAllOnAccountAsync(acct.Model.Id);
    }

    private static string ComputeResetCountdownText(TimeOnly resetHourUtc)
    {
        var nowUtc = DateTime.UtcNow;
        var nowTod = TimeOnly.FromDateTime(nowUtc);

        // Build the next reset datetime in UTC
        DateTime nextReset;
        if (nowTod >= resetHourUtc)
            nextReset = nowUtc.Date.AddDays(1).Add(resetHourUtc.ToTimeSpan());
        else
            nextReset = nowUtc.Date.Add(resetHourUtc.ToTimeSpan());

        var remaining = nextReset - nowUtc;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h {remaining.Minutes:00}m";
        return $"{remaining.Minutes:00}m {remaining.Seconds:00}s";
    }

    public bool HasAnyPropAccount => PropAccounts.Count > 0;

    // --------- Convenience computed properties for XAML binding ---------

    public string FirmName => SelectedAccount?.Model.PropConfig?.FirmName ?? "—";
    public string CurrencyCode => SelectedAccount?.Currency ?? "";
    public bool IsChallenge =>
        SelectedAccount?.Model.PropConfig?.Phase != PropFirmPhase.Funded;

    public string DaysRemainingInChallengeText
    {
        get
        {
            var cfg = SelectedAccount?.Model.PropConfig;
            if (cfg?.MaxChallengePeriodDays is not int max || max <= 0) return "—";
            var start = cfg.ChallengeStartDateUtc ?? DateTime.UtcNow.Date;
            var elapsed = (DateTime.UtcNow.Date - start.Date).TotalDays;
            var remaining = max - (int)elapsed;
            if (remaining < 0) remaining = 0;
            return $"{max} days ({remaining} left)";
        }
    }

    public string MinTradingDaysText
    {
        get
        {
            var cfg = SelectedAccount?.Model.PropConfig;
            if (cfg?.MinTradingDays is not int min || min <= 0) return "—";
            var done = Stats?.ChallengeTradingDays ?? 0;
            return $"{done} / {min}";
        }
    }

    public double TradingDaysProgress
    {
        get
        {
            var cfg = SelectedAccount?.Model.PropConfig;
            if (cfg?.MinTradingDays is not int min || min <= 0) return 0;
            var done = Stats?.ChallengeTradingDays ?? 0;
            return Math.Clamp((double)done / min * 100.0, 0, 100);
        }
    }

    // -------------- Profit target meter --------------
    // Counts BOTH realized P&L (closed trades) AND floating P&L (open
    // positions) toward the target. This matches what every prop firm
    // dashboard shows. Updates in real-time as floating P&L moves.

    /// <summary>
    /// Total P&L credited toward the profit target. Computed as:
    ///
    ///     equity - starting_balance
    ///
    /// MT5 maintains `equity = balance + floating` as an invariant, so this
    /// is identical to "realized + floating" but with one less moving part.
    /// Read straight off the live broker poll (api.AccountEquity), no
    /// dependency on history sync. Continuous across close events because
    /// equity doesn't move when a position closes (balance jumps up by the
    /// realized P&L, floating drops by the same amount, equity unchanged).
    /// </summary>
    public decimal ProfitTargetTotalPnl
    {
        get
        {
            var acct = SelectedAccount;
            var cfg = acct?.Model.PropConfig;
            if (acct is null || cfg is null || cfg.StartingBalance <= 0) return 0m;
            return (decimal)acct.Equity - cfg.StartingBalance;
        }
    }

    public string ProfitTargetPercentText
    {
        get
        {
            var cfg = SelectedAccount?.Model.PropConfig;
            if (cfg?.ProfitTarget is not decimal target || target <= 0) return "—";
            var total = ProfitTargetTotalPnl;
            if (total <= 0) return "0%";
            var pct = (total / target) * 100m;
            if (pct > 999m) pct = 999m;
            return $"{pct:F0}%";
        }
    }

    public string ProfitTargetDescription
    {
        get
        {
            var cfg = SelectedAccount?.Model.PropConfig;
            if (cfg?.ProfitTarget is not decimal target || target <= 0)
                return "No profit target configured for this account.";
            var total = ProfitTargetTotalPnl;
            var remaining = target - total;
            var ccy = SelectedAccount?.Currency ?? "";

            if (remaining <= 0)
                return $"🎉 Target reached. Total P&L: {total:N2} {ccy} (balance + floating − starting balance).";

            return $"Need {remaining:N2} {ccy} more to hit the target. " +
                   $"Total P&L so far: {total:N2} {ccy}.";
        }
    }

    public double ProfitTargetProgress
    {
        get
        {
            var cfg = SelectedAccount?.Model.PropConfig;
            if (cfg?.ProfitTarget is not decimal target || target <= 0) return 0;
            var total = ProfitTargetTotalPnl;
            if (total <= 0) return 0;
            var pct = (double)(total / target) * 100.0;
            return Math.Clamp(pct, 0, 100);
        }
    }

    public string ProfitTargetFooterEarned
    {
        get
        {
            return ProfitTargetTotalPnl.ToString("+#,##0.00;-#,##0.00;0.00");
        }
    }

    public string ProfitTargetFooterTarget
    {
        get
        {
            var cfg = SelectedAccount?.Model.PropConfig;
            if (cfg?.ProfitTarget is not decimal target || target <= 0) return "—";
            return target.ToString("N2");
        }
    }

    /// <summary>True when the configured target has been (at least)
    /// reached. The meter UI shows the "close all trades" call-to-action
    /// when this is true, and the auto-close-on-target feature uses this
    /// to trigger an action.</summary>
    public bool IsTargetReached
    {
        get
        {
            var cfg = SelectedAccount?.Model.PropConfig;
            if (cfg?.ProfitTarget is not decimal target || target <= 0) return false;
            return ProfitTargetTotalPnl >= target;
        }
    }

    // -------------- Max loss per trade meter --------------
    // Walks current open positions on the selected account and finds the
    // worst-bleeding one. Compares it to the configured per-trade cap
    // (percent of starting balance).

    /// <summary>The absolute dollar limit per trade, or null if no
    /// per-trade rule is configured.</summary>
    public decimal? MaxLossPerTradeDollars
    {
        get
        {
            var cfg = SelectedAccount?.Model.PropConfig;
            if (cfg is null || cfg.StartingBalance <= 0) return null;
            if (cfg.MaxLossPerTradePercent is not decimal pct || pct <= 0) return null;
            return cfg.StartingBalance * (pct / 100m);
        }
    }

    /// <summary>The worst (most negative) floating P&L among currently
    /// open positions on the selected account. Returns 0 if no positions
    /// are losing.</summary>
    public decimal WorstOpenPositionLoss
    {
        get
        {
            if (SelectedAccount is null) return 0m;
            var positions = _main.Positions;
            if (positions is null || positions.Count == 0) return 0m;
            decimal worst = 0m;
            foreach (var p in positions)
            {
                if (p.AccountId != SelectedAccount.Model.Id) continue;
                if (p.Profit < (double)worst)
                    worst = (decimal)p.Profit;
            }
            return worst;   // 0 or negative number
        }
    }

    public bool HasMaxLossPerTradeConfigured =>
        MaxLossPerTradeDollars.HasValue;

    public string MaxLossPerTradePercentText
    {
        get
        {
            var limit = MaxLossPerTradeDollars;
            if (limit is null || limit <= 0) return "—";
            var worst = Math.Abs(WorstOpenPositionLoss);
            var pct = (worst / limit.Value) * 100m;
            if (pct > 999m) pct = 999m;
            // Show one decimal when the value is small (< 10%) so users
            // see "0.6%" instead of "1%" rounding artifact. Round to a
            // whole number once the meter starts approaching the limit
            // where decimal precision adds noise.
            return pct < 10m ? $"{pct:F1}%" : $"{pct:F0}%";
        }
    }

    public string MaxLossPerTradeColorKey
    {
        get
        {
            var limit = MaxLossPerTradeDollars;
            if (limit is null || limit <= 0) return "TextDimBrush";
            var worst = Math.Abs(WorstOpenPositionLoss);
            var pct = (worst / limit.Value) * 100m;
            if (pct >= 90m) return "StatusErrBrush";
            if (pct >= 75m) return "StatusWarnBrush";
            if (pct >= 50m) return "BrandBrush";
            return "StatusOkBrush";
        }
    }

    public string MaxLossPerTradeDescription
    {
        get
        {
            var limit = MaxLossPerTradeDollars;
            if (limit is null || limit <= 0)
                return "No max-per-trade limit configured.";
            var worst = WorstOpenPositionLoss;
            if (worst >= 0)
                return $"No open positions are losing right now. Limit: {limit:N2} {SelectedAccount?.Currency}.";
            var pctVal = (Math.Abs(worst) / limit.Value) * 100m;
            var pctFmt = pctVal < 10m ? $"{pctVal:F1}%" : $"{pctVal:F0}%";
            return $"Worst open trade: {worst:N2} {SelectedAccount?.Currency} " +
                   $"({pctFmt} of {limit:N2} limit).";
        }
    }

    public double MaxLossPerTradeProgress
    {
        get
        {
            var limit = MaxLossPerTradeDollars;
            if (limit is null || limit <= 0) return 0;
            var worst = Math.Abs(WorstOpenPositionLoss);
            return Math.Clamp((double)(worst / limit.Value) * 100.0, 0, 100);
        }
    }

    // ====================================================================
    // CONSISTENCY RULE
    // ====================================================================
    // Live bar chart of per-day P&L since challenge start, plus a status
    // line evaluating the firm's "no single day > X% of total profit" rule.
    //
    // Refreshes whenever:
    //   - Stats changes (history poll completed) — refreshes the bars
    //   - Selected account changes
    //
    // Anti-bug measures (per the user's "without any bug" requirement):
    //   - Bars are MATERIALIZED into an ObservableCollection on each refresh
    //     so WPF binding doesn't see a stale list while we're rebuilding
    //   - All numeric inputs are guarded against div/0
    //   - The "biggest absolute day" used for normalization is computed
    //     once per refresh, not per-bar (no jitter from rounding)
    //   - Negative-only days, positive-only days, and mixed days are all
    //     handled symmetrically — both halves of the chart are sized the
    //     same so the zero line is exactly in the middle

    public ObservableCollection<ConsistencyBarVm> ConsistencyBars { get; } = new();

    [ObservableProperty] private string consistencyHeadline = "—";
    [ObservableProperty] private string consistencyDescription =
        "No closed trades yet. Once you start closing trades, this section shows your daily P&L history and warns if one day dominates your overall profit (which prop firms penalize as 'inconsistency').";
    [ObservableProperty] private string consistencyColorKey = "TextDimBrush";
    [ObservableProperty] private string consistencyFirstDayLabel = "";
    [ObservableProperty] private string consistencyMiddleLabel = "";
    [ObservableProperty] private string consistencyLastDayLabel = "";

    /// <summary>Rebuild ConsistencyBars from the current Stats snapshot.
    /// Idempotent — safe to call multiple times. Called on every history
    /// pull completion.</summary>
    private void RefreshConsistency()
    {
        ConsistencyBars.Clear();

        var stats = Stats;
        var cfg = SelectedAccount?.Model.PropConfig;
        var ccy = SelectedAccount?.Currency ?? "";

        if (stats is null || stats.DailyHistory.Count == 0)
        {
            ConsistencyHeadline = "—";
            ConsistencyColorKey = "TextDimBrush";
            ConsistencyDescription = "No closed trades yet. Once you start closing trades, this section shows your daily P&L history and warns if one day dominates your overall profit.";
            ConsistencyFirstDayLabel = "";
            ConsistencyMiddleLabel = "";
            ConsistencyLastDayLabel = "";
            return;
        }

        // Show up to the last 20 trading days. More than that and individual
        // bars get unreadably narrow at typical window widths.
        var history = stats.DailyHistory;
        if (history.Count > 20) history = history.GetRange(history.Count - 20, 20);

        // Normalization: biggest absolute single-day P&L = full bar height.
        decimal biggestAbs = 0m;
        foreach (var d in history)
        {
            var abs = Math.Abs(d.RealizedPnl);
            if (abs > biggestAbs) biggestAbs = abs;
        }
        if (biggestAbs <= 0m) biggestAbs = 1m;   // avoid div/0 for an all-zero history

        // Half-panel height in DIPs. Chart is 120 high split half/half by the
        // zero line, so each side has 60. We leave 4px of safety margin so
        // the max bar doesn't touch the card edge.
        const double halfMaxHeight = 56.0;

        // The "biggest day" (by absolute value) gets a different fill color
        // to draw the eye to it — that's the day the consistency rule cares
        // about. Identify it by the original date.
        DateTime? biggestDay = null;
        decimal biggestProfit = decimal.MinValue;   // we want the biggest POSITIVE day
        foreach (var d in history)
        {
            if (d.RealizedPnl > biggestProfit)
            {
                biggestProfit = d.RealizedPnl;
                biggestDay = d.Date;
            }
        }

        foreach (var d in history)
        {
            var pnl = d.RealizedPnl;
            var ratio = (double)(Math.Abs(pnl) / biggestAbs);   // 0..1
            var h = ratio * halfMaxHeight;
            var isBig = biggestDay.HasValue && d.Date == biggestDay.Value && pnl > 0;
            var color = pnl >= 0
                ? (isBig ? "BrandBrush" : "StatusOkBrush")
                : "StatusErrBrush";

            ConsistencyBars.Add(new ConsistencyBarVm
            {
                Date            = d.Date,
                Pnl             = pnl,
                PositiveHeight  = pnl > 0 ? h : 0,
                NegativeHeight  = pnl < 0 ? h : 0,
                ColorKey        = color,
                IsBiggestDay    = isBig,
                TooltipText     = $"{d.Date:yyyy-MM-dd}: {pnl:+#,##0.00;-#,##0.00;0.00} {ccy} ({d.TradeCount} trade{(d.TradeCount == 1 ? "" : "s")})",
            });
        }

        // X-axis labels
        ConsistencyFirstDayLabel = history[0].Date.ToString("MMM d");
        ConsistencyLastDayLabel  = history[^1].Date.ToString("MMM d") + " (today)";
        ConsistencyMiddleLabel   = history.Count >= 3
            ? history[history.Count / 2].Date.ToString("MMM d")
            : "";

        // Status line — evaluate vs the firm's consistency rule if configured.
        var bestDayPct = stats.BestDayPercentOfTotal;          // 0 if no profit yet
        var bestDayProfit = stats.BestDayProfit;
        var totalNetRealized = stats.TotalGrossProfit;         // net realized P&L; named for FTMO-compat
        var limit = cfg?.ConsistencyMaxDayPercent;

        // Today's running day-PnL — realized today + floating. NOT in
        // BestDayProfit yet (today is excluded until reset crosses).
        // We surface this so the trader can see "today is shaping up to
        // be a big day, lock in some profit before close to avoid a
        // consistency hit." Includes floating P&L because today's "final
        // number" is end-of-day equity vs start-of-day equity (the
        // overnight floater counts as today's profit when reset crosses).
        var todayRealized = stats.TodayRealizedPnl;
        var todayFloating = (decimal)(SelectedAccount?.FloatingProfit ?? 0.0);
        var todayLive = todayRealized + todayFloating;

        if (totalNetRealized <= 0)
        {
            // No net profitable days yet — rule isn't relevant
            ConsistencyHeadline = "—";
            ConsistencyColorKey = "TextDimBrush";
            ConsistencyDescription = "You don't have net realized profit yet (closed gains minus closed losses). The consistency rule only kicks in once you do.";
            return;
        }

        if (limit is not int limPct || limPct <= 0)
        {
            // Rule not configured — show neutral summary
            ConsistencyHeadline = $"{bestDayPct:F0}% of profit on biggest day";
            ConsistencyColorKey = "TextMutedBrush";
            ConsistencyDescription =
                $"Your biggest completed day is {bestDayProfit:N2} {ccy} " +
                $"({bestDayPct:F0}% of your {totalNetRealized:N2} {ccy} net realized profit). " +
                $"No consistency rule is configured for this account. " +
                $"If your firm enforces one (e.g. FTMO 40%, FundedNext 50%), set it in the account settings.";
            return;
        }

        // Rule IS configured — evaluate
        var marginToBreach = (decimal)limPct - bestDayPct;

        // Today's projected share IF the day closes right now (informational).
        var todayProjectedPct = 0m;
        if (todayLive > 0 && totalNetRealized > 0)
        {
            // What would the ratio be if today became the new best day at its
            // current running PnL? Use the projected total = current total +
            // today's running PnL (since today's PnL isn't yet booked).
            var projectedTotal = totalNetRealized + todayLive;
            if (projectedTotal > 0)
                todayProjectedPct = (todayLive / projectedTotal) * 100m;
        }

        if (bestDayPct >= limPct)
        {
            ConsistencyHeadline = $"BREACH — best day {bestDayPct:F0}% > {limPct}%";
            ConsistencyColorKey = "StatusErrBrush";
            ConsistencyDescription =
                $"⚠ Your biggest completed day ({bestDayProfit:N2} {ccy} on {biggestDay:MMM d}) is {bestDayPct:F0}% " +
                $"of your {totalNetRealized:N2} {ccy} net realized profit, exceeding the firm's {limPct}% rule. " +
                $"To stay compliant you need to build up profits on other days so this day's share drops below {limPct}%.";
        }
        else if (marginToBreach <= 5m)
        {
            ConsistencyHeadline = $"Close — best day {bestDayPct:F0}% of {limPct}%";
            ConsistencyColorKey = "StatusWarnBrush";
            ConsistencyDescription =
                $"Your biggest completed day ({bestDayProfit:N2} {ccy} on {biggestDay:MMM d}) is {bestDayPct:F0}% " +
                $"of net realized profit — only {marginToBreach:F0}% below the firm's {limPct}% limit. " +
                $"Keep building profits on other days so this day's share drops.";
        }
        else
        {
            ConsistencyHeadline = $"Safe — best day {bestDayPct:F0}% of {limPct}%";
            ConsistencyColorKey = "StatusOkBrush";
            ConsistencyDescription =
                $"Your biggest completed day is {bestDayProfit:N2} {ccy} on {biggestDay:MMM d} " +
                $"({bestDayPct:F0}% of {totalNetRealized:N2} {ccy} net realized). The firm's consistency limit is {limPct}% — " +
                $"you have a {marginToBreach:F0}% margin.";
        }

        // Append today's-projection note ONLY if today is shaping up to
        // be a problem when it locks in at end-of-day reset.
        if (todayProjectedPct >= (decimal)limPct)
        {
            ConsistencyDescription += $" Heads-up: today's running P&L ({todayLive:N2} {ccy}) would be " +
                                       $"{todayProjectedPct:F0}% of total if the day closed now — over the {limPct}% limit. " +
                                       $"Consider locking in some profit before reset.";
        }
        else if (todayProjectedPct >= (decimal)limPct - 10m && todayProjectedPct > 0)
        {
            ConsistencyDescription += $" Today's running P&L ({todayLive:N2} {ccy}) projects to " +
                                       $"{todayProjectedPct:F0}% of total at close — still under the {limPct}% limit but getting close.";
        }
    }
}

/// <summary>
/// Single-bar view model for the Consistency rule chart. One instance per
/// trading day. Heights are pre-computed in DIPs so the XAML doesn't need
/// a converter for normalization.
/// </summary>
public sealed class ConsistencyBarVm
{
    public DateTime Date { get; init; }
    public decimal  Pnl { get; init; }
    public double   PositiveHeight { get; init; }   // 0 for losing days
    public double   NegativeHeight { get; init; }   // 0 for winning days
    public string   ColorKey { get; init; } = "StatusOkBrush";
    public bool     IsBiggestDay { get; init; }
    public string   TooltipText { get; init; } = "";
}
