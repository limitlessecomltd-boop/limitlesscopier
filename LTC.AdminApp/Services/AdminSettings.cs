using System;
using System.IO;
using System.Text.Json;

namespace LTC.AdminApp.Services;

/// <summary>
/// Stores the admin-side configuration the operator needs to talk to
/// the activation server. Lives at
/// <c>%LOCALAPPDATA%\LimitlessTradeCopierAdmin\settings.json</c>.
///
/// Two fields:
///   - <see cref="ApiUrl"/>      defaults to https://api.limitlesscopier.com
///   - <see cref="BearerToken"/> no default — operator must paste this in
///                               on first run via the Settings tab.
///
/// The bearer token is a real secret. We persist it to LocalAppData
/// rather than the registry purely for simplicity; either way an attacker
/// who has code execution on the operator's machine wins, and this file
/// is per-user so other Windows accounts can't read it.
///
/// We DO NOT use DPAPI here — the file is plaintext JSON. This is a
/// deliberate trade-off: makes it trivially easy to back up and to move
/// to another machine. If the operator's user account is compromised
/// the token is gone anyway. Future enhancement: DPAPI-wrap the token,
/// at the cost of needing a separate "export settings" UI for moving
/// between machines.
/// </summary>
public sealed class AdminSettings
{
    public const string DefaultApiUrl = "https://api.limitlesscopier.com";

    public string ApiUrl { get; set; } = DefaultApiUrl;
    public string BearerToken { get; set; } = "";

    private static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LimitlessTradeCopierAdmin");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    /// <summary>True if both URL and token are present and usable.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiUrl) &&
        !string.IsNullOrWhiteSpace(BearerToken);

    /// <summary>Load settings from disk, or return defaults if the file
    /// doesn't exist or is malformed. Never throws — corrupt settings
    /// just yield defaults so the operator can fix them in the UI.</summary>
    public static AdminSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AdminSettings();

            var json = File.ReadAllText(SettingsPath);
            var loaded = JsonSerializer.Deserialize<AdminSettings>(json);
            if (loaded is null) return new AdminSettings();

            // Sanity: blank ApiUrl falls back to default
            if (string.IsNullOrWhiteSpace(loaded.ApiUrl))
                loaded.ApiUrl = DefaultApiUrl;

            return loaded;
        }
        catch
        {
            return new AdminSettings();
        }
    }

    /// <summary>Persist current values. Creates the directory if needed.</summary>
    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(SettingsPath, json);
    }
}
