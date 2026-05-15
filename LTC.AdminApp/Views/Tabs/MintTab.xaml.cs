using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTC.AdminApp.Services;
using Microsoft.Win32;

namespace LTC.AdminApp.Views.Tabs;

/// <summary>
/// Mint tab — form for issuing a license. Validates inputs live (Mint
/// button enables only when all fields are reasonable), delegates the
/// actual signing to <see cref="LicenseMinter"/>, and shows either a
/// green success panel or red error panel below the form.
/// </summary>
public partial class MintTab : UserControl
{
    private readonly LicenseMinter _minter = new();
    private MintResult? _lastResult;
    private string? _lastWritePath;

    public MintTab()
    {
        InitializeComponent();

        // Validate on every keystroke in any of the relevant fields.
        EmailBox.TextChanged       += (_, _) => UpdateValidation();
        FingerprintBox.TextChanged += (_, _) => UpdateValidation();
        OutputBox.TextChanged      += (_, _) => UpdateValidation();
        DaysBox.TextChanged        += (_, _) => UpdateValidation();

        UpdateValidation();
    }

    // -----------------------------------------------------------------
    // VALIDATION
    // -----------------------------------------------------------------

    private void UpdateValidation()
    {
        var (ok, reason) = ValidateInputs();
        MintButton.IsEnabled  = ok;
        ValidationText.Text   = ok ? "Ready to mint." : reason;
    }

    private (bool ok, string reason) ValidateInputs()
    {
        if (string.IsNullOrWhiteSpace(EmailBox.Text))
            return (false, "Customer email is required.");
        if (!EmailBox.Text.Contains('@'))
            return (false, "Email looks invalid (no '@').");

        var fp = (FingerprintBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(fp))
            return (false, "Paste the customer's fingerprint.");
        var parts = fp.Split('-');
        if (parts.Length != 4 || parts.Any(p => p.Length != 32))
            return (false, $"Fingerprint format wrong (need 4 chunks of 32 hex chars; got {parts.Length}).");

        if (string.IsNullOrWhiteSpace(OutputBox.Text))
            return (false, "Output filename is required.");

        var selectedPlan = (PlanBox.SelectedItem as ComboBoxItem)?.Content as string ?? "";
        if (selectedPlan == "Daily")
        {
            if (!int.TryParse(DaysBox.Text, out var d) || d <= 0)
                return (false, "Daily plan needs a positive number of days.");
        }

        return (true, "Ready to mint.");
    }

    private void OnPlanSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Days field only matters for the Daily plan.
        if (DaysBox is null) return;  // happens during init
        var plan = (PlanBox.SelectedItem as ComboBoxItem)?.Content as string ?? "";
        DaysBox.IsEnabled = (plan == "Daily");
        UpdateValidation();
    }

    // -----------------------------------------------------------------
    // BROWSE OUTPUT PATH
    // -----------------------------------------------------------------

    private void OnBrowseOutputClick(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Filter = "License files (*.lic)|*.lic|All files (*.*)|*.*",
            DefaultExt = "lic",
            FileName = Path.GetFileName(OutputBox.Text),
            InitialDirectory = ResolveInitialDir(),
        };
        if (sfd.ShowDialog() == true)
        {
            OutputBox.Text = sfd.FileName;
        }
    }

    private string ResolveInitialDir()
    {
        try
        {
            var path = OutputBox.Text;
            if (string.IsNullOrWhiteSpace(path)) return Environment.CurrentDirectory;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            return Directory.Exists(dir) ? dir! : Environment.CurrentDirectory;
        }
        catch { return Environment.CurrentDirectory; }
    }

    // -----------------------------------------------------------------
    // MINT
    // -----------------------------------------------------------------

    private void OnMintClick(object sender, RoutedEventArgs e)
    {
        HideResultAndError();

        var plan = (PlanBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Lifetime";
        var days = (plan == "Daily" && int.TryParse(DaysBox.Text, out var d)) ? d : 0;

        var request = new MintRequest(
            Email:           EmailBox.Text.Trim(),
            Plan:            plan,
            Fingerprint:     FingerprintBox.Text.Trim(),
            Days:            days,
            PrivateKeyPath:  ""); // empty → uses default "keygen-private.key" path

        try
        {
            var result = _minter.Mint(request);

            // Resolve output path (relative paths → working dir)
            var outPath = Path.GetFullPath(OutputBox.Text);
            _minter.WriteToFile(result, outPath);

            _lastResult = result;
            _lastWritePath = outPath;
            ShowResult(result, outPath);
        }
        catch (MintException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            ShowError($"Unexpected error: {ex.Message}");
        }
    }

    private void ShowResult(MintResult result, string fullPath)
    {
        ResultKeyText.Text   = result.LicenseKey;
        ResultEmailText.Text = result.Token.Email;
        ResultPlanText.Text  = result.Token.Plan
                             + (result.Token.ExpiresUtc == DateTime.MaxValue
                                ? "  ·  never expires"
                                : $"  ·  expires {result.Token.ExpiresUtc:yyyy-MM-dd}");
        ResultPathText.Text  = fullPath;

        ResultPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;

        // Update window status if we're hosted in MainWindow
        if (Window.GetWindow(this) is MainWindow mw)
            mw.SetStatus($"minted {result.LicenseKey}");
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        ResultPanel.Visibility = Visibility.Collapsed;

        if (Window.GetWindow(this) is MainWindow mw)
            mw.SetStatus("mint failed");
    }

    private void HideResultAndError()
    {
        ResultPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility  = Visibility.Collapsed;
    }

    // -----------------------------------------------------------------
    // POST-MINT ACTIONS
    // -----------------------------------------------------------------

    private void OnRevealInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastWritePath)) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{_lastWritePath}\"");
        }
        catch (Exception ex)
        {
            ShowError($"Could not open Explorer: {ex.Message}");
        }
    }

    private void OnCopySummaryClick(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null || _lastWritePath is null) return;

        var summary = $"""
            Limitless Trade Copier — License Issued

            Customer:    {_lastResult.Token.Email}
            License key: {_lastResult.LicenseKey}
            Plan:        {_lastResult.Token.Plan}
            Issued:      {_lastResult.Token.IssuedUtc:yyyy-MM-dd HH:mm} UTC
            Expires:     {(_lastResult.Token.ExpiresUtc == DateTime.MaxValue
                            ? "never" : _lastResult.Token.ExpiresUtc.ToString("yyyy-MM-dd"))}
            File:        {Path.GetFileName(_lastWritePath)}

            Customer instructions:
              1. Save the attached .lic file
              2. In the app, click "Install file..." in the License dialog
                 (or copy it to %LOCALAPPDATA%\LimitlessTradeCopier\activation.dat)
              3. Restart the app
            """;
        try
        {
            Clipboard.SetText(summary);
            if (Window.GetWindow(this) is MainWindow mw)
                mw.SetStatus("summary copied to clipboard");
        }
        catch (Exception ex)
        {
            ShowError($"Could not copy: {ex.Message}");
        }
    }

    private void OnMintAnotherClick(object sender, RoutedEventArgs e)
    {
        // Clear the form, hide the result, keep the operator in the Mint tab
        EmailBox.Text       = "";
        FingerprintBox.Text = "";
        // Plan/Days/Output kept — usually batches share these
        HideResultAndError();
        EmailBox.Focus();
    }
}
