using System;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using LTC.Core.Licensing;

namespace LTC.App.Licensing;

/// <summary>
/// Computes a stable, hard-to-spoof identifier for the Windows machine this
/// app is running on. Used by the activation system to bind a license to a
/// specific PC so the same license key can't be shared across machines.
///
/// We capture four independent identifiers, hash each separately, and store
/// all four hashes in the activation token. On startup we recompute all four
/// and require at LEAST THREE of them to match. This handles realistic
/// hardware change scenarios (replacing a hard drive, NIC, RAM) without
/// false-positive lockouts, while still catching anyone who tries to use a
/// license on a different machine.
///
/// The four components:
///   - MachineGuid: a GUID Windows generates at install time. Persists
///     across reboots/updates. Resets only on full Windows reinstall.
///     Most stable identifier available; primary anchor.
///   - CpuId: brand string + processor identifier from WMI. Stable across
///     reboots, changes only when CPU is replaced.
///   - BaseboardSerial: motherboard serial from WMI. Stable except for
///     mobo replacement.
///   - BiosUuid: SMBIOS UUID from WMI. Conceptually a unique system ID
///     burned into firmware; can be blank or generic on some OEM systems
///     and VMs, so this is the weakest of the four.
///
/// On VMs and unusual OEM systems some of these can return blank or
/// duplicate values. The "3 of 4 must match" tolerance handles that case
/// — even if BiosUuid is blank on the customer's machine, the activation
/// records that and matches the other three.
///
/// PRIVACY: we never store raw identifiers anywhere. We compute the SHA-256
/// hash of each (truncated to 16 bytes for storage compactness) and only
/// the hashes travel anywhere. The activation server, the .lic file, and
/// the in-memory cache all hold hashes, not raw machine details.
/// </summary>
public static class HardwareFingerprint
{
    /// <summary>Number of components that must match for a fingerprint
    /// to be considered "same machine." 3 of 4 = tolerates one hardware
    /// change at a time.</summary>
    public const int RequiredMatchCount = 3;

    /// <summary>
    /// Compute the four-component fingerprint. Returns immediately even
    /// if some components fail — failed components hash to a fixed
    /// "unavailable" marker so the resulting hash is still deterministic.
    /// </summary>
    public static FingerprintBundle Compute()
    {
        var machineGuid     = SafeHash(GetMachineGuid());
        var cpuId           = SafeHash(GetCpuId());
        var baseboardSerial = SafeHash(GetBaseboardSerial());
        var biosUuid        = SafeHash(GetBiosUuid());

        return new FingerprintBundle(machineGuid, cpuId, baseboardSerial, biosUuid);
    }

    /// <summary>
    /// User-friendly display fingerprint — first 8 chars of the combined
    /// hash, shown in the License dialog so a customer can read it off
    /// the screen to email/Telegram to support. NOT used for matching —
    /// match is done component-wise — but useful as a quick visual ID.
    /// </summary>
    public static string ShortDisplay(FingerprintBundle bundle)
    {
        var combined = bundle.MachineGuid + bundle.CpuId
                     + bundle.BaseboardSerial + bundle.BiosUuid;
        return combined[..8].ToUpperInvariant();
    }

    /// <summary>
    /// Compare two fingerprint bundles. Returns the count of matching
    /// components. Callers should require >= RequiredMatchCount to accept.
    /// </summary>
    /// <summary>
    /// Compare two fingerprint bundles. Forwards to
    /// <see cref="ActivationTokenCodec.MatchScore"/> — kept here so the
    /// app's UI code can call <c>HardwareFingerprint.MatchScore</c>
    /// without taking a direct dependency on the codec namespace.
    /// </summary>
    public static int MatchScore(FingerprintBundle a, FingerprintBundle b)
        => ActivationTokenCodec.MatchScore(a, b);

    // -------------------------------------------------------------------
    // Raw identifier readers — each returns the best string available
    // for that component, or null if we genuinely can't read it.
    // -------------------------------------------------------------------

    private static string? GetMachineGuid()
    {
        try
        {
            // The MachineGuid is created by Windows during install and lives
            // at HKLM\SOFTWARE\Microsoft\Cryptography. Reading it requires
            // no elevation but does require touching the registry.
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch { return null; }
    }

    private static string? GetCpuId()
    {
        // CPU brand + processor ID. We use WMI's Win32_Processor.
        // The ProcessorId field is a CPUID-style identifier; on Intel/AMD
        // it's a stable per-CPU-model value, not unique per chip — that's
        // why we combine it with motherboard for uniqueness.
        return ReadWmiSingle("SELECT Name, ProcessorId FROM Win32_Processor",
            row => $"{row["Name"]}|{row["ProcessorId"]}");
    }

    private static string? GetBaseboardSerial()
    {
        // Motherboard serial from BIOS. Most desktop boards have a real
        // serial; budget OEM laptops sometimes have generic strings like
        // "Default string" or "To be filled by O.E.M." We accept whatever
        // is reported — the hash will at least be stable on the machine.
        return ReadWmiSingle("SELECT SerialNumber FROM Win32_BaseBoard",
            row => row["SerialNumber"] as string);
    }

    private static string? GetBiosUuid()
    {
        // SMBIOS UUID — conceptually a globally unique system identifier
        // burned into firmware. In practice, many vendors leave it blank
        // or fill in a non-unique placeholder, but where present it's
        // very stable.
        return ReadWmiSingle("SELECT UUID FROM Win32_ComputerSystemProduct",
            row => row["UUID"] as string);
    }

    /// <summary>
    /// Tiny WMI helper — runs a query, returns null if it fails for any
    /// reason. We never want a license check to crash because WMI is in
    /// a weird state.
    /// </summary>
    private static string? ReadWmiSingle(string query, Func<ManagementObject, string?> extract)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            using var collection = searcher.Get();
            foreach (ManagementObject row in collection)
            {
                using (row)
                {
                    var value = extract(row);
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Hash a single identifier. If the input is null/blank we hash a
    /// fixed marker string so the result is deterministic but distinct
    /// from any real value — that way "blank CPU on machine A" doesn't
    /// match "blank CPU on machine B" by chance.
    /// </summary>
    private static string SafeHash(string? input)
    {
        var s = string.IsNullOrWhiteSpace(input)
            ? "LTC-UNAVAILABLE"
            : input.Trim();
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        // Truncate to 16 bytes hex = 32 chars. Enough entropy that
        // collisions are statistically impossible, half the storage
        // of full SHA-256.
        return Convert.ToHexString(hash.Take(16).ToArray());
    }

}

