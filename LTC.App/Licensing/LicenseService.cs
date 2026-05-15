using System.IO;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace LTC.App.Licensing;

/// <summary>
/// Local license verification. No server, no phone-home, no hardware binding.
///
/// HOW IT WORKS
/// ============
/// We use Ed25519 digital signatures. There's a keypair: a private key (kept
/// secret, lives on the developer's machine, used only by the keygen tool)
/// and a public key (embedded in the app via <see cref="PublicKeyBase64"/>).
///
/// To MINT a license: the keygen tool serializes a <see cref="LicenseInfo"/>
/// into a canonical byte string, signs it with the private key, and emits a
/// human-friendly key string of the form:
///     LTC-{plan}-{base32(payload+signature)}
///
/// To VERIFY a license: the app decodes the key string, splits payload from
/// signature, and asks the public key to verify. If verify succeeds, the
/// payload is trusted (since only the holder of the private key could have
/// produced a valid signature). If verify fails, the key is fake or tampered.
///
/// The key is stored at %LOCALAPPDATA%\LimitlessTradeCopier\license.dat,
/// encrypted via Windows DPAPI (same as the broker credentials -- bound to
/// the current Windows user account). On startup the app calls
/// <see cref="TryLoadActiveLicense"/>; on first run that returns null and we
/// show the LicenseDialog to ask for one.
///
/// SECURITY CAVEATS (intentional, simple-tier limitations)
/// =======================================================
///   * No hardware binding -- one key works on any machine.
///   * No revocation -- if a key leaks you can't kill it without shipping
///     a new app version with an embedded blacklist.
///   * Plan + expiry fields exist in the payload but the current app does
///     NOT enforce them. Any signed key works regardless of "Plan" or
///     "ExpiresUtc" values.
///   * If a determined attacker patches the verification call out of the
///     app binary, the check is bypassed. This is true of every offline
///     license system. We accept it for the simple tier.
///
/// All these are addressed in the planned professional system (server-side
/// activation, hardware fingerprint, revocation list, plan enforcement).
/// </summary>
public sealed class LicenseService
{
    /// <summary>
    /// The Ed25519 PUBLIC key that will verify all license signatures. This
    /// is embedded in the app and is NOT a secret -- it can only verify
    /// signatures, not produce them. The matching private key lives outside
    /// the repo and is used only by the keygen tool.
    /// </summary>
    /// <remarks>
    /// Generated 2026 alongside the simple licensing pass. If you ever need
    /// to rotate this (e.g. private key was leaked), generate a fresh
    /// keypair and ship a new app version with the new public key. All
    /// existing customer keys will then be invalid; you'll need to re-issue
    /// them with the new private key.
    /// </remarks>
    private const string PublicKeyBase64 = "KILODOCu6vN03de7aUorK4kLQT38TOsPZqDiBCdLjOA=";

    private static readonly string LicenseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LimitlessTradeCopier");
    private static readonly string LicenseFile = Path.Combine(LicenseDir, "license.dat");

    /// <summary>
    /// Try to load and verify a previously-saved license from disk. Returns
    /// null if no license is saved, the file is corrupt, or the saved key
    /// fails verification (e.g. somebody hand-edited license.dat). When
    /// null is returned the caller should show the LicenseDialog.
    /// </summary>
    public LicenseInfo? TryLoadActiveLicense()
    {
        try
        {
            if (!File.Exists(LicenseFile)) return null;

            // license.dat is DPAPI-protected per-user. Reading on a different
            // Windows account, or after Windows reset, decrypts to garbage
            // and we treat that as "no license" -- the user will be re-prompted.
            var encrypted = File.ReadAllBytes(LicenseFile);
            var keyString = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(encrypted, optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser));

            return TryVerifyLicense(keyString);
        }
        catch
        {
            // Any error reading/decrypting/verifying -> treat as no license.
            // The user will see the dialog and can re-paste the same key.
            return null;
        }
    }

    /// <summary>
    /// Verify a license key string and, if valid, persist it for future
    /// launches. Called from the LicenseDialog when the user clicks Apply.
    /// Returns the parsed LicenseInfo on success, null on invalid key.
    /// </summary>
    public LicenseInfo? Activate(string keyString)
    {
        var info = TryVerifyLicense(keyString);
        if (info is null) return null;

        // Persist the cleaned-up key string (DPAPI-encrypted at rest).
        try
        {
            Directory.CreateDirectory(LicenseDir);
            var bytes = Encoding.UTF8.GetBytes(NormalizeKey(keyString));
            var encrypted = ProtectedData.Protect(bytes, optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            File.WriteAllBytes(LicenseFile, encrypted);
        }
        catch
        {
            // If we can't write the file we still return success -- the user's
            // key is valid, they'll just see the dialog again next launch.
        }

        return info;
    }

    /// <summary>
    /// Wipe the saved license. Useful for "deactivate this device" flows.
    /// </summary>
    public void Deactivate()
    {
        try
        {
            if (File.Exists(LicenseFile)) File.Delete(LicenseFile);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Verify a key string against the embedded public key without persisting.
    /// Returns the parsed payload on success or null if the signature is bad,
    /// the key is malformed, or the format version is unrecognized.
    /// </summary>
    public LicenseInfo? TryVerifyLicense(string keyString)
    {
        try
        {
            var normalized = NormalizeKey(keyString);
            // Expected shape: "LTC-{plan}-{base32 payload+sig}".
            // The plan tag in the prefix is decorative (helps customer eyeball
            // which key it is); the authoritative plan field comes from the
            // signed payload below.
            var parts = normalized.Split('-', 3);
            if (parts.Length != 3 || parts[0] != "LTC") return null;

            byte[] decoded;
            try
            {
                decoded = Base32Decode(parts[2]);
            }
            catch { return null; }

            // Layout of the decoded blob:
            //   [0..N-65]    canonical payload bytes
            //   [N-64..N-1]  Ed25519 signature (always 64 bytes)
            const int sigLen = 64;
            if (decoded.Length < sigLen + 4) return null;
            var payload   = decoded[..^sigLen];
            var signature = decoded[^sigLen..];

            // Verify signature over the payload using the embedded public key.
            var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519,
                Convert.FromBase64String(PublicKeyBase64),
                KeyBlobFormat.RawPublicKey);
            if (!SignatureAlgorithm.Ed25519.Verify(publicKey, payload, signature))
                return null;

            // Decode the payload. Format is a simple length-prefixed packing
            // we picked for stability:
            //   uint16 LE  email length (bytes)
            //   N  bytes    email (UTF-8)
            //   uint16 LE  plan length
            //   M  bytes    plan
            //   int64 LE   issued (Windows ticks)
            //   int64 LE   expires (Windows ticks)
            int pos = 0;
            string email = ReadLengthPrefixedString(payload, ref pos);
            string plan  = ReadLengthPrefixedString(payload, ref pos);
            long issued  = BitConverter.ToInt64(payload, pos); pos += 8;
            long expires = BitConverter.ToInt64(payload, pos); pos += 8;

            return new LicenseInfo(
                Email:      email,
                Plan:       plan,
                IssuedUtc:  new DateTime(issued,  DateTimeKind.Utc),
                ExpiresUtc: new DateTime(expires, DateTimeKind.Utc));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Strip whitespace, enforce uppercase, and tidy the key string so users
    /// can paste with extra spaces or lowercase without us being pedantic.
    /// </summary>
    private static string NormalizeKey(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Read a length-prefixed UTF-8 string from a byte array, advancing pos.
    /// </summary>
    private static string ReadLengthPrefixedString(byte[] buf, ref int pos)
    {
        ushort len = BitConverter.ToUInt16(buf, pos); pos += 2;
        var s = Encoding.UTF8.GetString(buf, pos, len);
        pos += len;
        return s;
    }

    // -----------------------------------------------------------------
    // Base32 encoding (RFC 4648 alphabet, no padding). We use Base32 not
    // Base64 because Base32 is case-insensitive, friendlier to humans
    // pasting from email, and avoids the +/= characters that get URL-
    // mangled if a customer ever gets the key via a tracked link.
    // -----------------------------------------------------------------
    internal const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    internal static byte[] Base32Decode(string s)
    {
        int outLen = s.Length * 5 / 8;
        var output = new byte[outLen];
        int buffer = 0, bits = 0, idx = 0;
        foreach (char raw in s)
        {
            char c = char.ToUpperInvariant(raw);
            int v = Base32Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException($"Invalid base32 char '{c}'");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                output[idx++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
        }
        return output;
    }

    internal static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                int v = (buffer >> (bits - 5)) & 0x1F;
                sb.Append(Base32Alphabet[v]);
                bits -= 5;
            }
        }
        if (bits > 0)
        {
            int v = (buffer << (5 - bits)) & 0x1F;
            sb.Append(Base32Alphabet[v]);
        }
        return sb.ToString();
    }
}
