using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LTC.Server.Models;

/// <summary>
/// A license sold to a customer. May or may not be activated on a machine yet.
/// Created by an admin call (or eventually by a Stripe webhook); consumed when
/// the customer's app calls /activate with the matching key.
/// </summary>
public class License
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The customer-visible key like "LTC-LIFE-XKQ7-9PRT-FB2C".</summary>
    [Required, MaxLength(64)]
    public string LicenseKey { get; set; } = "";

    [Required, MaxLength(254)]
    public string Email { get; set; } = "";

    /// <summary>"Lifetime" | "Daily" | "Partner" | "Dev".</summary>
    [Required, MaxLength(32)]
    public string Plan { get; set; } = "";

    /// <summary>Null = lifetime / no expiry.</summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool Revoked { get; set; }
    public DateTime? RevokedAt { get; set; }
    [MaxLength(256)]
    public string? RevokedReason { get; set; }

    /// <summary>Internal notes: "Stripe order #1234", "comp for X", etc.</summary>
    [MaxLength(512)]
    public string? Notes { get; set; }

    /// <summary>Navigation to the active binding (or null if not yet activated).</summary>
    public Activation? Activation { get; set; }
}

/// <summary>
/// Records the binding of a license to a specific machine. Created on the
/// first successful /activate call; updated by /heartbeat; cleared by
/// /deactivate.
///
/// At most one Activation per License — a license is bound to exactly
/// one machine at a time. To move to a new PC, the customer calls
/// /deactivate first.
/// </summary>
public class Activation
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LicenseId { get; set; }
    [ForeignKey(nameof(LicenseId))]
    public License License { get; set; } = null!;

    /// <summary>Concatenated 4-part fingerprint exactly as received from the
    /// client: "machineGuidHash-cpuIdHash-baseboardSerialHash-biosUuidHash"
    /// (all 32-char hex, joined by '-').</summary>
    [Required, MaxLength(140)]
    public string FingerprintFull { get; set; } = "";

    /// <summary>Short 8-char display ID for ops UI.</summary>
    [Required, MaxLength(16)]
    public string MachineShort { get; set; } = "";

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;

    /// <summary>IP address of the most recent activation/heartbeat.
    /// Stored for fraud detection; never shown to customers.</summary>
    [MaxLength(45)]    // IPv6 max
    public string? LastIp { get; set; }
}

/// <summary>
/// Append-only log of every activation/heartbeat/deactivate request that
/// hit the server. Used for fraud detection: "same key, 5 different
/// fingerprints in 1 hour" → likely sharing.
///
/// Trimmed periodically (anything older than 90 days deleted via a
/// background job — TBD next session) to keep the table manageable.
/// </summary>
public class RequestLog
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>License key the request claimed to be for. May be a
    /// nonexistent key (we log failed lookups too, for security).</summary>
    [MaxLength(64)]
    public string? LicenseKey { get; set; }

    /// <summary>Full fingerprint from the request, if provided.</summary>
    [MaxLength(140)]
    public string? FingerprintFull { get; set; }

    [Required, MaxLength(16)]
    public string Endpoint { get; set; } = "";    // "activate" / "heartbeat" / "deactivate"

    /// <summary>Result code: "ok", "key_not_found", "revoked", "expired",
    /// "fingerprint_mismatch", "already_active_elsewhere", etc.</summary>
    [Required, MaxLength(48)]
    public string Result { get; set; } = "";

    [MaxLength(45)]
    public string? Ip { get; set; }

    [MaxLength(256)]
    public string? UserAgent { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;
}
