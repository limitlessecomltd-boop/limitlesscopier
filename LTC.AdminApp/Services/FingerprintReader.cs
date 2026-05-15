using System;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using LTC.Core.Licensing;

namespace LTC.AdminApp.Services;

/// <summary>
/// Reads this Windows machine's hardware fingerprint — used in the
/// "Fingerprint" tab so the operator can mint a license for THIS PC
/// (e.g. for dev testing) without having to deactivate the customer
/// app first.
///
/// Identical algorithm to LTC.App's HardwareFingerprint class. We
/// duplicate it here rather than reference LTC.App so the admin app
/// doesn't pick up customer-app dependencies (WPF assemblies for the
/// customer-side UI, the broker DLL, etc.) it doesn't need.
/// </summary>
public static class FingerprintReader
{
    public static FingerprintBundle Compute()
    {
        return new FingerprintBundle(
            MachineGuid:     SafeHash(GetMachineGuid()),
            CpuId:           SafeHash(GetCpuId()),
            BaseboardSerial: SafeHash(GetBaseboardSerial()),
            BiosUuid:        SafeHash(GetBiosUuid()));
    }

    public static string FormatFull(FingerprintBundle bundle)
        => $"{bundle.MachineGuid}-{bundle.CpuId}-{bundle.BaseboardSerial}-{bundle.BiosUuid}";

    public static string FormatShort(FingerprintBundle bundle)
        => (bundle.MachineGuid + bundle.CpuId + bundle.BaseboardSerial + bundle.BiosUuid)
            [..8].ToUpperInvariant();

    private static string? GetMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch { return null; }
    }

    private static string? GetCpuId() => ReadWmi(
        "SELECT Name, ProcessorId FROM Win32_Processor",
        row => $"{row["Name"]}|{row["ProcessorId"]}");

    private static string? GetBaseboardSerial() => ReadWmi(
        "SELECT SerialNumber FROM Win32_BaseBoard",
        row => row["SerialNumber"] as string);

    private static string? GetBiosUuid() => ReadWmi(
        "SELECT UUID FROM Win32_ComputerSystemProduct",
        row => row["UUID"] as string);

    private static string? ReadWmi(string query, Func<ManagementObject, string?> extract)
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

    private static string SafeHash(string? input)
    {
        var s = string.IsNullOrWhiteSpace(input) ? "LTC-UNAVAILABLE" : input.Trim();
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash.Take(16).ToArray());
    }
}
