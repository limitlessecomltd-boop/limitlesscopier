using System.Collections.Concurrent;
using LTC.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTC.Core.Connections;

/// <summary>
/// Owns the pool of <see cref="IBrokerConnection"/> instances, one per registered account.
/// Handles add/remove of accounts, drives initial connect, watches status, and
/// performs automatic reconnect with exponential backoff when connections drop.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    /// <summary>Factory used to create connections. Default is <see cref="Mt5BrokerConnection"/>; tests can supply mocks.</summary>
    public delegate IBrokerConnection ConnectionFactory(Account account, ILogger? logger);

    private readonly ConnectionFactory _factory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, ManagedConnection> _connections = new();
    private readonly CancellationTokenSource _shutdownCts = new();

    /// <summary>Backoff schedule: 1s, 2s, 4s, 8s, 16s, then 30s thereafter (capped).</summary>
    private static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
    };
    private static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(30);

    public ConnectionManager(ConnectionFactory? factory = null, ILogger? logger = null)
    {
        _factory = factory ?? ((acc, log) => new Mt5BrokerConnection(acc, log));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised when any managed connection's status changes. Sender = account id.</summary>
    public event EventHandler<ConnectionStatusChange>? StatusChanged;

    /// <summary>Raised when any master account emits an order update. UI / routing engine subscribe here.</summary>
    public event EventHandler<MasterOrderEvent>? OrderUpdate;

    /// <summary>Snapshot of all managed connections.</summary>
    public IReadOnlyCollection<IBrokerConnection> Connections =>
        _connections.Values.Select(m => m.Connection).ToArray();

    /// <summary>Same as <see cref="Connections"/> but a list — convenient for pollers
    /// that fan out tasks per connection.</summary>
    public IReadOnlyList<IBrokerConnection> GetAll() =>
        _connections.Values.Select(m => m.Connection).ToList();

    /// <summary>Look up a connection by account id, if managed.</summary>
    public IBrokerConnection? Get(Guid accountId)
        => _connections.TryGetValue(accountId, out var m) ? m.Connection : null;

    // -------------------------------------------------------------------
    // Add / Remove
    // -------------------------------------------------------------------

    /// <summary>
    /// Register an account with the manager and begin connecting in the background.
    /// Returns immediately; observe <see cref="StatusChanged"/> for connection progress.
    /// </summary>
    public IBrokerConnection Add(Account account)
    {
        if (_connections.ContainsKey(account.Id))
            throw new InvalidOperationException(
                $"Account {account.Id} ({account.Login}) is already registered.");

        var conn = _factory(account, _logger);
        var managed = new ManagedConnection(conn);
        _connections[account.Id] = managed;

        conn.StatusChanged += (s, status) => OnConnectionStatusChanged(account.Id, status, managed);
        conn.OrderUpdate += (s, ev) => OrderUpdate?.Invoke(this, ev);

        // Kick off initial connect
        if (account.Enabled)
            _ = ConnectInBackground(managed);

        return conn;
    }

    /// <summary>
    /// Unregister an account and dispose its connection. Idempotent.
    /// </summary>
    public async Task RemoveAsync(Guid accountId)
    {
        if (!_connections.TryRemove(accountId, out var managed)) return;
        managed.Cancel();
        try
        {
            await managed.Connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "While closing the connection for an account it threw an error. Safe to ignore unless it repeats.");
        }
    }

    // -------------------------------------------------------------------
    // Reconnect machinery
    // -------------------------------------------------------------------
    private void OnConnectionStatusChanged(Guid accountId, ConnectionStatus status, ManagedConnection managed)
    {
        StatusChanged?.Invoke(this, new ConnectionStatusChange(accountId, status));

        // If the connection drops or fails AFTER having been connected, auto-reconnect.
        // Initial Connecting -> Failed transitions also fall through to reconnect logic
        // because a transient broker hiccup at startup shouldn't permanently break us.
        if (status is ConnectionStatus.Failed or ConnectionStatus.Disconnected)
        {
            if (_shutdownCts.IsCancellationRequested) return;
            if (!managed.Connection.Account.Enabled) return;
            if (managed.IsCancelled) return;

            _ = ReconnectLoop(managed);
        }
        else if (status == ConnectionStatus.Connected)
        {
            // Reset the backoff counter on a successful connect.
            managed.ResetBackoff();
        }
    }

    private async Task ConnectInBackground(ManagedConnection managed)
    {
        try
        {
            await managed.Connection.ConnectAsync(managed.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not connect account {Login} on the first try; the auto-reconnect loop will keep trying in the background.",
                managed.Connection.Account.Login);
        }
    }

    private async Task ReconnectLoop(ManagedConnection managed)
    {
        // Only one reconnect loop per managed connection. Lock-free coordination via a flag.
        if (!managed.TryEnterReconnectLoop()) return;

        try
        {
            while (!managed.IsCancelled && !_shutdownCts.IsCancellationRequested)
            {
                var attempt = managed.NextAttempt();
                var delay = attempt < BackoffSchedule.Length ? BackoffSchedule[attempt] : BackoffMax;

                managed.SetReconnectingStatus();

                try
                {
                    await Task.Delay(delay, managed.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }

                if (managed.IsCancelled || _shutdownCts.IsCancellationRequested) return;

                try
                {
                    _logger.LogInformation("Trying to reconnect account {Login} (attempt #{Attempt}).",
                        managed.Connection.Account.Login, attempt + 1);
                    await managed.Connection.ConnectAsync(managed.Token).ConfigureAwait(false);

                    if (managed.Connection.Status == ConnectionStatus.Connected)
                        return; // Success — the StatusChanged handler will reset state.
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Reconnect attempt #{Attempt} for account {Login} did not succeed; will keep trying with longer waits between attempts.",
                        attempt + 1, managed.Connection.Account.Login);
                    // Loop continues — backoff increases on the next iteration.
                }
            }
        }
        finally
        {
            managed.ExitReconnectLoop();
        }
    }

    // -------------------------------------------------------------------
    // Disposal
    // -------------------------------------------------------------------
    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();

        var disposeTasks = _connections.Values
            .Select(m => DisposeOneAsync(m))
            .ToArray();
        _connections.Clear();

        try
        {
            await Task.WhenAll(disposeTasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Some accounts did not shut down cleanly. Usually safe to ignore.");
        }

        _shutdownCts.Dispose();
    }

    private static async Task DisposeOneAsync(ManagedConnection m)
    {
        m.Cancel();
        try { await m.Connection.DisposeAsync().ConfigureAwait(false); } catch { /* tolerate */ }
    }

    // -------------------------------------------------------------------
    // Internal state per connection
    // -------------------------------------------------------------------
    private sealed class ManagedConnection
    {
        public IBrokerConnection Connection { get; }

        private readonly CancellationTokenSource _cts = new();
        public CancellationToken Token => _cts.Token;
        public bool IsCancelled => _cts.IsCancellationRequested;

        private int _attempt = 0;
        private int _inLoop = 0;

        public ManagedConnection(IBrokerConnection conn) { Connection = conn; }

        public int NextAttempt() => Interlocked.Increment(ref _attempt) - 1;
        public void ResetBackoff() => Interlocked.Exchange(ref _attempt, 0);

        public bool TryEnterReconnectLoop() => Interlocked.CompareExchange(ref _inLoop, 1, 0) == 0;
        public void ExitReconnectLoop() => Interlocked.Exchange(ref _inLoop, 0);

        public void SetReconnectingStatus()
        {
            // Best-effort: reflect the in-progress attempt to subscribers.
            // The actual ConnectAsync call will transition through Connecting->Connected/Failed.
        }

        public void Cancel() { try { _cts.Cancel(); } catch { } }
    }
}

/// <summary>Snapshot of a status change for an account.</summary>
public sealed record ConnectionStatusChange(Guid AccountId, ConnectionStatus Status);
