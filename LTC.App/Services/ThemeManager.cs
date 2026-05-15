using System.IO;
using System.Windows;

namespace LTC.App.Services;

/// <summary>
/// Two-mode theme manager. Switches the live UI between Trader Cockpit (dark)
/// and Swiss Minimal (light) without restarting the app.
///
/// HOW THE THEME SWITCH WORKS
/// --------------------------
/// All brush references in the XAML use {DynamicResource XxxBrush} (not
/// StaticResource). DynamicResource is WPF's mechanism for runtime-changeable
/// resources: every consumer registers a listener at parse time, and when the
/// resource value at the keyed name changes, every listener re-resolves and
/// re-renders.
///
/// So all we have to do here is swap MergedDictionaries[0] (the palette slot)
/// from one ResourceDictionary to another. WPF emits a ResourcesChanged event,
/// every DynamicResource listener fires, and the entire visual tree retints
/// in one frame — no manual brush mutation, no walking the tree.
///
/// We KEEP the merged dictionary order [palette, controls]. Both palettes
/// expose identical brush keys (SolidColorBrush instances), so swapping one
/// for the other doesn't introduce or remove any keys; it just changes the
/// Color values behind the same names.
///
/// User preference persists to %LOCALAPPDATA%/LimitlessTradeCopier/theme.txt.
/// </summary>
public sealed class ThemeManager
{
    private static readonly string PrefsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LimitlessTradeCopier");
    private static readonly string PrefsFile = Path.Combine(PrefsDir, "theme.txt");

    /// <summary>The currently applied theme.</summary>
    public AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Raised after a theme swap completes; UI can react if needed.</summary>
    public event EventHandler<AppTheme>? ThemeChanged;

    /// <summary>Read the persisted theme preference, defaulting to Dark.</summary>
    public AppTheme LoadPreference()
    {
        try
        {
            if (File.Exists(PrefsFile))
            {
                var raw = File.ReadAllText(PrefsFile).Trim();
                if (Enum.TryParse<AppTheme>(raw, ignoreCase: true, out var pref))
                    return pref;
            }
        }
        catch { /* if anything goes wrong we just default */ }
        return AppTheme.Dark;
    }

    /// <summary>Apply a theme to the running application by swapping the
    /// palette ResourceDictionary at MergedDictionaries[0]. DynamicResource
    /// listeners pick up the change automatically.</summary>
    public void Apply(AppTheme theme)
    {
        if (Application.Current is null) return;

        var newPaletteUri = theme switch
        {
            AppTheme.Light => new Uri("Themes/Palette.Light.xaml", UriKind.Relative),
            _              => new Uri("Themes/Palette.Dark.xaml",  UriKind.Relative),
        };

        ResourceDictionary newPalette;
        try
        {
            newPalette = new ResourceDictionary { Source = newPaletteUri };
        }
        catch
        {
            // Palette file missing or malformed; abort silently rather than
            // leaving the app half-themed.
            return;
        }

        // The merged dictionary order from App.xaml is [palette, controls].
        // Replacing index 0 swaps the palette while keeping all the
        // ControlTemplates / Styles in MergedDictionaries[1] intact.
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count >= 1)
            merged[0] = newPalette;

        Current = theme;
        Persist(theme);
        ThemeChanged?.Invoke(this, theme);
    }

    /// <summary>Toggle between Dark and Light. Returns the new theme.</summary>
    public AppTheme Toggle()
    {
        var next = Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        Apply(next);
        return next;
    }

    private static void Persist(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(PrefsDir);
            File.WriteAllText(PrefsFile, theme.ToString());
        }
        catch { /* best-effort; theme just won't survive a restart */ }
    }
}

public enum AppTheme
{
    Dark,
    Light,
}
