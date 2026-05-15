using System;
using System.IO;
using LTC.Server.Models;

namespace LTC.Server.Services;

/// <summary>
/// Server-side signer for activation tokens. Loads the Ed25519 private
/// key once at startup and reuses it for every /activate and /heartbeat
/// response.
///
/// Key loading priority (first one that yields a non-empty value wins):
///   1. Signing:PrivateKey       — env var or config value containing the
///                                 raw base64-encoded private key. Preferred
///                                 for cloud platforms (Railway, Fly, AWS
///                                 Secrets Manager → env var). The key never
///                                 touches disk this way.
///   2. Signing:PrivateKeyPath   — file path to a base64 key file. Used by
///                                 local dev (./keygen-private.key) and by
///                                 VPS installs that mount the key as a file.
///
/// Both can be supplied via appsettings.json keys ("Signing": {...}) or via
/// environment variables using the double-underscore convention
/// (Signing__PrivateKey, Signing__PrivateKeyPath). ASP.NET Core's
/// IConfiguration merges these transparently.
///
/// In production we use Signing__PrivateKey (env var) on Railway. In dev
/// we use Signing:PrivateKeyPath pointing at ../keygen-private.key.
/// </summary>
public sealed class TokenSigningService
{
    private readonly byte[] _privateKeyBytes;
    private readonly ILogger<TokenSigningService> _log;

    public TokenSigningService(IConfiguration config, ILogger<TokenSigningService> log)
    {
        _log = log;

        // 1) Try the env-var / config string first.
        var inlineKey = config["Signing:PrivateKey"];
        if (!string.IsNullOrWhiteSpace(inlineKey))
        {
            _privateKeyBytes = ParseBase64OrThrow(inlineKey.Trim(),
                source: "Signing:PrivateKey (inline)");
            _log.LogInformation(
                "Loaded activation server signing key from Signing:PrivateKey env var ({Bytes} bytes)",
                _privateKeyBytes.Length);
            return;
        }

        // 2) Fall back to the file path.
        var path = config["Signing:PrivateKeyPath"] ?? "keygen-private.key";
        if (!File.Exists(path))
        {
            // Fail fast — the server is useless without the private key.
            // Crashes startup so a deploy operator notices immediately
            // rather than serving 500s silently.
            throw new InvalidOperationException(
                $"Activation server private key not configured. Set either " +
                $"Signing__PrivateKey (inline base64 string, recommended for " +
                $"Railway/cloud) or Signing__PrivateKeyPath (file path). " +
                $"Tried path '{path}' — file not found.");
        }

        var raw = File.ReadAllText(path).Trim();
        _privateKeyBytes = ParseBase64OrThrow(raw, source: $"file '{path}'");
        _log.LogInformation(
            "Loaded activation server signing key from {Path} ({Bytes} bytes)",
            path, _privateKeyBytes.Length);
    }

    /// <summary>
    /// Decode a base64 string to bytes, with a clean error message if it
    /// isn't valid base64. Shared between the env-var and file paths so
    /// both surface the same kind of failure.
    /// </summary>
    private static byte[] ParseBase64OrThrow(string base64, string source)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Private key from {source} is not valid Base64: {ex.Message}");
        }
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
