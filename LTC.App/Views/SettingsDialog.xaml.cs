using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LTC.App.Services;
using LTC.App.ViewModels;
using LTC.Persistence;

namespace LTC.App.Views;

public partial class SettingsDialog : Window
{
    public bool ResetRequested { get; private set; }

    /// <summary>Suppresses theme apply during constructor initialization. Without
    /// this guard, setting SelectedIndex below to mirror the current theme
    /// would fire SelectionChanged and try to "apply" the same theme again.</summary>
    private bool _initializing = true;

    public SettingsDialog(MainViewModel vm)
    {
        InitializeComponent();

        // App version: take it from the executing assembly's File / Informational version.
        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetName().Version?.ToString() ?? "1.0.0";
        AppVersionText.Text = $"Limitless Trade Copier · v{version}";

        var dbPath = LtcDatabase.DefaultPath();
        DbPathText.Text = dbPath;

        var logsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LimitlessTradeCopier", "logs");
        LogsPathText.Text = logsPath;

        var accountCount = vm.Masters.Count + vm.Slaves.Count;
        AccountsCountText.Text = $"{accountCount}";
        LinksCountText.Text = $"{vm.Links.Count}";

        // License section: show EITHER the modern activation token or the
        // legacy LicenseInfo, whichever is currently active. Either one is
        // populated by the App startup gate; not both.
        var lic = App.ActiveLicense;
        var tok = App.ActiveToken;
        if (tok is not null)
        {
            LicEmailText.Text  = tok.Email;
            LicPlanText.Text   = tok.Plan.ToUpperInvariant();
            LicIssuedText.Text = tok.IssuedUtc.ToString("yyyy-MM-dd");
        }
        else if (lic is not null)
        {
            LicEmailText.Text  = lic.Email;
            LicPlanText.Text   = lic.Plan.ToUpperInvariant() + "  (legacy)";
            LicIssuedText.Text = lic.IssuedUtc.ToString("yyyy-MM-dd");
        }
        else
        {
            // Shouldn't happen post-startup, but placeholder if it does
            LicEmailText.Text  = "—";
            LicPlanText.Text   = "—";
            LicIssuedText.Text = "—";
        }

        // Mirror the current theme into the ComboBox so it shows the user
        // what's active when they open Settings.
        ThemeBox.SelectedIndex = App.Theme.Current == AppTheme.Light ? 1 : 0;
        _initializing = false;
    }

    /// <summary>
    /// Wipe the saved license/activation and exit the app. Next launch
    /// will show the LicenseDialog. Used when the customer is moving to
    /// a new machine, selling/returning their PC, or wants to re-enter
    /// a different key.
    /// </summary>
    private void OnDeactivateLicenseClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Remove the saved license from this device?\n\n" +
            "The app will close. On next launch you'll be asked to activate again. " +
            "If you have a modern license file (.lic) you can re-install it, " +
            "or contact support to migrate your license to a new machine.",
            "Deactivate license",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        // Wipe both the modern activation file AND the legacy file. We
        // don't know which path the user is on (and they might have both
        // from a migration), so clear them both to be sure.
        App.Activation.Deactivate();
        App.License.Deactivate();
        Application.Current.Shutdown(0);
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (ThemeBox.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string tag) return;
        if (!Enum.TryParse<AppTheme>(tag, out var theme)) return;
        if (theme == App.Theme.Current) return;

        // Route the theme change through the same transition overlay used
        // by the top-bar button. The owner is whoever owns this dialog —
        // typically MainWindow — so the overlay centers over the app.
        if (Owner is MainWindow main)
        {
            main.ShowThemeTransition(theme);
        }
        else
        {
            // Fallback: apply directly if there's no MainWindow owner
            // (shouldn't happen in normal flow, but is defensive).
            App.Theme.Apply(theme);
        }
    }

    private void OnOpenLogsFolderClick(object sender, RoutedEventArgs e) =>
        OpenFolder(LogsPathText.Text);

    private void OnOpenDbFolderClick(object sender, RoutedEventArgs e)
    {
        // Open the *folder* containing the .db file, not the file itself.
        var dir = Path.GetDirectoryName(DbPathText.Text) ?? DbPathText.Text;
        OpenFolder(dir);
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                MessageBox.Show(this, $"Folder doesn't exist yet:\n{path}",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open folder:\n{ex.Message}",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        var first = MessageBox.Show(this,
            "This will permanently delete every account, copy link, and saved setting on this machine. " +
            "The app will close immediately afterwards. Continue?",
            "Delete all data and reset",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (first != MessageBoxResult.OK) return;

        var second = MessageBox.Show(this,
            "Are you absolutely sure? This cannot be undone.",
            "Final confirmation",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (second != MessageBoxResult.OK) return;

        ResetRequested = true;
        DialogResult = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
