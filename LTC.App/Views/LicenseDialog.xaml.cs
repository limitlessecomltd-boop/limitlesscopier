using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using LTC.App.Licensing;

namespace LTC.App.Views;

/// <summary>
/// License activation dialog. Two paths to activate, both produce the
/// same on-disk artifact:
///
///   PRIMARY  — paste a license key. Calls the activation server which
///              signs a token bound to this machine. The button is
///              labelled "Activate" and is what 99% of customers use.
///
///   FALLBACK — install a pre-signed .lic file. Hidden in an Expander
///              for support cases (offline machines, refunds, etc).
///
/// On successful activation (either path), DidActivate = true,
/// DialogResult = true, and App startup proceeds to the main window.
/// </summary>
public partial class LicenseDialog : Window
{
    private readonly ActivationService _activation;

    // We still receive the legacy LicenseService for symmetry with the
    // App.xaml.cs startup gate (which checks legacy first, then modern,
    // then shows this dialog). The legacy paste UI was removed — that path
    // is exercised only via App.xaml.cs reading existing license.dat files.
    private readonly LicenseService _legacy;

    /// <summary>True if the user successfully activated (any path).</summary>
    public bool DidActivate { get; private set; }

    /// <summary>Always false — the in-dialog legacy paste flow was removed.
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

        // Auto-focus the license key entry so customers can paste + press
        // Enter immediately. This is the primary path; the fingerprint
        // panel above is just for reference / support.
        LicenseKeyBox.Focus();
    }

    // -------------------------------------------------------------------
    // PRIMARY: paste license key → call server → activate
    // -------------------------------------------------------------------
    private void OnLicenseKeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnActivateOnlineClick(sender, e);
        }
    }

    private async void OnActivateOnlineClick(object sender, RoutedEventArgs e)
    {
        var key = LicenseKeyBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(key))
        {
            ShowError("Enter a license key.");
            LicenseKeyBox.Focus();
            return;
        }

        // UI feedback during the network call. The HTTP timeout is 10s so
        // worst case the spinner shows for about that long.
        SetBusy(true);

        try
        {
            // 30-second outer timeout — a little longer than the HTTP
            // client's own 10s timeout so we don't double-time-out.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await _activation.ActivateOnlineAsync(key, cts.Token);

            switch (result.Kind)
            {
                case OnlineActivationKind.Success:
                    ShowSuccess("Activated. Launching app…");
                    DidActivate = true;
                    // Brief delay so the user sees the success state before close
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DialogResult = true;
                        Close();
                    }), System.Windows.Threading.DispatcherPriority.Background);
                    break;

                case OnlineActivationKind.ServerRejected:
                    ShowError(result.Message);
                    break;

                case OnlineActivationKind.NetworkFailure:
                    ShowError("Couldn't reach the activation server. " + result.Message
                        + "\n\nIf the problem persists, contact support — they can issue a license file you can install offline (see the section below).");
                    break;

                case OnlineActivationKind.LocalError:
                default:
                    ShowError(result.Message);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            ShowError("The activation request timed out. Check your internet connection and try again.");
        }
        catch (Exception ex)
        {
            ShowError($"Unexpected error: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Disable input + show spinner during async activation.</summary>
    private void SetBusy(bool busy)
    {
        LicenseKeyBox.IsEnabled = !busy;
        ActivateBtn.IsEnabled = !busy;
        BusySpinnerText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // -------------------------------------------------------------------
    // FALLBACK: install a .lic file the operator emailed back to the customer
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
