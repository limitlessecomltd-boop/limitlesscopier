using LTC.Core.Connections;
using LTC.Core.Events;
using LTC.Core.Logging;
using LTC.Core.Models;
using LTC.Core.Positions;
using LTC.Core.Routing;
using LTC.Core.Symbols;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTC.Core;

/// <summary>
/// Top-level orchestrator. Wires together the ConnectionManager, EventBus, RoutingEngine,
/// ActivityLog, and SymbolMapper into one object the UI can drive end-to-end.
/// </summary>
public sealed class CopierEngine : IAsyncDisposable
{
    public ConnectionManager Connections { get; }
    public EventBus Bus { get; }
    public CopySubscriptionRegistry Subscriptions { get; }
    public ActivityLog Activity { get; }
    public RoutingEngine Routing { get; }
    public ISymbolMapper SymbolMapper { get; }
    public PlainLogBuffer PlainLog { get; }
    public PositionPoller Positions { get; }
    public AccountStatsPoller Stats { get; }

    private readonly ILogger _logger;

    public CopierEngine(
        ILogger? logger = null,
        ISymbolMapper? symbolMapper = null,
        ConnectionManager.ConnectionFactory? connectionFactory = null)
    {
        _logger = logger ?? NullLogger.Instance;
        Activity = new ActivityLog(capacity: 10_000);
        SymbolMapper = symbolMapper ?? new SuffixPrefixSymbolMapper();
        Subscriptions = new CopySubscriptionRegistry();
        PlainLog = new PlainLogBuffer(capacity: 2000);
        PlainLog.Append(PlainLogLevel.Info, "Copier engine started.");

        Connections = new ConnectionManager(connectionFactory, _logger);
        Bus = new EventBus(_logger);
        Routing = new RoutingEngine(Connections, Subscriptions, SymbolMapper, Activity, _logger);

        // Background pollers: positions and account stats both poll every 1.5s.
        // Started immediately so they're warm by the time the first connection
        // comes up; they no-op on Disconnected accounts.
        Positions = new PositionPoller(Connections, _logger);
        Stats = new AccountStatsPoller(Connections, _logger);
        Positions.Start();
        Stats.Start();

        // Feed account stats into the routing engine so risk-based lot modes
        // (RiskPercent, EquityRatio, BalanceRatio) have real balance/equity to
        // work with at open time.
        Stats.StatsChanged += (_, snap) =>
            Routing.UpdateAccountSnapshot(snap.AccountId, snap.Stats.Balance, snap.Stats.Equity);

        // Wire: every order update from any connection -> bus -> routing engine.
        Connections.OrderUpdate += (_, ev) => Bus.Publish(ev);
        Bus.SetHandler(Routing.HandleMasterEventAsync);

        // Connection status changes -> activity tape entry + plain-English log.
        Connections.StatusChanged += (_, change) =>
        {
            var conn = Connections.Get(change.AccountId);
            var who = conn?.Account.DisplayName ?? "Account";

            // Plain-English entry. Skip very-frequent transitions like Connecting
            // (they're noise) but always log Connected, Reconnecting, Failed,
            // and Disconnected because those are user-actionable.
            switch (change.Status)
            {
                case ConnectionStatus.Connected:
                    PlainLog.Append(PlainLogLevel.Info,
                        $"{who} connected to broker.");
                    break;
                case ConnectionStatus.Reconnecting:
                    PlainLog.Append(PlainLogLevel.Warning,
                        $"{who} lost the connection. Trying to reconnect...");
                    break;
                case ConnectionStatus.Failed:
                    PlainLog.Append(PlainLogLevel.Error,
                        $"{who} failed to connect. Reason: {conn?.LastError ?? "unknown"}");
                    break;
                case ConnectionStatus.Disconnected:
                    PlainLog.Append(PlainLogLevel.Info,
                        $"{who} disconnected.");
                    break;
            }

            Activity.Append(new ActivityEntry
            {
                Kind = change.Status switch
                {
                    ConnectionStatus.Connected => ActivityKind.Connect,
                    ConnectionStatus.Disconnected or ConnectionStatus.Failed
                        => ActivityKind.Disconnect,
                    _ => ActivityKind.Connect
                },
                Status = change.Status == ConnectionStatus.Connected
                    ? ActivityStatus.Success
                    : (change.Status == ConnectionStatus.Failed
                        ? ActivityStatus.Failed
                        : ActivityStatus.InFlight),
                MasterAccountId = change.AccountId,
                MasterAccountLabel = conn?.Account.DisplayName,
                ErrorMessage = change.Status switch
                {
                    ConnectionStatus.Connecting    => "connecting",
                    ConnectionStatus.Reconnecting  => "reconnecting",
                    ConnectionStatus.Failed        => conn?.LastError ?? "connection failed",
                    ConnectionStatus.Disconnected  => "disconnected",
                    _ => null
                }
            });
        };

        // Hook into the activity log too — every successful trade copy or
        // skipped/failed copy should generate a plain-English line for the
        // user-facing Logs view. We deliberately log only on Success, Failed,
        // and Skipped (Open/Close kinds) to avoid spamming the view with
        // InFlight noise on every order.
        Activity.EntryChanged += (_, entry) =>
        {
            if (entry.Status == ActivityStatus.InFlight) return;
            if (entry.Kind != ActivityKind.Open && entry.Kind != ActivityKind.Close
                && entry.Kind != ActivityKind.Modify && entry.Kind != ActivityKind.Filtered)
                return;

            var sym = entry.Symbol ?? "?";
            var slave = entry.SlaveAccountLabel ?? "slave";
            var master = entry.MasterAccountLabel ?? "master";

            switch (entry.Status)
            {
                case ActivityStatus.Success:
                    var verb = entry.Kind switch
                    {
                        ActivityKind.Open => "opened",
                        ActivityKind.Close => "closed",
                        ActivityKind.Modify => "updated stops on",
                        _ => "copied"
                    };
                    PlainLog.Append(PlainLogLevel.Info,
                        $"Copy from {master} to {slave}: {verb} {sym}.");
                    break;
                case ActivityStatus.Failed:
                    PlainLog.Append(PlainLogLevel.Error,
                        $"Copy from {master} to {slave} for {sym} failed. Reason: {entry.ErrorMessage ?? "unknown"}.");
                    break;
                case ActivityStatus.Skipped:
                    PlainLog.Append(PlainLogLevel.Warning,
                        $"Copy from {master} to {slave} for {sym} was skipped. Reason: {entry.ErrorMessage ?? "unknown"}.");
                    break;
            }
        };
    }

    /// <summary>Add an account to the engine and start its connection.</summary>
    public IBrokerConnection AddAccount(Account account) => Connections.Add(account);

    /// <summary>Add a CopyLink to the engine.</summary>
    public void AddLink(CopyLink link) => Subscriptions.Upsert(link);

    /// <summary>Replace the entire link set (e.g. after loading from persistence).</summary>
    public void SetLinks(IEnumerable<CopyLink> links) => Subscriptions.ReplaceAll(links);

    public async ValueTask DisposeAsync()
    {
        // Stop pollers first so they don't try to poll a disposing connection.
        await Positions.DisposeAsync().ConfigureAwait(false);
        await Stats.DisposeAsync().ConfigureAwait(false);
        await Bus.DisposeAsync().ConfigureAwait(false);
        await Connections.DisposeAsync().ConfigureAwait(false);
    }
}
