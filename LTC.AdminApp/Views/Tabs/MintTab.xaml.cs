using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LTC.AdminApp.Services;

namespace LTC.AdminApp.Views.Tabs;

/// <summary>
/// Mint tab — sends a request to the activation server's
/// <c>/admin/keys/issue</c> endpoint, displays the server-returned
/// license key in a copy-friendly way.
///
/// The flow that disappeared with this refactor: gathering a customer
/// fingerprint, signing locally, writing a .lic file. The fingerprint is
/// now collected by the customer's <c>LTC.App</c> at activation time
/// and the server signs the binding token. The admin operator only deals
/// in opaque "license keys" — much simpler.
/// </summary>
public partial class MintTab : UserControl
{
    private AdminSettings _settings;
    private IssueKeyResponse? _lastResult;

    public MintTab()
    {
        InitializeComponent();
        _settings = AdminSettings.Load();

        // Validate on every keystroke.
        EmailBox.TextChanged += (_, _) => UpdateValidation();
        DaysBox.TextChanged  += (_, _) => UpdateValidation();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Re-load settings every time the tab becomes visible — operator
        // may have just configured them in the Settings tab.
        _settings = AdminSettings.Load();
        NotConfiguredBanner.Visibility =
            _settings.IsConfigured ? Visibility.Collapsed : Visibility.Visible;
        UpdateValidation();
    }

    private void UpdateValidation()
    {
        var (ok, reason) = ValidateInputs();
        MintButton.IsEnabled = ok;
        ValidationText.Text  = ok ? "Ready to mint." : reason;
    }

    private (bool ok, string reason) ValidateInputs()
    {
        if (!_settings.IsConfigured)
            return (false, "Configure the server connection in Settings first.");
        if (string.IsNullOrWhiteSpace(EmailBox.Text))
            return (false, "Customer email is required.");
        if (!EmailBox.Text.Contains('@'))
            return (false, "Email looks invalid (no '@').");

        var plan = SelectedPlan();
        if (plan == "Daily")
        {
            if (!int.TryParse(DaysBox.Text, out var d) || d <= 0)
                return (false, "Daily plan needs a positive number of days.");
        }

        return (true, "Ready to mint.");
    }

    private string SelectedPlan() =>
        (PlanBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Lifetime";

    private void OnPlanSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DaysBox is null) return;  // init
        DaysBox.IsEnabled = (SelectedPlan() == "Daily");
        UpdateValidation();
    }

    // -----------------------------------------------------------------
    // MINT — server call
    // -----------------------------------------------------------------
    private async void OnMintClick(object sender, RoutedEventArgs e)
    {
        HideResultAndError();

        var plan = SelectedPlan();
        int? days = null;
        if (plan == "Daily" && int.TryParse(DaysBox.Text, out var d) && d > 0)
            days = d;

        var req = new IssueKeyRequest
        {
            Email = EmailBox.Text.Trim(),
            Plan  = plan,
            Days  = days,
            Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim(),
        };

        SetBusy(true);
        try
        {
            using var client = new AdminApiClient(_settings);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var result = await client.IssueKeyAsync(req, cts.Token);

            switch (result.Kind)
            {
                case AdminApiResultKind.Success:
                    _lastResult = result.Body;
                    ShowResult(result.Body!);
                    if (Window.GetWindow(this) is MainWindow mw)
                        mw.SetStatus($"minted {result.Body!.LicenseKey}");
                    break;

                case AdminApiResultKind.ServerRejected:
                    ShowError(result.Message);
                    break;

                case AdminApiResultKind.NetworkFailure:
                    ShowError("Couldn't reach the activation server. " + result.Message);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            ShowError("Request timed out. Check your internet connection and try again.");
        }
        catch (ArgumentException ex)
        {
            // AdminApiClient throws this when settings are missing.
            ShowError(ex.Message + " Open the Settings tab to configure the connection.");
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

    private void SetBusy(bool busy)
    {
        EmailBox.IsEnabled  = !busy;
        PlanBox.IsEnabled   = !busy;
        DaysBox.IsEnabled   = !busy && (SelectedPlan() == "Daily");
        NotesBox.IsEnabled  = !busy;
        MintButton.IsEnabled = !busy && ValidateInputs().ok;
        BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // -----------------------------------------------------------------
    // RESULT / ERROR DISPLAY
    // -----------------------------------------------------------------
    private void ShowResult(IssueKeyResponse r)
    {
        ResultKeyText.Text   = r.LicenseKey;
        ResultEmailText.Text = r.Email;
        ResultPlanText.Text  = r.ExpiresAt is null
            ? $"{r.Plan}  ·  never expires"
            : $"{r.Plan}  ·  expires {r.ExpiresAt:yyyy-MM-dd}";
        ResultPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
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
    private void OnCopyKeyClick(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null) return;
        try
        {
            Clipboard.SetText(_lastResult.LicenseKey);
            if (Window.GetWindow(this) is MainWindow mw)
                mw.SetStatus("key copied to clipboard");
        }
        catch (Exception ex)
        {
            ShowError($"Could not copy: {ex.Message}");
        }
    }

    private void OnCopyEmailClick(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null) return;
        var expiresLine = _lastResult.ExpiresAt is null
            ? "Plan: " + _lastResult.Plan + " (lifetime — no expiry)"
            : $"Plan: {_lastResult.Plan}, expires {_lastResult.ExpiresAt:yyyy-MM-dd}";

        var body = $"""
            Hi,

            Thank you for purchasing Limitless Trade Copier. Your license key is:

                {_lastResult.LicenseKey}

            {expiresLine}

            To activate:
              1. Download and install Limitless Trade Copier
              2. On first launch, the License dialog will appear
              3. Paste the key above and click "Activate"

            The key will bind to your machine on first use. If you ever need
            to move to a different PC, contact support and we'll release the
            binding so you can re-activate on the new machine.

            — Limitless Trade Copier
            """;
        try
        {
            Clipboard.SetText(body);
            if (Window.GetWindow(this) is MainWindow mw)
                mw.SetStatus("email body copied to clipboard");
        }
        catch (Exception ex)
        {
            ShowError($"Could not copy: {ex.Message}");
        }
    }

    private void OnMintAnotherClick(object sender, RoutedEventArgs e)
    {
        EmailBox.Text = "";
        NotesBox.Text = "";
        HideResultAndError();
        EmailBox.Focus();
    }
}
