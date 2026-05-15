using System;
using System.IO;
using System.Text;
using NSec.Cryptography;

namespace LTC.Core.Licensing;

/// <summary>
/// Canonical binary (de)serialization of <see cref="ActivationToken"/>
/// and Ed25519 signature verification. Lives in LTC.Core so both
/// LTC.App (verifies signatures) and LTC.KeyGen (creates signatures)
/// share the exact same bytes — any drift between the two would break
/// every existing license.
///
/// This class deliberately does NOT touch the filesystem or any
/// Windows-only API. Disk I/O + DPAPI encryption live in the LTC.App
/// layer that wraps this codec.
/// </summary>
public static class ActivationTokenCodec
{
    /// <summary>
    /// The Ed25519 PUBLIC key, baked in at build time. Used to verify
    /// activation tokens. The matching PRIVATE key lives only on the
    /// operator's machine in <c>keygen-private.key</c> and never ships.
    ///
    /// Rotating: bump CurrentFormatVersion, replace this constant, and
    /// reissue every active customer's token. Old tokens stop working.
    /// </summary>
    public const string PublicKeyBase64 = "KILODOCu6vN03de7aUorK4kLQT38TOsPZqDiBCdLjOA=";

    /// <summary>
    /// Serialize the payload portion (everything but the signature)
    /// into the exact byte sequence that gets signed. Both the signer
    /// (admin tool) and the verifier (app) MUST produce identical bytes
    /// or verification fails. The byte layout matches <see cref="ActivationToken"/>'s doc.
    /// </summary>
    public static byte[] SerializePayload(ActivationToken token)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        bw.Write(ActivationToken.CurrentFormatVersion);
        WriteString(bw, token.LicenseKey);
        WriteString(bw, token.Email);
        WriteString(bw, token.Plan);
        bw.Write(token.IssuedUtc.Ticks);
        bw.Write(token.ExpiresUtc.Ticks);
        bw.Write(token.HeartbeatDueUtc.Ticks);
        bw.Write((ushort)32);
        bw.Write(Encoding.ASCII.GetBytes(PadOrTruncateHash(token.Fingerprint.MachineGuid)));
        bw.Write(Encoding.ASCII.GetBytes(PadOrTruncateHash(token.Fingerprint.CpuId)));
        bw.Write(Encoding.ASCII.GetBytes(PadOrTruncateHash(token.Fingerprint.BaseboardSerial)));
        bw.Write(Encoding.ASCII.GetBytes(PadOrTruncateHash(token.Fingerprint.BiosUuid)));

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Combine a payload with its signature into the final on-disk
    /// blob. Used by the admin tool after signing. The app reads back
    /// the same blob via <see cref="Deserialize"/>.
    /// </summary>
    public static byte[] CombinePayloadAndSignature(byte[] payload, byte[] signature)
    {
        var combined = new byte[payload.Length + signature.Length];
        Buffer.BlockCopy(payload, 0, combined, 0, payload.Length);
        Buffer.BlockCopy(signature, 0, combined, payload.Length, signature.Length);
        return combined;
    }

    /// <summary>
    /// Parse the raw token bytes (payload + signature) back into a
    /// <see cref="ActivationToken"/>. Does NOT verify the signature —
    /// that's a separate call so the same code path can be used for
    /// inspection without requiring the public key.
    /// </summary>
    public static ActivationToken Deserialize(byte[] tokenBytes, out byte[] signature)
    {
        using var ms = new MemoryStream(tokenBytes);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        var version = br.ReadByte();
        if (version != ActivationToken.CurrentFormatVersion)
            throw new InvalidDataException(
                $"Activation token format version {version} not supported (expected {ActivationToken.CurrentFormatVersion}).");

        var licenseKey = ReadString(br);
        var email      = ReadString(br);
        var plan       = ReadString(br);
        var issued     = new DateTime(br.ReadInt64(), DateTimeKind.Utc);
        var expires    = new DateTime(br.ReadInt64(), DateTimeKind.Utc);
        var heartbeat  = new DateTime(br.ReadInt64(), DateTimeKind.Utc);

        var hashLen = br.ReadUInt16();
        if (hashLen != 32)
            throw new InvalidDataException($"Unexpected fingerprint hash length {hashLen}.");

        var machineGuid     = Encoding.ASCII.GetString(br.ReadBytes(32));
        var cpuId           = Encoding.ASCII.GetString(br.ReadBytes(32));
        var baseboardSerial = Encoding.ASCII.GetString(br.ReadBytes(32));
        var biosUuid        = Encoding.ASCII.GetString(br.ReadBytes(32));

        signature = br.ReadBytes((int)(ms.Length - ms.Position));

        return new ActivationToken
        {
            LicenseKey      = licenseKey,
            Email           = email,
            Plan            = plan,
            IssuedUtc       = issued,
            ExpiresUtc      = expires,
            HeartbeatDueUtc = heartbeat,
            Fingerprint = new FingerprintBundle(
                machineGuid, cpuId, baseboardSerial, biosUuid),
        };
    }

    /// <summary>
    /// Verify the Ed25519 signature on a serialized payload using the
    /// embedded public key. Never throws — bad data returns false.
    /// </summary>
    public static bool VerifySignature(byte[] payload, byte[] signature)
    {
        try
        {
            var keyBytes = Convert.FromBase64String(PublicKeyBase64);
            var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, keyBytes,
                KeyBlobFormat.RawPublicKey);
            return SignatureAlgorithm.Ed25519.Verify(publicKey, payload, signature);
        }
        catch { return false; }
    }

    /// <summary>
    /// Sign a payload with the given Ed25519 PRIVATE key. Used by the
    /// admin tool. Not used by the app (which has no private key).
    /// </summary>
    public static byte[] Sign(byte[] payload, byte[] privateKeyBytes)
    {
        using var privateKey = Key.Import(SignatureAlgorithm.Ed25519,
            privateKeyBytes, KeyBlobFormat.RawPrivateKey);
        return SignatureAlgorithm.Ed25519.Sign(privateKey, payload);
    }

    /// <summary>
    /// Match score between two fingerprint bundles. Returns 0-4 (count of
    /// matching components). Callers should require >= 3 for "same machine."
    /// </summary>
    public static int MatchScore(FingerprintBundle a, FingerprintBundle b)
    {
        int score = 0;
        if (EqualHashes(a.MachineGuid,     b.MachineGuid))     score++;
        if (EqualHashes(a.CpuId,           b.CpuId))           score++;
        if (EqualHashes(a.BaseboardSerial, b.BaseboardSerial)) score++;
        if (EqualHashes(a.BiosUuid,        b.BiosUuid))        score++;
        return score;
    }

    // -------- HELPERS --------
    private static void WriteString(BinaryWriter bw, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? "");
        bw.Write((ushort)bytes.Length);
        bw.Write(bytes);
    }

    private static string ReadString(BinaryReader br)
    {
        var len = br.ReadUInt16();
        return Encoding.UTF8.GetString(br.ReadBytes(len));
    }

    private static string PadOrTruncateHash(string h)
    {
        if (h.Length == 32) return h;
        if (h.Length > 32)  return h[..32];
        return h.PadRight(32, '0');
    }

    private static bool EqualHashes(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
