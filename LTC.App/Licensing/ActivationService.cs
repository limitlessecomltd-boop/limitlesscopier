using System;
using LTC.Core.Licensing;

namespace LTC.App.Licensing;

/// <summary>
/// High-level licensing facade — the only thing App.xaml.cs and the
/// License dialog should know about. Decides on each launch whether the
/// app is licensed for this machine, and if not, what the user should
/// see (License dialog, expired warning, hardware mismatch error, etc.).
///
/// This session ships the OFFLINE / manual-issuance flow:
///   - Customer pays
///   - You ask them for their fingerprint (the License dialog displays it)
///   - You run `ltc-admin mint` with --fingerprint X --email Y --plan Z
///   - Tool writes a .lic file
///   - Customer drops the .lic at %LOCALAPPDATA%\LimitlessTradeCopier\activation.dat
///   - App's next launch verifies the signature + fingerprint match
///
/// A future session adds the activation server, at which point the
/// "GetStatus" call will optionally heartbeat to the server. The
/// API surface stays the same so nothing else has to change.
/// </summary>
public sealed class ActivationService
{
    private readonly ActivationTokenStore _tokens = new();

    /// <summary>
    /// Resolve the current activation status. Reads the saved token,
    /// verifies its signature, compares fingerprints, returns a status
    /// the UI can switch on.
    /// </summary>
    public ActivationStatus GetStatus()
    {
        var token = _tokens.LoadFromDisk();
        if (token is null)
        {
            return new ActivationStatus(
                ActivationState.NotActivated,
                Token: null,
                Message: "Not activated. Enter a license key to begin.");
        }

        if (token.IsExpired)
        {
            return new ActivationStatus(
                ActivationState.Expired,
                Token: token,
                Message: $"License expired on {token.ExpiresUtc:yyyy-MM-dd}.");
        }

        // Compare live fingerprint to stored fingerprint
        var live = HardwareFingerprint.Compute();
        var matchScore = ActivationTokenCodec.MatchScore(token.Fingerprint, live);
        if (matchScore < HardwareFingerprint.RequiredMatchCount)
        {
            return new ActivationStatus(
                ActivationState.HardwareMismatch,
                Token: token,
                Message: $"This license is bound to a different machine "
                       + $"({matchScore}/4 hardware components match, need "
                       + $"{HardwareFingerprint.RequiredMatchCount}). Contact support to migrate.");
        }

        if (token.IsHardLocked)
        {
            // Server heartbeat has been overdue for 30+7 days. Not yet
            // implemented (no server) but the gate is here for later.
            return new ActivationStatus(
                ActivationState.HeartbeatRequired,
                Token: token,
                Message: "License must re-verify online. Connect to the internet and restart.");
        }

        return new ActivationStatus(
            ActivationState.Active,
            Token: token,
            Message: $"Licensed to {token.Email} ({token.Plan}).");
    }

    /// <summary>Compute the current machine's fingerprint and return a
    /// short displayable code. Shown in the License dialog so the
    /// customer can read it off and send it to support for manual
    /// issuance.</summary>
    public string GetMachineFingerprintDisplay()
    {
        var bundle = HardwareFingerprint.Compute();
        return HardwareFingerprint.ShortDisplay(bundle);
    }

    /// <summary>Get the raw fingerprint bundle so the customer can
    /// export it as a longer string for the admin to use when
    /// minting.</summary>
    public string GetMachineFingerprintFull()
    {
        var bundle = HardwareFingerprint.Compute();
        // Concatenate the 4 hashes with separators for readability.
        // The admin tool's `mint --fingerprint X` accepts this string.
        return $"{bundle.MachineGuid}-{bundle.CpuId}-{bundle.BaseboardSerial}-{bundle.BiosUuid}";
    }

    /// <summary>
    /// Install a .lic file the customer received from support. Validates
    /// the signature, checks the fingerprint matches THIS machine, and
    /// writes it to the standard activation.dat location. Returns true on
    /// success; on failure populates errorMessage with a user-facing
    /// description.
    /// </summary>
    public bool TryInstallLicenseFile(byte[] licFileBytes, out string errorMessage)
    {
        errorMessage = "";
        try
        {
            // The .lic file content IS the signed token bytes (no DPAPI
            // wrapper since it has to be portable across users).
            var token = ActivationTokenCodec.Deserialize(licFileBytes, out var signature);
            var payload = ActivationTokenCodec.SerializePayload(token);
            if (!ActivationTokenCodec.VerifySignature(payload, signature))
            {
                errorMessage = "License file signature is invalid. The file may be corrupted, modified, or issued under a different key.";
                return false;
            }

            // Check fingerprint matches THIS machine before installing —
            // otherwise the customer would activate, then crash on the
            // next launch with a confusing "wrong machine" error.
            var live = HardwareFingerprint.Compute();
            var matchScore = ActivationTokenCodec.MatchScore(token.Fingerprint, live);
            if (matchScore < HardwareFingerprint.RequiredMatchCount)
            {
                errorMessage = $"This license is bound to a different machine ({matchScore}/4 components match). The fingerprint sent to support must not have been from this PC.";
                return false;
            }

            if (token.IsExpired)
            {
                errorMessage = $"This license expired on {token.ExpiresUtc:yyyy-MM-dd}.";
                return false;
            }

            // All checks pass — DPAPI-encrypt and save
            _tokens.SaveToDisk(licFileBytes);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Could not read the license file: {ex.Message}";
            return false;
        }
    }

    /// <summary>Clear the local activation. Used when the user wants to
    /// move to a new machine — they call support, support runs deactivate
    /// on the server (future), and the local file is removed so they can
    /// re-activate elsewhere.</summary>
    public void Deactivate() => _tokens.ClearFromDisk();
}

/// <summary>What state the app's license is in, as a discriminated value
/// the UI can switch on. The bundled Token is null only in NotActivated.</summary>
public sealed record ActivationStatus(
    ActivationState State,
    ActivationToken? Token,
    string Message);

public enum ActivationState
{
    NotActivated,        // No activation.dat exists
    Active,              // Token valid, fingerprint matches, not expired
    Expired,             // Token's expiry date has passed
    HardwareMismatch,    // Fewer than 3 of 4 components match
    HeartbeatRequired,   // (future) Server check overdue
}
