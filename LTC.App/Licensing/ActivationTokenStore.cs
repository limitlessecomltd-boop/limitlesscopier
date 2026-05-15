using System;
using System.IO;
using System.Security.Cryptography;
using LTC.Core.Licensing;

namespace LTC.App.Licensing;

/// <summary>
/// Disk persistence for activation tokens. DPAPI-encrypts the bytes on
/// the way to disk so a copy of activation.dat from PC A to PC B is
/// unreadable on B even before the hardware fingerprint check fires.
///
/// The token bytes themselves are already signed (see <see cref="ActivationTokenCodec"/>);
/// DPAPI here is a second line of defense, not the primary one.
///
/// LIVES IN LTC.APP because DPAPI is Windows-only. The cross-platform
/// signing + parsing lives in LTC.Core so the admin tool can use it.
/// </summary>
public sealed class ActivationTokenStore
{
    /// <summary>Disk path for the activation token (DPAPI-encrypted).</summary>
    public static string DefaultActivationPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LimitlessTradeCopier", "activation.dat");

    /// <summary>
    /// Save signed token bytes to disk, DPAPI-encrypted to the current
    /// user. The bytes passed in should be the output of
    /// <see cref="ActivationTokenCodec.CombinePayloadAndSignature"/>.
    /// </summary>
    public void SaveToDisk(byte[] signedTokenBytes, string? path = null)
    {
        path ??= DefaultActivationPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encrypted = ProtectedData.Protect(signedTokenBytes,
            optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, encrypted);
    }

    /// <summary>
    /// Load + verify the activation token from disk. Returns null if the
    /// file doesn't exist, if DPAPI decryption fails, or if the
    /// signature doesn't verify.
    /// </summary>
    public ActivationToken? LoadFromDisk(string? path = null)
    {
        path ??= DefaultActivationPath;
        if (!File.Exists(path)) return null;

        try
        {
            var encrypted = File.ReadAllBytes(path);
            var decrypted = ProtectedData.Unprotect(encrypted,
                optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            var token = ActivationTokenCodec.Deserialize(decrypted, out var signature);
            var payload = ActivationTokenCodec.SerializePayload(token);
            if (!ActivationTokenCodec.VerifySignature(payload, signature)) return null;
            return token;
        }
        catch { return null; }
    }

    /// <summary>Delete the activation file. Used during deactivate.</summary>
    public void ClearFromDisk(string? path = null)
    {
        path ??= DefaultActivationPath;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* swallow: cleanup */ }
    }
}
