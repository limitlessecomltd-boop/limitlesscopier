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
/// using our private key (kept on the server). The app verifies using
/// the embedded public key in the verifier class.
///
/// Layout (must match the writer on the server):
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

    /// <summary>
    /// How much wall-clock slack we allow between <see cref="HeartbeatDueUtc"/>
    /// passing and the client refusing to start (<see cref="IsHardLocked"/>).
    /// 4 hours covers brief outages and accommodates customers whose
    /// internet just dropped right at the boundary — without giving a
    /// cracked binary an extra week to operate offline.
    ///
    /// Together with the server-set heartbeat window (currently 48h, see
    /// <c>CustomerEndpoints.BuildToken</c>), this means a healthy app will
    /// re-verify with the server every ~2 days, and a fully offline app
    /// stops working at 48h + 4h = 52h since its last server contact.
    /// </summary>
    public static readonly TimeSpan HardLockSlack = TimeSpan.FromHours(4);

    public bool IsExpired => DateTime.UtcNow > ExpiresUtc;

    /// <summary>True once the client is past <see cref="HeartbeatDueUtc"/>.
    /// The app should attempt a heartbeat as soon as possible; if it
    /// succeeds the new token will push this back.</summary>
    public bool NeedsHeartbeat => DateTime.UtcNow > HeartbeatDueUtc;

    /// <summary>True once we're past <see cref="HeartbeatDueUtc"/> plus
    /// <see cref="HardLockSlack"/>. The app must refuse to start trading
    /// in this state — only re-activation (online) clears it.</summary>
    public bool IsHardLocked => DateTime.UtcNow > HeartbeatDueUtc + HardLockSlack;
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
