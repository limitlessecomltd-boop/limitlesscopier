using System.Threading.Channels;
using LTC.Core.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTC.Core.Events;

/// <summary>
/// In-memory, lock-free event bus for master order events.
/// Producer side: <see cref="ConnectionManager"/> forwards every <see cref="MasterOrderEvent"/>
/// here. Consumer side: a single dispatcher task drains the channel and invokes registered handlers.
/// </summary>
/// <remarks>
/// We use an unbounded channel because dropping a master trade event is unacceptable —
/// the routing engine MUST process every one. With 50 accounts the channel will see
/// at most a few thousand events per second in the worst case, which is well below any
/// practical bound for this in-memory queue.
/// </remarks>
public sealed class EventBus : IAsyncDisposable
{
    private readonly Channel<MasterOrderEvent> _channel;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _dispatcher;
    private Func<MasterOrderEvent, ValueTask>? _handler;

    public EventBus(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _channel = Channel.CreateUnbounded<MasterOrderEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,    // we dispatch on one task — enables fast paths in the channel
            SingleWriter = false,   // many connections can publish concurrently
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>
    /// Set the handler that will receive every dispatched event. The handler is invoked
    /// on the dispatcher task and must NOT block; it should fire-and-forget any I/O.
    /// </summary>
    public void SetHandler(Func<MasterOrderEvent, ValueTask> handler)
    {
        if (_handler is not null)
            throw new InvalidOperationException("Handler already set.");
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _dispatcher = Task.Run(DispatchLoop);
    }

    /// <summary>
    /// Publish an event onto the bus. Returns immediately. Safe from any thread,
    /// including the DLL's event handler threads.
    /// </summary>
    public void Publish(MasterOrderEvent ev)
    {
        // TryWrite never blocks for an unbounded channel. The only failure mode is
        // a closed channel during shutdown, which we silently ignore.
        _channel.Writer.TryWrite(ev);
    }

    private async Task DispatchLoop()
    {
        var reader = _channel.Reader;
        var handler = _handler!;
        var ct = _cts.Token;

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var ev))
                {
                    if (ct.IsCancellationRequested) return;

                    try
                    {
                        await handler(ev).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // A handler exception must NEVER kill the dispatcher.
                        _logger.LogError(ex, "An error happened while routing a master trade event (ticket {Ticket}). The trade may not have been copied to all slaves.", ev.Ticket);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "The trade-event dispatcher stopped because of an unexpected error. Restart the app to recover.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        if (_dispatcher is not null)
        {
            try { await _dispatcher.ConfigureAwait(false); }
            catch { /* tolerate */ }
        }
        _cts.Dispose();
    }
}
