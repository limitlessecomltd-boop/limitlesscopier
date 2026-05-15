namespace LTC.Core.Models;

/// <summary>
/// A broker account registered with the copier. Credentials are stored encrypted in persistence;
/// this in-memory model carries the plaintext password only after decryption for use by MT5API.
/// </summary>
public sealed class Account
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Friendly name shown in the UI (e.g. "Master - FTMO Challenge").</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>MT5 account login number.</summary>
    public ulong Login { get; set; }

    /// <summary>Plaintext password. Only populated after decryption from the persistence layer.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Broker server hostname or IP address (e.g. "access.metatrader5.com").</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>Broker server port (typically 443).</summary>
    public int Port { get; set; } = 443;

    /// <summary>Whether this account acts as a master, slave, or both.</summary>
    public AccountRole Role { get; set; } = AccountRole.Slave;

    /// <summary>If false, this account is held in the registry but never connects.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The most recent successful connection time (UTC), or null if never connected.</summary>
    public DateTime? LastConnectedAt { get; set; }

    /// <summary>Optional broker-name label used in the UI grouping (e.g. "IC Markets").</summary>
    public string? BrokerLabel { get; set; }

    /// <summary>
    /// Optional symbol-naming prefix this broker uses. When this account is a slave,
    /// master symbols get this prefix prepended before being sent to the broker.
    /// E.g. broker uses "m." for micro accounts → set Prefix = "m.".
    /// </summary>
    public string SymbolPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Optional symbol-naming suffix this broker uses. When this account is a slave,
    /// master symbols get this suffix appended before being sent to the broker.
    /// E.g. broker uses ECN naming → set Suffix = "ecn" so XAUUSD becomes XAUUSDecn.
    /// </summary>
    public string SymbolSuffix { get; set; } = string.Empty;

    /// <summary>
    /// Whether this account is a retail/personal account or a prop firm
    /// challenge/funded account. Drives the onboarding flow and the
    /// presence of risk meters in the UI. Defaults to Personal so existing
    /// rows from older schema versions read as plain retail.
    /// </summary>
    public AccountKind Kind { get; set; } = AccountKind.Personal;

    /// <summary>
    /// Prop firm rule set. Null when <see cref="Kind"/> is Personal.
    /// Populated when Kind is PropChallenge or PropFunded, either from a
    /// preset (FTMO etc.) or from the trader's manual entry. Drives the
    /// daily/overall drawdown calculations and (when opt-in flags are set)
    /// the auto-pause / auto-close safety automations.
    /// </summary>
    public PropFirmConfig? PropConfig { get; set; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
