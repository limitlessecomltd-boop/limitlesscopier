using LTC.Core.Connections;
using LTC.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTC.Core.Positions;

/// <summary>
/// Polls connected brokers for account-level stats (balance, equity, margin)
/// on a fixed cadence and raises events. Mirror of <see cref="PositionPoller"/>
/// but for account financials. Same 1.5s default cadence — these values
/// drift continuously with floating P&L.
/// </summary>
public sealed class AccountStatsPoller : IAsyncDisposable
{
    private readonly ConnectionManager _connections;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _interval;
    private Task? _loop;

    public event EventHandler<AccountStatsSnapshot>? StatsChanged;

    public AccountStatsPoller(ConnectionManager connections, ILogger? logger = null,
        TimeSpan? interval = null)
    {
        _connections = connections;
        _logger = logger ?? NullLogger.Instance;
        _interval = interval ?? TimeSpan.FromMilliseconds(1500);
    }

    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var conns = _connections.GetAll();
                var tasks = new List<Task>(conns.Count);
                foreach (var c in conns)
                {
                    if (c.Status != ConnectionStatus.Connected) continue;
                    tasks.Add(PollOneAsync(c, ct));
                }
                if (tasks.Count > 0)
                    await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Account-stats poller iteration failed; will retry next cycle.");
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollOneAsync(IBrokerConnection conn, CancellationToken ct)
    {
        try
        {
            var stats = await conn.GetAccountStatsAsync(ct).ConfigureAwait(false);
            if (stats is null) return;
            StatsChanged?.Invoke(this, new AccountStatsSnapshot(conn.AccountId, stats));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not poll account stats for account {Login}.", conn.Account.Login);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); }
        catch { /* swallow on shutdown */ }
        _cts.Dispose();
    }
}

public sealed record AccountStatsSnapshot(Guid AccountId, AccountStats Stats);
