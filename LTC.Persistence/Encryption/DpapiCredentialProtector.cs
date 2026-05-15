using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace LTC.Persistence.Encryption;

/// <summary>
/// Encrypts and decrypts strings using Windows Data Protection API (DPAPI).
/// The encryption key is derived from the current Windows user's profile, so
/// the database file is unreadable on any other machine or by any other user
/// (including admins on the same machine — DPAPI scope is per-user).
/// </summary>
/// <remarks>
/// This is the appropriate choice for a single-machine desktop app. It removes
/// the need to ask the user for a master password on every startup, while
/// keeping credentials safer than plaintext.
///
/// Limitations:
///   * Windows-only. macOS / Linux ports would need DPAPI-NG or an explicit
///     password-based KDF.
///   * Loses access if the user's Windows profile is reset (e.g. clean install).
///     This is by design — losing your Windows account should mean losing the
///     stored credentials.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialProtector : ICredentialProtector
{
    // Optional entropy: an extra fixed value mixed into encryption. Acts like a salt.
    // Hardcoded here because the DPAPI master key is already user-scoped; this just
    // adds defence-in-depth against another local DPAPI-using app accidentally
    // decrypting our blobs.
    private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("LimitlessTradeCopier:v1");

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, AppEntropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public string Unprotect(string protectedData)
    {
        if (string.IsNullOrEmpty(protectedData)) return string.Empty;

        try
        {
            var encrypted = Convert.FromBase64String(protectedData);
            var bytes = ProtectedData.Unprotect(encrypted, AppEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            // Tampered, wrong user, or wrong machine. Caller decides what to do.
            throw;
        }
        catch (FormatException)
        {
            // Not valid base64 — maybe the DB was migrated from plaintext.
            throw new CryptographicException("Stored credential is not in a valid encrypted format.");
        }
    }
}
