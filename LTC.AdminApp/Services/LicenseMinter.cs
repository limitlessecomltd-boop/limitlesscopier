using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LTC.Core.Licensing;

namespace LTC.AdminApp.Services;

/// <summary>
/// Signs and writes activation tokens. Takes the form data the user
/// entered in the Mint tab, validates it, signs with the Ed25519
/// private key, and writes a .lic file ready to send to the customer.
///
/// The cryptography lives in LTC.Core.ActivationTokenCodec — this is
/// just the form-validation + key-loading + file-writing wrapper.
/// </summary>
public sealed class LicenseMinter
{
    private const string DefaultKeyFileName = "keygen-private.key";

    /// <summary>
    /// Mint a license. Returns the signed token bytes (caller writes to
    /// disk) plus the customer-facing key string. Throws
    /// <see cref="MintException"/> on validation/IO/crypto failure with
    /// a user-readable message.
    /// </summary>
    public MintResult Mint(MintRequest request)
    {
        // -------- validate inputs --------
        if (string.IsNullOrWhiteSpace(request.Email))
            throw new MintException("Email is required.");

        if (string.IsNullOrWhiteSpace(request.Plan))
            throw new MintException("Plan is required.");

        var fingerprintRaw = request.Fingerprint?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(fingerprintRaw))
            throw new MintException("Fingerprint is required. Paste the long fingerprint string from the customer's License dialog.");

        var parts = fingerprintRaw.Split('-');
        if (parts.Length != 4 || parts.Any(p => p.Length != 32))
        {
            throw new MintException(
                $"Fingerprint format is wrong — expected 4 hex chunks of 32 chars each, separated by '-'. " +
                $"Got {parts.Length} chunks with lengths: {string.Join(", ", parts.Select(p => p.Length))}.");
        }
        var bundle = new FingerprintBundle(parts[0], parts[1], parts[2], parts[3]);

        // -------- load private key --------
        var keyPath = string.IsNullOrWhiteSpace(request.PrivateKeyPath)
            ? DefaultKeyFileName
            : request.PrivateKeyPath;

        if (!File.Exists(keyPath))
        {
            throw new MintException(
                $"Private key file not found at:\n  {Path.GetFullPath(keyPath)}\n\n" +
                $"This is the keygen-private.key file. Set its path in Settings, " +
                $"or place a copy in the admin app's working directory.");
        }

        byte[] privateKeyBytes;
        try
        {
            var raw = File.ReadAllText(keyPath).Trim();
            privateKeyBytes = Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            throw new MintException("Private key file is not valid Base64. It may be corrupted or have been written in a different format.");
        }
        catch (IOException ex)
        {
            throw new MintException($"Could not read the private key file: {ex.Message}");
        }

        // -------- build token --------
        var issued    = DateTime.UtcNow;
        var expires   = request.Days > 0 ? issued.AddDays(request.Days) : DateTime.MaxValue;
        var heartbeat = request.Days > 0 ? expires : issued.AddDays(30);

        var planTag = request.Plan.ToUpperInvariant();
        if (planTag.Length > 4) planTag = planTag[..4];
        var licenseKey = $"LTC-{planTag}-{GenerateKeySegments(3, 4)}";

        var token = new ActivationToken
        {
            LicenseKey      = licenseKey,
            Email           = request.Email.Trim(),
            Plan            = request.Plan,
            IssuedUtc       = issued,
            ExpiresUtc      = expires,
            HeartbeatDueUtc = heartbeat,
            Fingerprint     = bundle,
        };

        // -------- sign --------
        var payload   = ActivationTokenCodec.SerializePayload(token);
        byte[] signature;
        try
        {
            signature = ActivationTokenCodec.Sign(payload, privateKeyBytes);
        }
        catch (Exception ex)
        {
            throw new MintException($"Signing failed (private key may be invalid): {ex.Message}");
        }

        // Self-test: verify the just-produced signature with the embedded
        // public key. Guards against the private-key-on-disk being from
        // a DIFFERENT keypair than the one baked into the customer app.
        var verified = ActivationTokenCodec.VerifySignature(payload, signature);
        if (!verified)
        {
            throw new MintException(
                "Just-written signature failed verification! The private key file " +
                "doesn't match the public key embedded in the customer app. " +
                "Either you're using the wrong keypair, or the customer app was " +
                "built with an outdated public key.");
        }

        var combined = ActivationTokenCodec.CombinePayloadAndSignature(payload, signature);

        return new MintResult(
            LicenseBytes: combined,
            LicenseKey:   licenseKey,
            Token:        token);
    }

    /// <summary>Write a minted license file to disk. Always overwrites.</summary>
    public void WriteToFile(MintResult result, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, result.LicenseBytes);
    }

    /// <summary>Build the visible portion of the license key — random
    /// groups separated by dashes. The "real" license content is the
    /// signed token; this string is decorative + helps customers
    /// reference their key visually.</summary>
    private static string GenerateKeySegments(int groups, int charsPerGroup)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I, O, 0, 1
        var rng = RandomNumberGenerator.Create();
        var sb = new StringBuilder();
        var buf = new byte[1];
        for (int g = 0; g < groups; g++)
        {
            if (g > 0) sb.Append('-');
            for (int c = 0; c < charsPerGroup; c++)
            {
                rng.GetBytes(buf);
                sb.Append(alphabet[buf[0] % alphabet.Length]);
            }
        }
        return sb.ToString();
    }
}

/// <summary>Inputs for a mint operation.</summary>
public sealed record MintRequest(
    string Email,
    string Plan,
    string Fingerprint,
    int Days,                       // 0 = lifetime
    string PrivateKeyPath);         // empty = default path

/// <summary>Outputs of a successful mint.</summary>
public sealed record MintResult(
    byte[] LicenseBytes,
    string LicenseKey,
    ActivationToken Token);

/// <summary>User-readable mint failure. Thrown for any reason the
/// operator should see — bad input, missing key, sign failure, etc.</summary>
public sealed class MintException : Exception
{
    public MintException(string message) : base(message) { }
}
