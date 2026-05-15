using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTC.Core;
using LTC.Core.Connections;
using LTC.Core.Logging;
using LTC.Core.Models;
using LTC.Core.Positions;
using LTC.Core.Routing;
using LTC.Persistence;
using Microsoft.Extensions.Logging;

// 'ActivityKind' is defined in both System.Diagnostics (distributed tracing) and
// LTC.Core.Models (our trade-event categories). Alias ours so call sites stay clean.
using ActivityKind = LTC.Core.Models.ActivityKind;
using ActivityStatus = LTC.Core.Models.ActivityStatus;

namespace LTC.App.ViewModels;

/// <summary>
/// Root view model: owns the engine + persistence, exposes observable collections
/// for accounts/links/activity, plus commands for top-bar buttons. All cross-thread
/// updates from the engine are marshalled to the UI dispatcher.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly CopierEngine _engine;
    private readonly PersistedConfig _persistence;
    private readonly Dispatcher _uiDispatcher;
    private readonly ILogger _logger;

    /// <summary>One recorder for the whole app — tracks the equity-at-reset
    /// anchor for every prop firm account. Updated on each stats snapshot.
    /// Read by AccountViewModel.RecomputePropRisk so the daily-loss meter
    /// works correctly. Constructed lazily once we have the persistence
    /// reference (in the ctor) so we can wire the save callback.</summary>
    public LTC.Core.Risk.DailyAnchorRecorder AnchorRecorder { get; private set; } = null!;

    // Quick lookup so we can update view models when the engine fires events.
    private readonly Dictionary<Guid, AccountViewModel> _accountsById = new();
    private readonly Dictionary<Guid, ActivityEntryViewModel> _activityById = new();

    /// <summary>
    /// Accounts where the AutoCloseAtPercent threshold has already fired
    /// this session. Used to prevent re-firing every 1.5s while positions
    /// are being closed and the risk meter dances around the threshold.
    /// An entry is removed once the account's worst meter drops back at
    /// least 5pp below threshold — letting the safety re-arm naturally
    /// when the user manually closes and reopens new trades.
    /// </summary>
    private readonly HashSet<Guid> _autoCloseFired = new();

    public ObservableCollection<AccountViewModel> Masters { get; } = new();
    public ObservableCollection<AccountViewModel> Slaves { get; } = new();
    public ObservableCollection<CopyLinkViewModel> Links { get; } = new();
    public ObservableCollection<ActivityEntryViewModel> Activity { get; } = new();
    public ObservableCollection<PlainLogEntryViewModel> Logs { get; } = new();
    /// <summary>All open positions across all connected accounts. Each row
    /// remembers its AccountId so close handlers know which broker to fire to.
    /// This is the canonical collection — <see cref="MasterPositions"/> and
    /// <see cref="SlavePositions"/> are filtered views of the same rows split
    /// by the owning account's role, rendered as two sub-sections in the UI.</summary>
    public ObservableCollection<PositionViewModel> Positions { get; } = new();

    /// <summary>Positions that live on accounts marked as Master. Kept in
    /// sync with <see cref="Positions"/> by <see cref="OnPositionsSnapshot"/>
    /// so the UI can show "Master positions" and "Slave positions" as two
    /// separate sub-sections under Open Positions.</summary>
    public ObservableCollection<PositionViewModel> MasterPositions { get; } = new();

    /// <summary>Positions that live on accounts marked as Slave.</summary>
    public ObservableCollection<PositionViewModel> SlavePositions { get; } = new();

    /// <summary>Right-pane toggle. Three-way: when both ShowLogs and ShowPositions
    /// are false, the right pane shows the Activity tape (default).</summary>
    [ObservableProperty] private bool showLogs;
    [ObservableProperty] private bool showPositions;

    /// <summary>True when the right pane should display the Activity tape.</summary>
    public bool RightPaneIsActivity => !ShowLogs && !ShowPositions;
    /// <summary>True when the right pane should display the Logs view.</summary>
    public bool RightPaneIsLogs => ShowLogs && !ShowPositions;
    /// <summary>True when the right pane should display the Positions view.</summary>
    public bool RightPaneIsPositions => ShowPositions;

    /// <summary>Notify the computed pane properties whenever the underlying toggles change.</summary>
    partial void OnShowLogsChanged(bool value)
    {
        OnPropertyChanged(nameof(RightPaneIsActivity));
        OnPropertyChanged(nameof(RightPaneIsLogs));
        OnPropertyChanged(nameof(RightPaneIsPositions));
    }
    partial void OnShowPositionsChanged(bool value)
    {
        OnPropertyChanged(nameof(RightPaneIsActivity));
        OnPropertyChanged(nameof(RightPaneIsLogs));
        OnPropertyChanged(nameof(RightPaneIsPositions));
    }

    // ---- Top-bar / status-bar / hero-strip fields ----
    [ObservableProperty] private string engineStatusText = "Engine running";
    [ObservableProperty] private string engineStatusColorKey = "StatusOkBrush";
    [ObservableProperty] private string latencyText = "—";
    [ObservableProperty] private string todayTradesText = "0";
    [ObservableProperty] private string successRateText = "—";
    [ObservableProperty] private string totalPnlText = "—";
    [ObservableProperty] private string totalPnlColorKey = "TextDimBrush";
    [ObservableProperty] private string cpuText = "—";
    [ObservableProperty] private string ramText = "—";
    [ObservableProperty] private string uptimeText = "00:00:00";
    [ObservableProperty] private AccountViewModel? selectedAccount;

    // Hero stats — aggregate across all accounts. Recomputed whenever any
    // account's stats change. Total equity is sum of all account equities;
    // floating P&L is sum of all account profits.
    [ObservableProperty] private string totalEquityText = "—";
    [ObservableProperty] private string totalEquityChangeText = "";
    [ObservableProperty] private string totalEquityChangeColorKey = "TextDimBrush";
    [ObservableProperty] private string floatingPnlText = "—";
    [ObservableProperty] private string floatingPnlColorKey = "TextDimBrush";
    [ObservableProperty] private string winRateText = "—";
    [ObservableProperty] private double winRatePercent;

    /// <summary>True when at least one activity row exists. Drives empty-state visibility.</summary>
    [ObservableProperty] private bool hasActivity;
    [ObservableProperty] private bool hasLinks;
    [ObservableProperty] private bool hasAnyAccount;

    public CopierEngine Engine => _engine;
    public PersistedConfig Persistence => _persistence;

    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly DispatcherTimer _statsTimer;
    private long _lastLatencyMicros;
    private int _todayTradeCount;
    private int _todaySuccessCount;
    /// <summary>IDs of activity entries we've already counted toward the
    /// daily totals. Prevents double-counting when an entry refreshes
    /// (InFlight → Success transitions arrive as a second event with the
    /// same Id).</summary>
    private readonly HashSet<Guid> _countedActivityIds = new();

    public MainViewModel(CopierEngine engine, PersistedConfig persistence, ILogger logger)
    {
        _engine = engine;
        _persistence = persistence;
        _logger = logger;
        _uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        // Construct the daily-anchor recorder with a persistence callback
        // so anchors written on rollover or first-touch survive app restarts.
        // Saves are wrapped in try/catch inside the recorder; nothing here
        // can crash the app on a disk-error path.
        AnchorRecorder = new LTC.Core.Risk.DailyAnchorRecorder(
            persistSave: anchor =>
            {
                try { _persistence.SaveAnchor(anchor); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not persist daily anchor for {Login}.", anchor.AccountLogin); }
            });

        // Rehydrate anchors from disk. If today's reset has already passed
        // since the saved anchor's trading date, the next equity-update tick
        // will detect rollover and snap a fresh anchor automatically — so
        // loading stale anchors here is always safe.
        try
        {
            var persisted = _persistence.LoadAllAnchors();
            AnchorRecorder.LoadAll(persisted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load persisted daily anchors. Recorder will retro-anchor at next tick.");
        }

        // Wire engine events to view models. All event sources fire on background
        // threads — we marshal to the UI dispatcher.
        _engine.Connections.StatusChanged += (_, change) =>
            UI(() => OnConnectionStatusChanged(change));
        _engine.Activity.EntryChanged += (_, entry) =>
            UI(() => OnActivityEntry(entry));
        _engine.PlainLog.EntryAdded += (_, entry) =>
            UI(() => OnPlainLogEntry(entry));

        // Backfill any plain-log entries that fired before the UI subscribed.
        foreach (var entry in _engine.PlainLog.Snapshot())
            Logs.Add(new PlainLogEntryViewModel(entry));

        // Live positions and account stats — both polled at 1.5s intervals
        // by the engine. We marshal to UI thread before mutating collections.
        _engine.Positions.PositionsChanged += (_, snap) =>
            UI(() => OnPositionsSnapshot(snap));
        _engine.Stats.StatsChanged += (_, snap) =>
            UI(() => OnAccountStatsSnapshot(snap));
        _engine.Routing.LinkCountersChanged += (_, snap) =>
            UI(() => OnLinkCounters(snap));

        // Periodic status bar refresh (process metrics, uptime).
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => RefreshStats();
        _statsTimer.Start();

        // Theme change hookup. We use the {DynamicResource XxxBrush} idiom in
        // XAML for direct property bindings — those re-resolve automatically
        // when the palette dict swaps. But several Foreground/Fill bindings go
        // through the KeyToBrush converter (P&L colors, status colors etc.)
        // because the choice of brush is data-driven (e.g. "StatusOkBrush" if
        // P&L positive, "StatusErrBrush" if negative). Those converter-bound
        // values DON'T auto-refresh because the source string ("StatusOkBrush")
        // doesn't change on theme swap — only the brush behind the name does.
        // We force them to re-resolve by raising PropertyChanged on every
        // ColorKey property when the theme changes.
        if (LTC.App.App.Theme is { } themeMgr)
        {
            themeMgr.ThemeChanged += (_, _) => UI(RefreshThemedColors);
        }
    }

    /// <summary>
    /// Force every brush-key binding to re-evaluate against the new theme.
    /// Called from the ThemeChanged handler. Bumps PropertyChanged for each
    /// *ColorKey property on this VM and on every collection-item VM.
    /// </summary>
    private void RefreshThemedColors()
    {
        // Top-level VM color keys.
        OnPropertyChanged(nameof(EngineStatusColorKey));
        OnPropertyChanged(nameof(TotalPnlColorKey));
        OnPropertyChanged(nameof(TotalEquityChangeColorKey));
        OnPropertyChanged(nameof(FloatingPnlColorKey));

        // Cascade to child VMs that hold their own ColorKey strings.
        foreach (var a in Masters)        a.NotifyColorKeysChanged();
        foreach (var a in Slaves)         a.NotifyColorKeysChanged();
        foreach (var p in Positions)      p.NotifyColorKeysChanged();
        foreach (var l in Links)          l.NotifyColorKeysChanged();
        foreach (var act in Activity)     act.NotifyColorKeysChanged();
        foreach (var log in Logs)         log.NotifyColorKeysChanged();
    }

    /// <summary>
    /// Load persisted state from the database and seed the engine.
    /// Called once at startup.
    /// </summary>
    public void LoadFromPersistence()
    {
        var snap = _persistence.LoadAll();

        foreach (var account in snap.Accounts)
            AddAccount(account, persist: false);

        foreach (var link in snap.Links)
            AddLink(link, persist: false);
    }

    // ----------------------------------------------------------------
    // Account add/remove
    // ----------------------------------------------------------------
    public AccountViewModel AddAccount(Account account, bool persist = true)
    {
        var vm = new AccountViewModel(account, AnchorRecorder);
        _accountsById[account.Id] = vm;

        if (vm.IsMaster) Masters.Add(vm);
        else             Slaves.Add(vm);
        HasAnyAccount = Masters.Count > 0 || Slaves.Count > 0;

        var conn = _engine.AddAccount(account);
        vm.AttachConnection(conn);

        if (persist)
            try { _persistence.SaveAccount(account); }
            catch (Exception ex) { _logger.LogError(ex, "Could not save the account to the database. Your changes might be lost on restart."); }

        return vm;
    }

    public void RemoveAccount(AccountViewModel vm)
    {
        // Remove dependent links from the UI list (engine + DB cascade handle theirs)
        var depLinks = Links.Where(l =>
            l.MasterAccountId == vm.Model.Id || l.SlaveAccountId == vm.Model.Id).ToList();
        foreach (var l in depLinks)
        {
            Links.Remove(l);
            _engine.Subscriptions.Remove(l.Model.Id);
        }
        HasLinks = Links.Count > 0;

        Masters.Remove(vm);
        Slaves.Remove(vm);
        HasAnyAccount = Masters.Count > 0 || Slaves.Count > 0;
        _accountsById.Remove(vm.Model.Id);

        // Drop any positions that lived on this account from all three lists.
        for (int i = Positions.Count - 1; i >= 0; i--)
        {
            if (Positions[i].AccountId == vm.Model.Id)
            {
                var p = Positions[i];
                Positions.RemoveAt(i);
                MasterPositions.Remove(p);
                SlavePositions.Remove(p);
            }
        }

        try { _persistence.DeleteAccount(vm.Model.Id); }
        catch (Exception ex) { _logger.LogError(ex, "Could not delete the account from the database."); }

        _ = _engine.Connections.RemoveAsync(vm.Model.Id);
    }

    /// <summary>
    /// Replace an existing account with an updated version. Disconnects the old
    /// engine connection, saves the new account to the DB, then re-adds it so
    /// the engine reconnects with the new credentials/server. Existing copy
    /// links pointing at this account keep working — we preserve the Id.
    /// </summary>
    /// <summary>
    /// Lightweight account save — persists in-memory mutations to disk
    /// WITHOUT tearing down the connection or replacing the VM. Use this
    /// for in-place setting toggles (e.g. flipping CloseAllOnTargetHit
    /// from the Prop Journal meter) where the credentials haven't changed.
    /// Returns silently on error (logs to the diagnostic log).
    /// </summary>
    public void UpdateAccountInPlace(AccountViewModel vm)
    {
        try { _persistence.SaveAccount(vm.Model); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save in-place account update. Change persists only for this session.");
        }
    }

    public void ReplaceAccount(AccountViewModel oldVm, Account updated)
    {
        // Snapshot the link Ids that referenced the old account; we need to
        // rebind them to the new view model since RemoveAccount() also tears
        // them down.
        var snapshotLinks = Links
            .Where(l => l.Model.MasterAccountId == oldVm.Model.Id
                     || l.Model.SlaveAccountId == oldVm.Model.Id)
            .Select(l => l.Model)
            .ToList();

        // Tear down the old. RemoveAccount also deletes from DB, but we'll
        // re-save with the same Id which writes a fresh row.
        RemoveAccount(oldVm);

        // Persist + add the updated account. AddAccount also re-creates the
        // engine connection so it auto-reconnects with the new credentials.
        AddAccount(updated, persist: true);

        // Restore the links that pointed at this account.
        foreach (var l in snapshotLinks)
        {
            try { AddLink(l, persist: true); }
            catch (Exception ex) { _logger.LogError(ex, "Could not restore copy link after editing account."); }
        }
    }

    // ----------------------------------------------------------------
    // Link add/remove
    // ----------------------------------------------------------------
    public CopyLinkViewModel AddLink(CopyLink link, bool persist = true)
    {
        var masterName = _accountsById.TryGetValue(link.MasterAccountId, out var m)
            ? m.DisplayName : "?";
        var slaveName = _accountsById.TryGetValue(link.SlaveAccountId, out var s)
            ? s.DisplayName : "?";

        var vm = new CopyLinkViewModel(link, masterName, slaveName);
        Links.Add(vm);
        HasLinks = true;
        _engine.AddLink(link);

        if (persist)
            try { _persistence.SaveLink(link); }
            catch (Exception ex) { _logger.LogError(ex, "Could not save the copy link to the database."); }

        return vm;
    }

    public void RemoveLink(CopyLinkViewModel vm)
    {
        Links.Remove(vm);
        HasLinks = Links.Count > 0;
        _engine.Subscriptions.Remove(vm.Model.Id);
        try { _persistence.DeleteLink(vm.Model.Id); }
        catch (Exception ex) { _logger.LogError(ex, "Could not delete the copy link from the database."); }
    }

    public void UpdateLink(CopyLinkViewModel vm)
    {
        // Persist + refresh the displayed strings.
        _engine.Subscriptions.Upsert(vm.Model);
        try { _persistence.SaveLink(vm.Model); }
        catch (Exception ex) { _logger.LogError(ex, "Could not update the copy link in the database."); }
        vm.Refresh();
    }

    // ----------------------------------------------------------------
    // Engine events -> view model updates (UI thread)
    // ----------------------------------------------------------------
    private void OnConnectionStatusChanged(ConnectionStatusChange change)
    {
        // The AccountViewModel already attached itself to the connection's
        // StatusChanged. Nothing extra to do here for now — but we keep the hook
        // to maintain status-bar latency and metrics.
    }

    private void OnActivityEntry(ActivityEntry entry)
    {
        // Track whether this is a brand-new entry vs a refresh of an existing
        // one so we know whether to insert/trim or just bump the metrics.
        bool isRefresh = _activityById.TryGetValue(entry.Id, out var existing);
        if (isRefresh)
        {
            existing!.Refresh();
            // Important: don't return here. We still need to update metrics
            // because activity entries fire as InFlight first and only later
            // refresh to Success/Failed. If we returned early on refresh, the
            // success counter would never increment and copy-latency / copy-
            // success would stay stuck at "—". Aggregate update below.
        }
        else
        {
            var vm = new ActivityEntryViewModel(entry);
            _activityById[entry.Id] = vm;
            Activity.Insert(0, vm);   // newest first
            HasActivity = Activity.Count > 0;

            // Trim to the most recent N — keeps memory bounded for long sessions.
            const int maxKeep = 200;
            while (Activity.Count > maxKeep)
            {
                var dropped = Activity[Activity.Count - 1];
                Activity.RemoveAt(Activity.Count - 1);
                _activityById.Remove(dropped.Model.Id);
            }
        }

        // Aggregate metrics: count this entry's CURRENT terminal status. Every
        // entry that reaches Success or Failed counts exactly once because we
        // gate on a status transition: the first time we see Success/Failed for
        // a given entry we increment, then mark it as already-counted.
        if ((entry.Kind == ActivityKind.Open || entry.Kind == ActivityKind.Close)
            && (entry.Status == ActivityStatus.Success || entry.Status == ActivityStatus.Failed)
            && _countedActivityIds.Add(entry.Id))
        {
            _todayTradeCount++;
            if (entry.Status == ActivityStatus.Success)
            {
                _todaySuccessCount++;
                _lastLatencyMicros = entry.InternalLatencyMicros;
                // Show per-trade latency in the status bar AND hero strip.
                // Format as ms with 1 decimal — sub-millisecond precision is
                // noise for the human reader.
                LatencyText = $"{entry.InternalLatencyMicros / 1000.0:F1}ms";
            }
        }
        TodayTradesText = $"{_todayTradeCount}";
        SuccessRateText = _todayTradeCount > 0
            ? $"{(100.0 * _todaySuccessCount / _todayTradeCount):F2}%"
            : "—";

        // Win rate for the hero strip. Today this is the same metric as
        // SuccessRateText (copy success); when we add real per-trade P&L
        // tracking it'll be redefined as winning vs losing closes.
        if (_todayTradeCount > 0)
        {
            WinRatePercent = 100.0 * _todaySuccessCount / _todayTradeCount;
            WinRateText = $"{WinRatePercent:F0}%";
        }
        else
        {
            WinRatePercent = 0;
            WinRateText = "—";
        }
    }

    private void OnPlainLogEntry(PlainLogEntry entry)
    {
        Logs.Add(new PlainLogEntryViewModel(entry));
        // Cap the UI collection at 2000 entries so it never grows unbounded —
        // matches the buffer's capacity in the engine.
        while (Logs.Count > 2000) Logs.RemoveAt(0);
    }

    public void ClearLogs()
    {
        _engine.PlainLog.Clear();
        Logs.Clear();
    }

    // ----------------------------------------------------------------
    // Live positions: dispatcher for poller snapshots
    // ----------------------------------------------------------------
    /// <summary>
    /// Reconcile our Positions collection against a fresh snapshot from one
    /// account. Strategy: update existing rows by ticket, add new ones, remove
    /// rows that are no longer in the broker's response.
    /// </summary>
    private void OnPositionsSnapshot(PositionsSnapshot snap)
    {
        // Index existing rows for this account by ticket so we can update in place.
        var existingForAccount = new Dictionary<ulong, PositionViewModel>();
        foreach (var p in Positions)
            if (p.AccountId == snap.AccountId)
                existingForAccount[p.Ticket] = p;

        // 1. Update or add.
        var seenTickets = new HashSet<ulong>();
        foreach (var pos in snap.Positions)
        {
            seenTickets.Add(pos.Ticket);
            if (existingForAccount.TryGetValue(pos.Ticket, out var vm))
            {
                // Existing row: update fields in place. Already in the right
                // sub-collection from when we first added it.
                vm.Apply(pos);
            }
            else
            {
                // New position: add to the canonical list and to the matching
                // master/slave sub-list. We look up the role from the account
                // VM collections rather than caching it on the position
                // because role can technically change (account edited).
                var newVm = new PositionViewModel(snap.AccountId, pos);
                Positions.Add(newVm);
                if (Masters.Any(a => a.Model.Id == snap.AccountId))
                    MasterPositions.Add(newVm);
                else if (Slaves.Any(a => a.Model.Id == snap.AccountId))
                    SlavePositions.Add(newVm);
                // If account not found in either collection (transient state
                // between AddAccount and AddAccount-completes), it lives only
                // in the canonical collection until the next snapshot picks it
                // up. Acceptable.
            }
        }

        // 2. Remove anything for this account that wasn't in the snapshot
        // (the position was closed). Iterate backwards to mutate safely.
        for (int i = Positions.Count - 1; i >= 0; i--)
        {
            var p = Positions[i];
            if (p.AccountId != snap.AccountId) continue;
            if (!seenTickets.Contains(p.Ticket))
            {
                Positions.RemoveAt(i);
                // Also drop from whichever role-specific list it was in.
                MasterPositions.Remove(p);
                SlavePositions.Remove(p);
                // If the per-trade safety watcher had auto-closed this
                // ticket, remove from the tracking set now that it's gone.
                // The set would otherwise grow indefinitely over a long
                // session (every ticket ever auto-closed). MT5 tickets are
                // unique forever so re-firing on the same ticket isn't a
                // concern, but unbounded memory growth is.
                _perTradeClosed.Remove(p.Ticket);
            }
        }

        // 3. Defensive consistency sweep: drop anything from the role-split
        // lists that's no longer in the canonical Positions collection. This
        // self-heals if any code path ever forgets to keep the splits in
        // sync (the optimistic-close path had this bug — closed rows kept
        // showing in MasterPositions because only Positions got the remove).
        // Cheap to run; the lists are small (open trades, not history).
        for (int i = MasterPositions.Count - 1; i >= 0; i--)
            if (!Positions.Contains(MasterPositions[i]))
                MasterPositions.RemoveAt(i);
        for (int i = SlavePositions.Count - 1; i >= 0; i--)
            if (!Positions.Contains(SlavePositions[i]))
                SlavePositions.RemoveAt(i);
    }

    /// <summary>
    /// Remove a single position view-model from every collection it might
    /// live in (Positions, MasterPositions, SlavePositions). Used by the
    /// optimistic close path so the UI reacts the instant the broker
    /// confirms the close, instead of waiting for the next 1.5s poll.
    /// Calling this for a position that's already gone from a list is safe
    /// — ObservableCollection.Remove is a no-op if the item isn't present.
    /// </summary>
    private void RemovePositionFromAllLists(PositionViewModel pos)
    {
        Positions.Remove(pos);
        MasterPositions.Remove(pos);
        SlavePositions.Remove(pos);
    }

    private void OnAccountStatsSnapshot(AccountStatsSnapshot snap)
    {
        // Find the matching account VM in either collection.
        AccountViewModel? acct =
            Masters.FirstOrDefault(a => a.Model.Id == snap.AccountId)
            ?? Slaves.FirstOrDefault(a => a.Model.Id == snap.AccountId);
        if (acct is null) return;

        // For prop accounts, update the daily anchor BEFORE ApplyStats so
        // the equity-changed handlers inside the VM see fresh anchor data.
        if (acct.Model.Kind != LTC.Core.Models.AccountKind.Personal
            && acct.Model.PropConfig is not null)
        {
            AnchorRecorder.Update(
                login:        acct.Model.Login,
                equity:       (decimal)snap.Stats.Equity,
                balance:      (decimal)snap.Stats.Balance,
                resetHourUtc: acct.Model.PropConfig.DailyResetUtc);
        }

        acct.ApplyStats(snap.Stats);

        // Prop-firm SAFETY WATCHER. After fresh stats land and RiskState is
        // recomputed, check whether the user's auto-close threshold has been
        // crossed. If so, fire CloseAllOnAccount once. See CheckPropSafety
        // for the full semantics (one-shot, re-arm on recovery, etc).
        CheckPropSafety(acct);

        // Hero strip aggregates — recompute on every poll so the UI shows
        // a unified portfolio view across all accounts.
        RecomputeHeroStats();
    }

    /// <summary>
    /// Prop-firm safety automation. Runs after every account stats snapshot
    /// (every 1.5s). If the account's AutoCloseAtPercent threshold is
    /// configured AND the DAILY or OVERALL meter has reached that percent,
    /// we close all open positions on this account to prevent breach.
    ///
    /// One-shot per breach: once we fire, we add the account to
    /// _autoCloseFired and don't fire again until either:
    ///   (a) the worst meter drops back at least 5pp below the threshold
    ///       (the user closed some trades manually or the day reset), at
    ///       which point we remove the entry and re-arm, or
    ///   (b) the app restarts.
    ///
    /// The 5pp hysteresis prevents flapping: equity moves a tiny amount
    /// after the close, the meter wobbles around the threshold for a few
    /// ticks, we don't want to re-fire just because we crossed the line
    /// from 70.1% to 69.9% and back.
    ///
    /// AutoPauseAtPercent is NOT implemented yet — it would require a
    /// routing-engine "paused accounts" set which we haven't built. The
    /// value persists in config but is logged with a warning when set,
    /// so the user knows it's a no-op until we wire it.
    /// </summary>
    private void CheckPropSafety(AccountViewModel acct)
    {
        var cfg = acct.Model.PropConfig;
        if (cfg is null) return;
        var risk = acct.RiskState;
        if (risk is null) return;

        // Worst meter percentage — whichever of daily / overall is highest.
        // Either crossing the threshold should trigger close-all.
        var worstPct = Math.Max(risk.DailyPercent, risk.OverallPercent);

        // Auto-close threshold check (daily or overall drawdown)
        if (cfg.AutoCloseAtPercent is int closeThreshold && closeThreshold > 0)
        {
            if (!_autoCloseFired.Contains(acct.Model.Id))
            {
                if (worstPct >= closeThreshold)
                {
                    _autoCloseFired.Add(acct.Model.Id);
                    var which = risk.DailyPercent >= risk.OverallPercent ? "daily" : "overall";
                    _logger.LogWarning(
                        "Account {Login}: AUTO-CLOSE TRIGGERED. {Which} loss meter at {Pct:F1}% reached threshold {Threshold}%. Closing all open positions on this account.",
                        acct.Model.Login, which, worstPct, closeThreshold);
                    _ = CloseAllOnAccountAsync(acct.Model.Id);
                }
            }
            else
            {
                // Already fired this session — re-arm only if worst meter
                // drops at least 5pp below threshold (hysteresis).
                if (worstPct <= closeThreshold - 5m)
                {
                    _autoCloseFired.Remove(acct.Model.Id);
                    _logger.LogInformation(
                        "Account {Login}: auto-close re-armed (worst meter dropped to {Pct:F1}%, below {ReArm}%).",
                        acct.Model.Login, worstPct, closeThreshold - 5);
                }
            }
        }

        // Per-trade max-loss check — close any SINGLE position whose
        // floating loss exceeds the user's per-trade limit. Unlike the
        // account-wide auto-close above, this fires per position. We
        // track which tickets we've already auto-closed so we don't
        // re-fire on the same one while the close is in flight.
        if (cfg.MaxLossPerTradePercent is decimal perTradePct
            && perTradePct > 0
            && cfg.StartingBalance > 0)
        {
            var maxLossAbs = cfg.StartingBalance * (perTradePct / 100m);
            // Snapshot positions for this account (avoid mutation during iteration)
            var positions = Positions.Where(p => p.AccountId == acct.Model.Id).ToList();
            foreach (var p in positions)
            {
                if (_perTradeClosed.Contains(p.Ticket)) continue;
                var loss = -(decimal)p.Profit;   // p.Profit is negative for losers; flip sign
                if (loss >= maxLossAbs)
                {
                    _perTradeClosed.Add(p.Ticket);
                    _logger.LogWarning(
                        "Account {Login}: AUTO-CLOSE single position #{Ticket} ({Symbol}) — loss {Loss:N2} reached per-trade limit {Limit:N2} ({Pct}% of starting balance).",
                        acct.Model.Login, p.Ticket, p.Symbol, loss, maxLossAbs, perTradePct);
                    _ = CloseSinglePositionAsync(p);
                }
            }
        }

        // Auto-pause threshold — NOT YET IMPLEMENTED.
        // We log a one-time warning per account per session so the user
        // knows their setting isn't doing anything yet.
        if (cfg.AutoPauseAtPercent is int && !_autoPauseWarningLogged.Contains(acct.Model.Id))
        {
            _autoPauseWarningLogged.Add(acct.Model.Id);
            _logger.LogWarning(
                "Account {Login}: AutoPauseAtPercent is configured but the pause-new-copies feature isn't wired to the routing engine yet. The setting is stored but has no effect. Use AutoCloseAtPercent for active safety.",
                acct.Model.Login);
        }
    }

    /// <summary>Tickets we've already auto-closed via the per-trade max-loss
    /// rule, so the watcher doesn't re-fire on the same trade while the
    /// close request is in flight. Cleaned up when a position naturally
    /// disappears from Positions (OnPositionsSnapshot calls
    /// _perTradeClosed.IntersectWith of remaining tickets).</summary>
    private readonly HashSet<ulong> _perTradeClosed = new();

    /// <summary>Tracks accounts where we've already logged the
    /// "auto-pause not implemented" warning, so we only spam the log
    /// once per account per session.</summary>
    private readonly HashSet<Guid> _autoPauseWarningLogged = new();

    /// <summary>
    /// Compute the four hero stats from current account VM data:
    /// Total equity (sum), Floating P&L (sum of profits), and a colour key
    /// derived from the sign. Win rate is derived from successful vs total
    /// activity entries seen so far. Latency is updated separately on each
    /// activity event in OnActivityEntry.
    /// </summary>
    private void RecomputeHeroStats()
    {
        var allAccounts = Masters.Concat(Slaves).Where(a => a.HasStats).ToList();

        if (allAccounts.Count == 0)
        {
            TotalEquityText             = "—";
            TotalEquityChangeText       = "";
            TotalEquityChangeColorKey   = "TextDimBrush";
            FloatingPnlText             = "—";
            FloatingPnlColorKey         = "TextDimBrush";
            return;
        }

        double equitySum  = allAccounts.Sum(a => a.Equity);
        double profitSum  = allAccounts.Sum(a => a.FloatingProfit);
        double balanceSum = allAccounts.Sum(a => a.Balance);

        // Use the first account's currency as a label. In practice all linked
        // accounts share a currency anyway.
        string currency = allAccounts[0].Currency;

        TotalEquityText = string.IsNullOrEmpty(currency)
            ? equitySum.ToString("N2")
            : $"{equitySum:N2} {currency}";

        // "Change today" = sum of floating profits, expressed both as currency
        // and as a percentage of total balance. Balance can be 0 if every
        // account is empty, in which case skip the percentage.
        if (balanceSum > 0)
        {
            double pct = (profitSum / balanceSum) * 100.0;
            TotalEquityChangeText = (profitSum >= 0 ? "+" : "") +
                profitSum.ToString("0.00") + $" ({pct:+0.00;-0.00;0.00}%)";
        }
        else
        {
            TotalEquityChangeText = (profitSum >= 0 ? "+" : "") + profitSum.ToString("0.00");
        }
        TotalEquityChangeColorKey =
            profitSum > 0 ? "StatusOkBrush"
          : profitSum < 0 ? "StatusErrBrush"
          :                 "TextDimBrush";

        FloatingPnlText = (profitSum >= 0 ? "+" : "") + profitSum.ToString("0.00");
        FloatingPnlColorKey =
            profitSum > 0 ? "StatusOkBrush"
          : profitSum < 0 ? "StatusErrBrush"
          :                 "TextDimBrush";
    }

    private void OnLinkCounters(LinkCountersSnapshot snap)
    {
        var link = Links.FirstOrDefault(l => l.Model.Id == snap.LinkId);
        link?.ApplyCounters(snap.Counters);
    }

    // ----------------------------------------------------------------
    // User-initiated trading actions (called by code-behind)
    // ----------------------------------------------------------------
    /// <summary>Close a single position by ticket. Bypasses the routing engine —
    /// this is a user action on a specific account, not a master-driven copy.</summary>
    public async Task<bool> CloseSinglePositionAsync(PositionViewModel pos)
    {
        var conn = _engine.Connections.Get(pos.AccountId);
        if (conn is null || conn.Status != ConnectionStatus.Connected)
        {
            _engine.PlainLog.Append(PlainLogLevel.Error,
                $"Cannot close: account is not connected.");
            return false;
        }

        var quote = conn.GetQuote(pos.Symbol);
        double price = pos.OrderType switch
        {
            CopyOrderType.Buy  => quote?.Bid ?? pos.CurrentPrice,
            CopyOrderType.Sell => quote?.Ask ?? pos.CurrentPrice,
            _ => 0
        };

        var req = new OrderCloseRequest(
            Ticket: pos.Ticket,
            Symbol: pos.Symbol,
            Volume: pos.Volume,
            OriginalOrderType: pos.OrderType,
            Price: price,
            MaxSlippagePoints: 100);

        try
        {
            var result = await conn.CloseOrderAsync(req).ConfigureAwait(true);
            if (result.Success)
            {
                _engine.PlainLog.Append(PlainLogLevel.Info,
                    $"Closed {pos.Symbol} on {conn.Account.DisplayName} (ticket #{pos.Ticket}).");
                // Optimistic remove — the next poller cycle would do it anyway.
                // CRITICAL: we must remove from ALL THREE collections (canonical
                // Positions plus the role-split Master/SlavePositions). Removing
                // only from Positions left orphan rows in the role-split lists
                // because the next poll's reconciliation walks Positions to find
                // stale rows — anything that's already gone from Positions is
                // invisible to the cleanup loop.
                RemovePositionFromAllLists(pos);
                return true;
            }
            _engine.PlainLog.Append(PlainLogLevel.Error,
                $"Could not close {pos.Symbol}: {result.ErrorMessage ?? "unknown"}.");
            return false;
        }
        catch (Exception ex)
        {
            _engine.PlainLog.Append(PlainLogLevel.Error,
                $"Could not close {pos.Symbol}: {ex.Message}.");
            return false;
        }
    }

    /// <summary>Close every position on a given account. Returns count of successful closes.</summary>
    public async Task<int> CloseAllOnAccountAsync(Guid accountId)
    {
        var toClose = Positions.Where(p => p.AccountId == accountId).ToList();
        int ok = 0;
        foreach (var p in toClose)
        {
            if (await CloseSinglePositionAsync(p).ConfigureAwait(true)) ok++;
        }
        return ok;
    }

    /// <summary>Close every position on every account (panic button).</summary>
    public async Task<int> CloseAllEverywhereAsync()
    {
        var snapshot = Positions.ToList();
        int ok = 0;
        foreach (var p in snapshot)
        {
            if (await CloseSinglePositionAsync(p).ConfigureAwait(true)) ok++;
        }
        return ok;
    }

    /// <summary>Force-reconnect the given account's broker connection.
    /// Useful when MT5 terminal moves to a different gateway IP and the
    /// existing socket is stale. The connection status indicator on the
    /// account row reflects the disconnected → connecting → connected
    /// transition. Fire-and-forget; failures are logged but don't show
    /// dialogs (the status pill turns red, which is informative enough).</summary>
    public async Task RefreshAccountConnectionAsync(AccountViewModel account)
    {
        var conn = _engine.Connections.Get(account.Model.Id);
        if (conn is null)
        {
            _logger.LogWarning("Refresh requested for {Login}: no connection registered.", account.Model.Login);
            return;
        }
        try
        {
            await conn.ReconnectAsync().ConfigureAwait(true);
            _logger.LogInformation("Account {Login}: refresh complete.", account.Model.Login);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Account {Login}: refresh failed.", account.Model.Login);
        }
    }

    private void RefreshStats()
    {
        var uptime = DateTime.UtcNow - _startedAt;
        UptimeText = $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";

        // Process CPU / RAM — light-touch, refreshes once per second.
        try
        {
            using var proc = Process.GetCurrentProcess();
            var ramMb = proc.WorkingSet64 / 1024 / 1024;
            RamText = $"{ramMb}MB";
            // CPU% on a single sample is hard; we leave a placeholder until we add
            // proper sampling. Show "—" rather than misleading numbers.
            CpuText = "—";
        }
        catch { /* tolerate */ }
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------
    private void UI(Action a)
    {
        if (_uiDispatcher.CheckAccess()) a();
        else _uiDispatcher.BeginInvoke(a);
    }

    public void Dispose()
    {
        _statsTimer.Stop();
    }
}
