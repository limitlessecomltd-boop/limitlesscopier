using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LTC.App.Licensing;

namespace LTC.App;

/// <summary>
/// Heartbeat scheduler. After the license gate clears at startup, the main
/// startup path should call <see cref="App.StartHeartbeatTimer"/> exactly
/// once. From then on we re-validate with the activation server every
/// <see cref="HeartbeatInterval"/> hours.
///
/// On a successful heartbeat we silently refresh the on-disk token (the
/// new token has an extended <c>HeartbeatDueUtc</c>). On network failure
/// we do nothing — the existing token continues to be honored until the
/// 48h+4h offline-grace window expires, at which point
/// <see cref="ActivationService.GetStatus"/> returns
/// <see cref="ActivationState.HeartbeatRequired"/> and the main window
/// or trading paths can react.
///
/// On server rejection (revoked / expired / etc) we currently just log
/// it. A future enhancement could pop a banner inside the main window;
/// we don't want to kill the app mid-session, so the customer learns at
/// their next restart (where the new revoked-state token blocks startup).
/// </summary>
public partial class App
{
    /// <summary>How often to re-contact the server. Six hours strikes a
    /// balance: frequent enough that a revoked license is invalidated
    /// within a working day, but rare enough that we don't hammer the
    /// server with thousands of heartbeats per customer per month.
    /// With a 48h server-side window, 6h gives the customer 8 chances
    /// to refresh before they'd hit the offline lock.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromHours(6);

    private static Timer? _heartbeatTimer;

    /// <summary>Idempotent — calling more than once does nothing. Safe to
    /// invoke from anywhere after the license gate passes.</summary>
    public static void StartHeartbeatTimer()
    {
        if (_heartbeatTimer is not null) return;

        // First heartbeat 5 minutes after startup (giving the network
        // stack time to settle on flaky Wi-Fi), then every HeartbeatInterval.
        _heartbeatTimer = new Timer(
            HeartbeatCallback, state: null,
            dueTime: TimeSpan.FromMinutes(5),
            period: HeartbeatInterval);
    }

    /// <summary>Stop the heartbeat timer. Used during shutdown so the
    /// process doesn't hang waiting for the periodic callback to finish.</summary>
    public static void StopHeartbeatTimer()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    /// <summary>Timer callback. Runs on a thread-pool thread; UI updates
    /// (if we ever add them) must marshal back via the Dispatcher.</summary>
    private static async void HeartbeatCallback(object? state)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await Activation.HeartbeatAsync(cts.Token);

            // We're deliberately silent here. The new token (if any) is
            // already persisted by ActivationService.HeartbeatAsync.
            //
            //   Success         → fresh token on disk, life is good
            //   NetworkFailure  → existing token still applies; if it's
            //                     near expiry, GetStatus() will notice
            //                     on next read
            //   ServerRejected  → customer's session continues for now;
            //                     next app start will block them
            //   LocalError      → no activation on disk (shouldn't happen
            //                     since we got past the gate)
            //
            // Future enhancement: surface ServerRejected via a non-blocking
            // banner in the main window so the customer knows immediately.
            //
            // For now we log to debug output so devs can see what happened.
            System.Diagnostics.Debug.WriteLine(
                $"[Heartbeat] {result.Kind}: {result.Message}");
        }
        catch (Exception ex)
        {
            // Swallow — heartbeat must never crash the app
            System.Diagnostics.Debug.WriteLine($"[Heartbeat] Exception: {ex.Message}");
        }
    }
}
