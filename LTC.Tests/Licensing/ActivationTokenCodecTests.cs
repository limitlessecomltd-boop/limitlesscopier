using System;
using System.Linq;
using FluentAssertions;
using LTC.Core.Licensing;
using NSec.Cryptography;
using Xunit;

namespace LTC.Tests.Licensing;

/// <summary>
/// Tests for the cross-platform licensing codec — the bit that both the
/// admin tool and the app use to read and write activation tokens. If
/// these tests break, every existing license file in the wild stops
/// working, so we test the byte format strictly.
/// </summary>
public class ActivationTokenCodecTests
{
    private static ActivationToken SampleToken() => new()
    {
        LicenseKey      = "LTC-LIFE-XKQ7-9PRT-FB2C",
        Email           = "alice@example.com",
        Plan            = "Lifetime",
        IssuedUtc       = new DateTime(2026, 5, 12, 14, 30, 0, DateTimeKind.Utc),
        ExpiresUtc      = DateTime.MaxValue,
        HeartbeatDueUtc = new DateTime(2026, 6, 11, 14, 30, 0, DateTimeKind.Utc),
        Fingerprint = new FingerprintBundle(
            MachineGuid:     "A1B2C3D4E5F60718293A4B5C6D7E8F90",
            CpuId:           "B2C3D4E5F60718293A4B5C6D7E8F9011",
            BaseboardSerial: "C3D4E5F60718293A4B5C6D7E8F901122",
            BiosUuid:        "D4E5F60718293A4B5C6D7E8F90112233"),
    };

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var original = SampleToken();
        var payload  = ActivationTokenCodec.SerializePayload(original);

        // We need a signature to round-trip through Deserialize. Sign with
        // a freshly-generated keypair (the test doesn't need the production
        // key — just any signature so the format gets exercised).
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var sig = SignatureAlgorithm.Ed25519.Sign(key, payload);
        var combined = ActivationTokenCodec.CombinePayloadAndSignature(payload, sig);

        var roundTripped = ActivationTokenCodec.Deserialize(combined, out var extractedSig);

        roundTripped.LicenseKey.Should().Be(original.LicenseKey);
        roundTripped.Email.Should().Be(original.Email);
        roundTripped.Plan.Should().Be(original.Plan);
        roundTripped.IssuedUtc.Should().Be(original.IssuedUtc);
        roundTripped.ExpiresUtc.Should().Be(original.ExpiresUtc);
        roundTripped.HeartbeatDueUtc.Should().Be(original.HeartbeatDueUtc);
        roundTripped.Fingerprint.Should().Be(original.Fingerprint);
        extractedSig.Should().Equal(sig);
    }

    [Fact]
    public void TamperedPayload_FailsSignatureVerification()
    {
        var token = SampleToken();
        var payload = ActivationTokenCodec.SerializePayload(token);

        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var sig = SignatureAlgorithm.Ed25519.Sign(key, payload);

        // Flip one byte in the middle of the payload — verify must fail
        var tampered = (byte[])payload.Clone();
        tampered[20] ^= 0xFF;

        // We're using a test keypair, not the embedded production public
        // key, so we have to test verification via the same key.
        var publicBytes = key.Export(KeyBlobFormat.RawPublicKey);
        var pub = PublicKey.Import(SignatureAlgorithm.Ed25519, publicBytes,
            KeyBlobFormat.RawPublicKey);

        SignatureAlgorithm.Ed25519.Verify(pub, payload, sig).Should().BeTrue();
        SignatureAlgorithm.Ed25519.Verify(pub, tampered, sig).Should().BeFalse(
            "any byte change should invalidate the signature");
    }

    [Fact]
    public void DifferentFormatVersion_RejectedOnDeserialize()
    {
        // Hand-craft a payload with a bogus version byte at offset 0
        var token = SampleToken();
        var realPayload = ActivationTokenCodec.SerializePayload(token);
        var fakePayload = (byte[])realPayload.Clone();
        fakePayload[0] = 99;
        var combined = new byte[fakePayload.Length + 64];
        Array.Copy(fakePayload, combined, fakePayload.Length);
        // Signature bytes don't matter for this test — Deserialize throws
        // before signature check.

        var act = () => ActivationTokenCodec.Deserialize(combined, out _);
        act.Should().Throw<System.IO.InvalidDataException>()
           .WithMessage("*format version 99*");
    }

    // ---------- MatchScore ----------

    [Fact]
    public void IdenticalFingerprints_ScoreFour()
    {
        var fp = new FingerprintBundle("A", "B", "C", "D");
        ActivationTokenCodec.MatchScore(fp, fp).Should().Be(4);
    }

    [Fact]
    public void CompletelyDifferent_ScoreZero()
    {
        var a = new FingerprintBundle("A1", "A2", "A3", "A4");
        var b = new FingerprintBundle("B1", "B2", "B3", "B4");
        ActivationTokenCodec.MatchScore(a, b).Should().Be(0);
    }

    [Fact]
    public void OneComponentDiffers_ScoreThree()
    {
        // The realistic case: customer replaces a NIC or upgrades CPU.
        var stored = new FingerprintBundle("MGUID", "CPU_OLD", "BOARD", "BIOS");
        var live   = new FingerprintBundle("MGUID", "CPU_NEW", "BOARD", "BIOS");
        ActivationTokenCodec.MatchScore(stored, live).Should().Be(3,
            "3 of 4 should still pass the >= 3 threshold");
    }

    [Fact]
    public void TwoComponentsDiffer_ScoreTwo_BelowThreshold()
    {
        var stored = new FingerprintBundle("MGUID_OLD", "CPU_OLD", "BOARD", "BIOS");
        var live   = new FingerprintBundle("MGUID_NEW", "CPU_NEW", "BOARD", "BIOS");
        ActivationTokenCodec.MatchScore(stored, live).Should().Be(2,
            "2 of 4 should fail (below required 3) — likely a different machine");
    }

    [Fact]
    public void CaseInsensitive_HashComparison()
    {
        var a = new FingerprintBundle("ABCDEF", "DEFFED", "XX", "YY");
        var b = new FingerprintBundle("abcdef", "DEFFED", "xx", "yy");
        ActivationTokenCodec.MatchScore(a, b).Should().Be(4,
            "hex hashes should compare case-insensitively");
    }
}
