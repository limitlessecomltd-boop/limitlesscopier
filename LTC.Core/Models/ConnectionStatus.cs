namespace LTC.Core.Models;

/// <summary>
/// Lifecycle state of an MT5 broker connection.
/// </summary>
public enum ConnectionStatus
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Failed = 4,
    Disabled = 5
}
