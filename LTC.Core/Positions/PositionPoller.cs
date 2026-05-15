using LTC.Core.Connections;
using LTC.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTC.Core.Positions;

/// <summary>
/// Polls every connected broker for open positions on a fixed cadence and
/// raises <see cref="PositionsChanged"/> events keyed by account.
///
/// Why polling instead of pure event-driven? The DLL fires order events on
/// open/close/modify, but P&L drifts continuously with quotes. Polling at
/// 1.5s gives a "live" feel without flooding the UI thread or hammering the
/// broker.
///
/// Lifecycle: created by <see cref="CopierEngine"/>, started when the engine
/// is constructed, stopped on dispose.
/// </summary>
public sealed class PositionPoller : IAsyncDisposable
{
    private readonly ConnectionManager _connections;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _interval;
    private Task? _loop;

    public event EventHandler<PositionsSnapshot>? PositionsChanged;

    public PositionPoller(ConnectionManager connections, ILogger? logger = null,
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
        // Tiny startup delay so the first poll doesn't race with connection setup.
        try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Snapshot the connections collection — Get* operations on the
                // manager itself are cheap and we don't want to hold its lock
                // across an async fetch.
                var conns = _connections.GetAll();

                // Fan out concurrently. We don't await all together so a slow
                // broker doesn't stall the others.
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
                _logger.LogDebug(ex, "Position poller iteration failed; will retry next cycle.");
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollOneAsync(IBrokerConnection conn, CancellationToken ct)
    {
        try
        {
            var positions = await conn.GetOpenPositionsAsync(ct).ConfigureAwait(false);
            // Always raise — even with an empty list — so the UI can clear stale rows.
            PositionsChanged?.Invoke(this, new PositionsSnapshot(conn.AccountId, positions));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not poll positions for account {Login}.", conn.Account.Login);
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

/// <summary>One poll's worth of positions for a single account.</summary>
public sealed record PositionsSnapshot(Guid AccountId, IReadOnlyList<OpenPosition> Positions);
