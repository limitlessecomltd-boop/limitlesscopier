using System;
using System.Threading;
using System.Threading.Tasks;
using LTC.Core.Licensing;

namespace LTC.App.Licensing;

/// <summary>
/// High-level licensing facade — the only thing App.xaml.cs and the
/// License dialog should know about. Decides on each launch whether the
/// app is licensed for this machine, and if not, what the user should
/// see (License dialog, expired warning, hardware mismatch error, etc.).
///
/// Two activation paths, both produce the same artifact (a signed token
/// at <c>activation.dat</c>) and are treated identically afterwards:
///
///   1. <see cref="ActivateOnlineAsync"/> — customer pastes a license key,
///      app calls the activation server, server signs a token bound to
///      this machine. This is the normal path for paid customers.
///
///   2. <see cref="TryInstallLicenseFile"/> — operator emails a pre-signed
///      <c>.lic</c> file to the customer (support cases, air-gapped
///      installs, refunds-as-replacements). Still uses the same
///      <see cref="ActivationTokenCodec"/> verifier — the file is just a
///      pre-built version of what the server would have signed.
///
/// Once a token exists on disk, <see cref="HeartbeatAsync"/> runs every
/// few hours to refresh it. If the server is reachable and the license
/// is still valid, the heartbeat returns a fresh token with a new
/// <see cref="ActivationToken.HeartbeatDueUtc"/>. If the server is
/// unreachable, the existing token continues to be honored until
/// <see cref="ActivationToken.HardLockSlack"/> past its expiry.
/// </summary>
public sealed class ActivationService
{
    private readonly ActivationTokenStore _tokens = new();
    private readonly LicenseApiClient _api;

    public ActivationService() : this(new LicenseApiClient()) { }

    /// <summary>Constructor for tests that want to inject a fake API client.</summary>
    public ActivationService(LicenseApiClient api)
    {
        _api = api;
    }

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
                Message: "Not activated. Enter your license key to begin.");
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
            // We're past HeartbeatDueUtc + HardLockSlack (52h since last
            // successful server contact). Refuse to operate until the
            // user reconnects and we successfully heartbeat again.
            return new ActivationStatus(
                ActivationState.HeartbeatRequired,
                Token: token,
                Message: "Couldn't verify your license for over 2 days. "
                       + "Please reconnect to the internet and restart the app.");
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
    /// PRIMARY ACTIVATION PATH (online): customer pastes a license key,
    /// we send {key, fingerprint} to the activation server, server signs
    /// a token bound to this machine, we persist it.
    ///
    /// Returns an <see cref="OnlineActivationResult"/> describing what
    /// happened. The UI uses Kind to decide what to show:
    ///   - Success           → close dialog, proceed to main window
    ///   - ServerRejected    → show the message (e.g. "License revoked")
    ///   - NetworkFailure    → show "Couldn't reach server, check internet"
    ///   - LocalError        → show "Invalid input"
    /// </summary>
    public async Task<OnlineActivationResult> ActivateOnlineAsync(
        string licenseKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            return new OnlineActivationResult(
                OnlineActivationKind.LocalError, "Enter a license key.", null);

        string fingerprint;
        try
        {
            fingerprint = GetMachineFingerprintFull();
        }
        catch (Exception ex)
        {
            return new OnlineActivationResult(
                OnlineActivationKind.LocalError,
                $"Could not compute hardware fingerprint: {ex.Message}",
                null);
        }

        var api = await _api.ActivateAsync(licenseKey.Trim(), fingerprint, ct)
            .ConfigureAwait(false);

        return ApplyApiResult(api, fingerprint);
    }

    /// <summary>
    /// Refresh an existing activation. Returns the new status so the UI
    /// can react if (e.g.) the server has revoked the license since the
    /// last heartbeat.
    ///
    /// On network failure this is a NO-OP — we keep the existing token
    /// and let <see cref="GetStatus"/> handle the offline-grace logic.
    /// </summary>
    public async Task<OnlineActivationResult> HeartbeatAsync(CancellationToken ct = default)
    {
        var existing = _tokens.LoadFromDisk();
        if (existing is null)
        {
            return new OnlineActivationResult(
                OnlineActivationKind.LocalError,
                "No activation on disk to heartbeat.", null);
        }

        string fingerprint;
        try
        {
            fingerprint = GetMachineFingerprintFull();
        }
        catch (Exception ex)
        {
            return new OnlineActivationResult(
                OnlineActivationKind.LocalError,
                $"Could not compute hardware fingerprint: {ex.Message}",
                null);
        }

        var api = await _api.HeartbeatAsync(existing.LicenseKey, fingerprint, ct)
            .ConfigureAwait(false);

        return ApplyApiResult(api, fingerprint);
    }

    /// <summary>
    /// Shared post-processing for both /activate and /heartbeat results.
    /// On Success, verifies the signature locally, sanity-checks the
    /// returned fingerprint matches this machine, and persists.
    /// </summary>
    private OnlineActivationResult ApplyApiResult(LicenseApiResult api, string sentFingerprint)
    {
        switch (api.Kind)
        {
            case ResultKind.Success:
                if (api.TokenBytes is null || api.TokenBytes.Length == 0)
                    return new OnlineActivationResult(OnlineActivationKind.ServerRejected,
                        "Server returned no activation token. Contact support.", null);

                ActivationToken signedToken;
                byte[] signature;
                try
                {
                    signedToken = ActivationTokenCodec.Deserialize(api.TokenBytes, out signature);
                }
                catch (Exception ex)
                {
                    return new OnlineActivationResult(OnlineActivationKind.ServerRejected,
                        $"Server returned an unreadable token: {ex.Message}", null);
                }

                // Verify signature with the embedded public key. If this
                // fails, the server response is forged or our keys don't
                // match — either way, treat it as a hard error.
                var payload = ActivationTokenCodec.SerializePayload(signedToken);
                if (!ActivationTokenCodec.VerifySignature(payload, signature))
                {
                    return new OnlineActivationResult(OnlineActivationKind.ServerRejected,
                        "Server returned a token with an invalid signature. "
                        + "If you're sure you're on the official limitlesscopier.com server, "
                        + "your app may need to be updated to the latest version.",
                        null);
                }

                // Sanity-check: the token the server signed should bind to
                // the SAME fingerprint we sent. Anything else means a bug
                // or a man-in-the-middle.
                var live = HardwareFingerprint.Compute();
                var matchScore = ActivationTokenCodec.MatchScore(signedToken.Fingerprint, live);
                if (matchScore < HardwareFingerprint.RequiredMatchCount)
                {
                    return new OnlineActivationResult(OnlineActivationKind.ServerRejected,
                        $"Server signed a token for a different machine "
                        + $"({matchScore}/4 components match). This shouldn't happen — "
                        + "please contact support.", null);
                }

                // All checks pass. Persist.
                try
                {
                    _tokens.SaveToDisk(api.TokenBytes);
                }
                catch (Exception ex)
                {
                    return new OnlineActivationResult(OnlineActivationKind.LocalError,
                        $"Could not save activation file: {ex.Message}", null);
                }
                return new OnlineActivationResult(OnlineActivationKind.Success,
                    api.Message, signedToken);

            case ResultKind.ServerRejected:
                return new OnlineActivationResult(OnlineActivationKind.ServerRejected,
                    api.Message, null);

            case ResultKind.NetworkFailure:
                return new OnlineActivationResult(OnlineActivationKind.NetworkFailure,
                    api.Message, null);

            case ResultKind.LocalError:
            default:
                return new OnlineActivationResult(OnlineActivationKind.LocalError,
                    api.Message, null);
        }
    }

    /// <summary>
    /// FALLBACK ACTIVATION PATH (offline): install a .lic file the customer
    /// received from support. Validates the signature, checks the fingerprint
    /// matches THIS machine, and writes it to activation.dat. Returns true on
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

            _tokens.SaveToDisk(licFileBytes);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Could not read the license file: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Clear the local activation. Tries to notify the server first so
    /// the activation slot is freed (allowing the user to activate on a
    /// different machine). If the server is unreachable we delete locally
    /// anyway — the user can still re-activate via support if needed.
    /// </summary>
    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        var existing = _tokens.LoadFromDisk();
        if (existing is not null)
        {
            try
            {
                var fingerprint = GetMachineFingerprintFull();
                await _api.DeactivateAsync(existing.LicenseKey, fingerprint, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best effort — if the server call fails we still delete locally
                // so the customer isn't stuck. The slot stays bound server-side
                // until they contact support, but their app behaves correctly.
            }
        }
        _tokens.ClearFromDisk();
    }

    /// <summary>Synchronous local-only deactivate. Same as the old API
    /// for callers that haven't been updated to async.</summary>
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
    HeartbeatRequired,   // Past HeartbeatDueUtc + slack — must reconnect
}

/// <summary>
/// Result of <see cref="ActivationService.ActivateOnlineAsync"/> /
/// <see cref="ActivationService.HeartbeatAsync"/>. The token field is
/// only populated on Success.
/// </summary>
public sealed record OnlineActivationResult(
    OnlineActivationKind Kind,
    string Message,
    ActivationToken? Token);

public enum OnlineActivationKind
{
    /// <summary>Server accepted, token signed and persisted.</summary>
    Success,
    /// <summary>Server reachable but said no (revoked, expired, wrong machine, etc).</summary>
    ServerRejected,
    /// <summary>Couldn't reach server. Existing cached token (if any) still applies.</summary>
    NetworkFailure,
    /// <summary>Bad input or local-side problem; nothing was sent.</summary>
    LocalError,
}
