using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using LTC.App.Licensing;

namespace LTC.App.Views;

/// <summary>
/// License activation dialog. Single flow:
///
///   The customer's machine fingerprint is computed and displayed. Customer
///   emails it to support along with the purchase receipt. Support replies
///   with a .lic file generated against that fingerprint. Customer clicks
///   "Install license file..." and selects the file.
///
/// This dialog only handles NEW installations. Existing legacy short-key
/// activations (from the older LicenseService format) are honored at app
/// startup before this dialog is shown — so customers who already activated
/// with the old method don't get logged out.
///
/// On successful install we set DialogResult = true and the App startup
/// code proceeds.
/// </summary>
public partial class LicenseDialog : Window
{
    private readonly ActivationService _activation;

    // We still receive the legacy LicenseService for symmetry with the
    // App.xaml.cs startup gate (which checks legacy first, then modern,
    // then shows this dialog). We don't actually use it here — the legacy
    // paste UI was removed — but the constructor signature is stable so
    // App.xaml.cs doesn't need to change.
    private readonly LicenseService _legacy;

    /// <summary>True if the user successfully installed a .lic file.</summary>
    public bool DidActivate { get; private set; }

    /// <summary>Always false now — the legacy paste flow was removed.
    /// Kept for backwards-compat with the App.xaml.cs caller, which still
    /// branches on this.</summary>
    public bool IsLegacyMode => false;

    /// <summary>Always null — see <see cref="IsLegacyMode"/>.</summary>
    public LicenseInfo? LegacyLicense => null;

    public LicenseDialog(ActivationService activation, LicenseService legacy)
    {
        _activation = activation;
        _legacy = legacy;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Compute and display the machine fingerprint. Done in Loaded so
        // the dialog has already rendered with the "loading..." placeholder
        // — WMI calls can take 1-2 seconds on a cold machine and we don't
        // want the dialog to feel frozen.
        try
        {
            FingerprintShortText.Text = _activation.GetMachineFingerprintDisplay();
            FingerprintFullText.Text  = _activation.GetMachineFingerprintFull();
        }
        catch (Exception ex)
        {
            FingerprintShortText.Text = "ERROR";
            FingerprintFullText.Text  = $"Could not compute fingerprint: {ex.Message}";
        }
    }

    // -------------------------------------------------------------------
    // INSTALL the .lic file the operator emailed back to the customer
    // -------------------------------------------------------------------

    private void OnInstallLicFileClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title           = "Select your license file",
            Filter          = "License files (*.lic)|*.lic|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect     = false,
        };
        if (picker.ShowDialog(this) != true) return;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(picker.FileName);
        }
        catch (Exception ex)
        {
            ShowError($"Could not read the file: {ex.Message}");
            return;
        }

        if (!_activation.TryInstallLicenseFile(bytes, out var error))
        {
            ShowError(error);
            return;
        }

        ShowSuccess("License installed. Launching app…");
        DidActivate = true;

        // Brief delay so user sees the success state before we close.
        // Fire-and-forget intentionally; discard the DispatcherOperation.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            DialogResult = true;
            Close();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnCopyFingerprintClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(FingerprintFullText.Text);
            ShowSuccess("Fingerprint copied to clipboard.");
        }
        catch (Exception ex)
        {
            ShowError($"Could not copy: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------
    // QUIT
    // -------------------------------------------------------------------

    private void OnQuitClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // -------------------------------------------------------------------
    // STATUS BANNER HELPERS
    // -------------------------------------------------------------------

    private void ShowSuccess(string message)
    {
        StatusBanner.Visibility = Visibility.Visible;
        StatusBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("StatusOkBrush");
        StatusIcon.Text = "✓";
        StatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("StatusOkBrush");
        StatusTitle.Text = message;
    }

    private void ShowError(string message)
    {
        StatusBanner.Visibility = Visibility.Visible;
        StatusBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("StatusErrBrush");
        StatusIcon.Text = "×";
        StatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("StatusErrBrush");
        StatusTitle.Text = message;
    }
}
