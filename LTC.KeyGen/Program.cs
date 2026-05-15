using System;
using System.IO;
using System.Text;
using LTC.Core.Licensing;

namespace LTC.KeyGen;

/// <summary>
/// Limitless Trade Copier — admin command-line tool.
///
/// This is the OPERATOR-ONLY tool. It carries the Ed25519 private key
/// used to sign activation tokens; if that key leaks, all existing
/// licenses must be re-issued under a new keypair. NEVER ship this
/// executable to customers, NEVER commit the private key to git, and
/// NEVER include the tool in the customer installer.
///
/// COMMANDS
///
///   mint --email X --plan Y --fingerprint F [--days N] [--out path]
///       Issue a new license bound to a specific machine. The customer
///       sends you their fingerprint (shown in the app's License dialog),
///       you pick a plan and run this. Writes a .lic file the customer
///       drops into their app folder.
///
///       --email       Customer email
///       --plan        "Lifetime" | "Daily" | "Partner" | "Dev"
///       --fingerprint The 4-part hash bundle from the customer (the long
///                     string the License dialog produces)
///       --days        Optional expiry in days from today (default: no expiry)
///       --out         Output path (default: ./license.lic)
///       --key         Path to keygen-private.key (default: ./keygen-private.key)
///
///   inspect <file>
///       Read a .lic file and print its contents (license key, email,
///       plan, dates, fingerprint hashes). Verifies the signature.
///       Useful for debugging "the customer says it won't work."
///
///   fingerprint-check <expected> <actual>
///       Compare two fingerprint strings. Reports how many components
///       match. Useful for debugging hardware-change issues.
///
/// EXAMPLES
///
///   ltc-admin mint --email alice@example.com --plan Lifetime \
///       --fingerprint "A1B2...-C3D4...-E5F6...-G7H8..." --out alice.lic
///
///   ltc-admin inspect alice.lic
///
///   ltc-admin fingerprint-check \
///       "A1B2...-C3D4...-E5F6...-G7H8..." \
///       "A1B2...-DIFF...-E5F6...-G7H8..."
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            return args[0].ToLowerInvariant() switch
            {
                "mint"               => RunMint(args[1..]),
                "inspect"            => RunInspect(args[1..]),
                "fingerprint-check"  => RunFingerprintCheck(args[1..]),
                "print-fingerprint"  => RunPrintFingerprint(args[1..]),
                "help" or "--help" or "-h" => PrintUsageAndReturn(0),
                _                    => PrintUsageAndReturn(1),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 99;
        }
    }

    // ---------------------------------------------------------------
    // MINT — issue a new activation token bound to a fingerprint
    // ---------------------------------------------------------------
    private static int RunMint(string[] args)
    {
        var opts = ParseArgs(args);
        if (!opts.ContainsKey("email") || !opts.ContainsKey("plan")
            || !opts.ContainsKey("fingerprint"))
        {
            Console.Error.WriteLine(
                "USAGE: ltc-admin mint --email X --plan Y --fingerprint F [--days N] [--out path]");
            return 1;
        }

        var email       = opts["email"];
        var plan        = opts["plan"];
        var fingerprint = opts["fingerprint"];
        var days        = opts.TryGetValue("days", out var d) ? int.Parse(d) : 0;
        var outPath     = opts.TryGetValue("out", out var o) ? o : "license.lic";
        var keyFile     = opts.TryGetValue("key", out var k) ? k : "keygen-private.key";

        if (!File.Exists(keyFile))
        {
            Console.Error.WriteLine($"ERROR: private key file not found: {keyFile}");
            Console.Error.WriteLine("Pass --key <path> if it lives somewhere else.");
            return 2;
        }

        // Parse the customer-supplied fingerprint string.
        // Format: "machineGuidHash-cpuIdHash-baseboardSerialHash-biosUuidHash"
        // Each hash is exactly 32 hex chars.
        var parts = fingerprint.Trim().Split('-');
        if (parts.Length != 4 || parts.Any(p => p.Length != 32))
        {
            Console.Error.WriteLine(
                "ERROR: fingerprint must be four 32-char hex strings joined by '-'.");
            Console.Error.WriteLine($"Got {parts.Length} parts: " +
                string.Join(", ", parts.Select(p => $"{p.Length} chars")));
            return 3;
        }
        var bundle = new FingerprintBundle(parts[0], parts[1], parts[2], parts[3]);

        // Build the activation token
        var issued    = DateTime.UtcNow;
        var expires   = days > 0 ? issued.AddDays(days) : DateTime.MaxValue;
        var heartbeat = days > 0
            ? expires                                  // expire-bound plans: heartbeat = expiry
            : issued.AddDays(30);                      // lifetime: re-check monthly

        // Generate the customer-visible license key. Format:
        // LTC-<PLAN-PREFIX>-XXXX-XXXX-XXXX (decorative; the real binding
        // is in the signed payload).
        var planTag = plan.ToUpperInvariant();
        if (planTag.Length > 4) planTag = planTag[..4];
        var keyTail = GenerateKeySegments(3, 4);  // 3 groups of 4 chars
        var licenseKey = $"LTC-{planTag}-{keyTail}";

        var token = new ActivationToken
        {
            LicenseKey      = licenseKey,
            Email           = email,
            Plan            = plan,
            IssuedUtc       = issued,
            ExpiresUtc      = expires,
            HeartbeatDueUtc = heartbeat,
            Fingerprint     = bundle,
        };

        // Sign + combine + write
        var privateKeyB64 = File.ReadAllText(keyFile).Trim();
        var privateKeyBytes = Convert.FromBase64String(privateKeyB64);
        var payload = ActivationTokenCodec.SerializePayload(token);
        var signature = ActivationTokenCodec.Sign(payload, privateKeyBytes);
        var combined = ActivationTokenCodec.CombinePayloadAndSignature(payload, signature);

        File.WriteAllBytes(outPath, combined);

        // Self-test: re-verify what we just wrote
        var ok = ActivationTokenCodec.VerifySignature(payload, signature);
        if (!ok)
        {
            Console.Error.WriteLine("ERROR: just-written signature failed verification! Public/private key pair likely mismatched.");
            return 4;
        }

        // Customer-friendly summary
        Console.WriteLine();
        Console.WriteLine("=======================================================");
        Console.WriteLine(" Limitless Trade Copier — license issued");
        Console.WriteLine("=======================================================");
        Console.WriteLine($"  Email:        {email}");
        Console.WriteLine($"  Plan:         {plan}");
        Console.WriteLine($"  License key:  {licenseKey}");
        Console.WriteLine($"  Issued:       {issued:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"  Expires:      {(expires == DateTime.MaxValue ? "never" : expires.ToString("yyyy-MM-dd"))}");
        Console.WriteLine($"  Fingerprint:  {bundle.MachineGuid[..8].ToUpperInvariant()}…");
        Console.WriteLine();
        Console.WriteLine($"WROTE:  {Path.GetFullPath(outPath)}");
        Console.WriteLine();
        Console.WriteLine("INSTRUCTIONS FOR THE CUSTOMER:");
        Console.WriteLine("  1. Save the attached file as 'activation.dat'");
        Console.WriteLine("  2. Move it to: %LOCALAPPDATA%\\LimitlessTradeCopier\\");
        Console.WriteLine("     (or use the 'Install license file' button in the app)");
        Console.WriteLine("  3. Launch Limitless Trade Copier");
        Console.WriteLine();
        return 0;
    }

    // ---------------------------------------------------------------
    // INSPECT — dump a .lic file for debugging
    // ---------------------------------------------------------------
    private static int RunInspect(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("USAGE: ltc-admin inspect <file.lic>");
            return 1;
        }
        var path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"ERROR: file not found: {path}");
            return 2;
        }

        var bytes = File.ReadAllBytes(path);
        // Try parsing both as raw signed token (what mint produces) AND
        // as DPAPI-protected (what the app stores). DPAPI files only
        // decrypt on the same Windows user that wrote them, so we'll
        // usually only succeed on the raw format here.
        ActivationToken token;
        byte[] signature;
        try
        {
            token = ActivationTokenCodec.Deserialize(bytes, out signature);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: could not parse file: {ex.Message}");
            Console.Error.WriteLine("Note: if this is activation.dat from a customer PC, it's DPAPI-encrypted and only readable on their machine.");
            return 3;
        }

        var payload = ActivationTokenCodec.SerializePayload(token);
        var sigOk = ActivationTokenCodec.VerifySignature(payload, signature);

        Console.WriteLine();
        Console.WriteLine("=======================================================");
        Console.WriteLine(" License inspection");
        Console.WriteLine("=======================================================");
        Console.WriteLine($"  Signature:   {(sigOk ? "✓ valid" : "✗ INVALID")}");
        Console.WriteLine($"  License key: {token.LicenseKey}");
        Console.WriteLine($"  Email:       {token.Email}");
        Console.WriteLine($"  Plan:        {token.Plan}");
        Console.WriteLine($"  Issued:      {token.IssuedUtc:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"  Expires:     {(token.ExpiresUtc == DateTime.MaxValue ? "never" : token.ExpiresUtc.ToString("yyyy-MM-dd HH:mm UTC"))}");
        Console.WriteLine($"  Heartbeat:   {token.HeartbeatDueUtc:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"  Fingerprint:");
        Console.WriteLine($"    machine:   {token.Fingerprint.MachineGuid}");
        Console.WriteLine($"    cpu:       {token.Fingerprint.CpuId}");
        Console.WriteLine($"    baseboard: {token.Fingerprint.BaseboardSerial}");
        Console.WriteLine($"    bios:      {token.Fingerprint.BiosUuid}");
        Console.WriteLine();
        return sigOk ? 0 : 5;
    }

    // ---------------------------------------------------------------
    // FINGERPRINT-CHECK — compare two fingerprint strings
    // ---------------------------------------------------------------
    private static int RunFingerprintCheck(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("USAGE: ltc-admin fingerprint-check <expected> <actual>");
            return 1;
        }

        var a = args[0].Split('-');
        var b = args[1].Split('-');
        if (a.Length != 4 || b.Length != 4)
        {
            Console.Error.WriteLine("ERROR: both fingerprints must be 4 dash-separated parts.");
            return 2;
        }

        var bundleA = new FingerprintBundle(a[0], a[1], a[2], a[3]);
        var bundleB = new FingerprintBundle(b[0], b[1], b[2], b[3]);
        var score = ActivationTokenCodec.MatchScore(bundleA, bundleB);

        Console.WriteLine();
        Console.WriteLine($"Match score: {score} / 4");
        Console.WriteLine($"  Machine GUID:     {(bundleA.MachineGuid == bundleB.MachineGuid ? "match" : "differ")}");
        Console.WriteLine($"  CPU ID:           {(bundleA.CpuId == bundleB.CpuId ? "match" : "differ")}");
        Console.WriteLine($"  Baseboard serial: {(bundleA.BaseboardSerial == bundleB.BaseboardSerial ? "match" : "differ")}");
        Console.WriteLine($"  BIOS UUID:        {(bundleA.BiosUuid == bundleB.BiosUuid ? "match" : "differ")}");
        Console.WriteLine();
        Console.WriteLine(score >= 3
            ? "✓ Considered same machine (needs ≥3 of 4)"
            : "✗ Considered different machines (needs ≥3 of 4)");
        return score >= 3 ? 0 : 6;
    }

    // ---------------------------------------------------------------
    // PRINT-FINGERPRINT — compute and display this machine's fingerprint
    // ---------------------------------------------------------------
    // Useful when you want to mint a license for the SAME machine the
    // admin tool is running on (e.g. dev testing on your own laptop)
    // without having to deactivate the running app to surface the
    // fingerprint in the UI.
    private static int RunPrintFingerprint(string[] args)
    {
        var machineGuid     = SafeHash(GetMachineGuid());
        var cpuId           = SafeHash(GetCpuId());
        var baseboardSerial = SafeHash(GetBaseboardSerial());
        var biosUuid        = SafeHash(GetBiosUuid());
        var full = $"{machineGuid}-{cpuId}-{baseboardSerial}-{biosUuid}";

        Console.WriteLine();
        Console.WriteLine("This machine's fingerprint:");
        Console.WriteLine();
        Console.WriteLine($"  Short:  {(machineGuid + cpuId + baseboardSerial + biosUuid)[..8].ToUpperInvariant()}");
        Console.WriteLine();
        Console.WriteLine("  Full:");
        Console.WriteLine($"  {full}");
        Console.WriteLine();
        Console.WriteLine("Pass to mint with:");
        Console.WriteLine($"  ltc-admin mint --email X --plan Y --fingerprint \"{full}\" --out out.lic");
        Console.WriteLine();
        return 0;
    }

    // ----- WMI helpers (Windows-only, inlined here so this CLI doesn't
    // need a reference to the LTC.App project that also has this code) -----

    private static string? GetMachineGuid()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch { return null; }
    }

    private static string? GetCpuId() => ReadWmiSingle(
        "SELECT Name, ProcessorId FROM Win32_Processor",
        row => $"{row["Name"]}|{row["ProcessorId"]}");

    private static string? GetBaseboardSerial() => ReadWmiSingle(
        "SELECT SerialNumber FROM Win32_BaseBoard",
        row => row["SerialNumber"] as string);

    private static string? GetBiosUuid() => ReadWmiSingle(
        "SELECT UUID FROM Win32_ComputerSystemProduct",
        row => row["UUID"] as string);

    private static string? ReadWmiSingle(string query,
        Func<System.Management.ManagementObject, string?> extract)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(query);
            using var collection = searcher.Get();
            foreach (System.Management.ManagementObject row in collection)
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

    private static string SafeHash(string? input)
    {
        var s = string.IsNullOrWhiteSpace(input) ? "LTC-UNAVAILABLE" : input.Trim();
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash.Take(16).ToArray());
    }

    // ---------------------------------------------------------------
    // HELPERS
    // ---------------------------------------------------------------

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) continue;
            var name = a[2..];
            string value;
            if (name.Contains('='))
            {
                var split = name.Split('=', 2);
                name = split[0];
                value = split[1];
            }
            else
            {
                if (i + 1 >= args.Length) continue;
                value = args[++i];
            }
            result[name] = value;
        }
        return result;
    }

    /// <summary>Build the visible part of the license key — random
    /// alphanumeric groups separated by dashes. Decorative; the real
    /// binding is in the signed payload.</summary>
    private static string GenerateKeySegments(int groups, int charsPerGroup)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I, O, 0, 1
        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
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

    private static int PrintUsageAndReturn(int code)
    {
        PrintUsage();
        return code;
    }

    private static void PrintUsage()
    {
        Console.WriteLine();
        Console.WriteLine("Limitless Trade Copier — admin tool");
        Console.WriteLine();
        Console.WriteLine("USAGE:");
        Console.WriteLine("  ltc-admin print-fingerprint");
        Console.WriteLine("  ltc-admin mint --email X --plan Y --fingerprint F [--days N] [--out path]");
        Console.WriteLine("  ltc-admin inspect <file>");
        Console.WriteLine("  ltc-admin fingerprint-check <expected> <actual>");
        Console.WriteLine();
        Console.WriteLine("Plans:    Lifetime | Daily | Partner | Dev");
        Console.WriteLine("Days:     omit for never-expires (lifetime/partner/dev)");
        Console.WriteLine();
        Console.WriteLine("Quick local test:");
        Console.WriteLine("  ltc-admin print-fingerprint   # copy the Full output below");
        Console.WriteLine("  ltc-admin mint --email me@me.com --plan Lifetime --fingerprint \"<paste>\" --out test.lic");
        Console.WriteLine();
    }
}
