using System;
using System.IO;
using LTC.Server.Models;

namespace LTC.Server.Services;

/// <summary>
/// Server-side signer for activation tokens. Reads the Ed25519 private
/// key once at startup from the configured path and reuses it for every
/// /activate and /heartbeat response.
///
/// Configuration:
///   appsettings.json → "Signing" → "PrivateKeyPath"
///   default = "keygen-private.key" (relative to app working dir)
///
/// On the production server, this should be set to something like
/// "/etc/limitless/keygen-private.key" with chmod 0400 (owner-read-only)
/// and owned by the service user.
/// </summary>
public sealed class TokenSigningService
{
    private readonly byte[] _privateKeyBytes;
    private readonly ILogger<TokenSigningService> _log;

    public TokenSigningService(IConfiguration config, ILogger<TokenSigningService> log)
    {
        _log = log;

        var path = config["Signing:PrivateKeyPath"] ?? "keygen-private.key";

        if (!File.Exists(path))
        {
            // Fail fast — the server is useless without the private key.
            // This crashes the app on startup so an admin notices immediately
            // rather than serving 500 errors silently.
            throw new InvalidOperationException(
                $"Activation server private key not found at '{path}'. " +
                $"Set Signing:PrivateKeyPath in appsettings.json or env var. " +
                $"On the production VPS this should be /etc/limitless/keygen-private.key " +
                $"with chmod 0400 ownership by the service user.");
        }

        var raw = File.ReadAllText(path).Trim();
        try
        {
            _privateKeyBytes = Convert.FromBase64String(raw);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Private key file at '{path}' is not valid Base64: {ex.Message}");
        }

        _log.LogInformation("Loaded activation server signing key from {Path} ({Bytes} bytes)",
            path, _privateKeyBytes.Length);
    }

    /// <summary>
    /// Serialize the token payload, sign it, return the combined bytes
    /// the customer's app will store as activation.dat.
    /// </summary>
    public byte[] SignToken(ActivationToken token)
    {
        var payload   = ActivationTokenCodec.SerializePayload(token);
        var signature = ActivationTokenCodec.Sign(payload, _privateKeyBytes);
        return ActivationTokenCodec.CombinePayloadAndSignature(payload, signature);
    }
}
