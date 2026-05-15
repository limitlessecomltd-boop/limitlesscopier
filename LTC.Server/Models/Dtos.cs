using System;
using System.ComponentModel.DataAnnotations;

namespace LTC.Server.Models;

// =================================================================
// PUBLIC ENDPOINT DTOS — these shape the JSON the customer app sends
// and receives. Keep stable — every customer in the field knows these.
// =================================================================

/// <summary>POST /activate request body.</summary>
public class ActivateRequest
{
    /// <summary>Customer-facing license key, e.g. "LTC-LIFE-XKQ7-9PRT-FB2C".</summary>
    [Required, MaxLength(64)]
    public string LicenseKey { get; set; } = "";

    /// <summary>Concatenated 4-part fingerprint
    /// ("machineGuid-cpuId-baseboardSerial-biosUuid", each 32 hex chars).</summary>
    [Required, MaxLength(140)]
    public string Fingerprint { get; set; } = "";
}

/// <summary>POST /heartbeat request body — same shape as activate.</summary>
public class HeartbeatRequest
{
    [Required, MaxLength(64)]
    public string LicenseKey { get; set; } = "";

    [Required, MaxLength(140)]
    public string Fingerprint { get; set; } = "";
}

/// <summary>POST /deactivate request body — only needs the key.</summary>
public class DeactivateRequest
{
    [Required, MaxLength(64)]
    public string LicenseKey { get; set; } = "";

    /// <summary>Fingerprint is required to confirm the request comes
    /// from the same machine that holds the activation, not someone
    /// who just knows the key. If fingerprint doesn't match the bound
    /// machine, we refuse the deactivate.</summary>
    [Required, MaxLength(140)]
    public string Fingerprint { get; set; } = "";
}

/// <summary>Response body for all three endpoints. On success, includes
/// the signed activation token (base64). On failure, includes a code
/// and human-readable message the client app can show.</summary>
public class ActivationResponse
{
    public bool Ok { get; set; }

    /// <summary>Machine-readable error code, present only when Ok=false.
    /// Stable strings (clients may switch on these):
    ///   "key_not_found", "revoked", "expired",
    ///   "fingerprint_mismatch", "already_active_elsewhere",
    ///   "rate_limited", "server_error".</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Human-readable explanation. Safe to show to the user.</summary>
    public string? Message { get; set; }

    /// <summary>Base64-encoded signed activation token. Customer app
    /// decodes and stores as activation.dat. Present only on Ok=true
    /// for /activate and /heartbeat.</summary>
    public string? TokenBase64 { get; set; }
}
