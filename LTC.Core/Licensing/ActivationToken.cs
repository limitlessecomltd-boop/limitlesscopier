using System;

namespace LTC.Core.Licensing;

/// <summary>
/// The signed payload stored on disk after a license is activated for a
/// specific machine. Lives at %LOCALAPPDATA%\LimitlessTradeCopier\activation.dat
/// in DPAPI-encrypted form (so a copy off the disk to another machine is
/// useless even before the signature check fails).
///
/// This class lives in LTC.Core so both the customer-facing app
/// (LTC.App) and the admin minting tool (LTC.KeyGen) can read/write
/// the same data shape. The Windows-specific DPAPI persistence lives
/// in LTC.App; the cross-platform crypto + serialization lives here.
///
/// The wire format inside the file (after DPAPI decryption) is a small
/// length-prefixed binary blob followed by an Ed25519 signature. The
/// signature is computed over the canonical bytes of all fields below
/// using our private key (kept in the admin tool). The app verifies
/// using the embedded public key in the verifier class.
///
/// Layout (must match the writer in the admin tool):
///   uint8     format version (currently 1)
///   uint16    license key length
///   N bytes   license key (UTF-8, e.g. "LTC-XKQ7-9PRT-FB2C-AM4Z")
///   uint16    email length
///   N bytes   email (UTF-8)
///   uint16    plan length
///   N bytes   plan (UTF-8, e.g. "Lifetime")
///   int64     issuedUtcTicks
///   int64     expiresUtcTicks    (DateTime.MaxValue.Ticks for never)
///   int64     heartbeatDueTicks  (when the app should re-check with server)
///   uint16    32                 (fingerprint hash length, always 32 hex chars)
///   32 bytes  machineGuid hash hex
///   32 bytes  cpuId hash hex
///   32 bytes  baseboardSerial hash hex
///   32 bytes  biosUuid hash hex
///   64 bytes  Ed25519 signature
/// </summary>
public sealed class ActivationToken
{
    public const byte CurrentFormatVersion = 1;

    public string LicenseKey { get; init; } = "";
    public string Email { get; init; } = "";
    public string Plan { get; init; } = "";
    public DateTime IssuedUtc { get; init; }
    public DateTime ExpiresUtc { get; init; }
    public DateTime HeartbeatDueUtc { get; init; }
    public FingerprintBundle Fingerprint { get; init; } =
        new FingerprintBundle("", "", "", "");

    public bool IsExpired => DateTime.UtcNow > ExpiresUtc;
    public bool NeedsHeartbeat => DateTime.UtcNow > HeartbeatDueUtc;
    public bool IsHardLocked => DateTime.UtcNow > HeartbeatDueUtc.AddDays(7);
}

/// <summary>
/// The four-component fingerprint of a single machine. Stored inside
/// an activation token; recomputed on each app startup and compared
/// to the stored copy.
/// </summary>
public sealed record FingerprintBundle(
    string MachineGuid,
    string CpuId,
    string BaseboardSerial,
    string BiosUuid);
