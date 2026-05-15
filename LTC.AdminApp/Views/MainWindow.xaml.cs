using System;
using System.Windows;
using LTC.AdminApp.Services;

namespace LTC.AdminApp.Views;

/// <summary>
/// Admin app shell. Hosts three tab content controls (Mint, Fingerprint,
/// About) and swaps their visibility based on the sidebar radio buttons.
///
/// Mirrors the pattern used by the customer app's MainShell — every tab
/// is a self-contained UserControl; the shell just toggles visibility.
/// Keeps each tab isolated and easy to evolve independently.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Pre-fill the three tab hosts. They're created up front so the
        // first tab switch feels instant — no UserControl construction
        // delay on click.
        TabMintHost.Content        = new Tabs.MintTab();
        TabFingerprintHost.Content = new Tabs.FingerprintTab();
        TabAboutHost.Content       = new Tabs.AboutTab();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Show this machine's short fingerprint up in the top bar — it's
        // a quick "which PC am I on?" sanity check for the operator.
        try
        {
            var bundle = FingerprintReader.Compute();
            OperatorMachineText.Text = FingerprintReader.FormatShort(bundle);
        }
        catch
        {
            OperatorMachineText.Text = "unknown";
        }
    }

    // -------- TAB SWITCHING --------
    //
    // These handlers fire when RadioButton.IsChecked changes — including
    // during XAML parsing when IsChecked="True" on NavMint sets it true
    // for the first time. At that moment InitializeComponent hasn't
    // finished, so TabMintHost / TabFingerprintHost / TabAboutHost are
    // still null. We bail out early in that case; the real first paint
    // happens after Loaded fires.

    private void OnNavMintChecked(object sender, RoutedEventArgs e) =>
        ShowOnly(TabMintHost, "mint license");

    private void OnNavFingerprintChecked(object sender, RoutedEventArgs e) =>
        ShowOnly(TabFingerprintHost, "this machine's fingerprint");

    private void OnNavAboutChecked(object sender, RoutedEventArgs e) =>
        ShowOnly(TabAboutHost, "about");

    private void ShowOnly(System.Windows.Controls.ContentControl? visibleTab, string statusLabel)
    {
        // Defensive — early IsChecked events fire before XAML is finished.
        if (TabMintHost is null || TabFingerprintHost is null || TabAboutHost is null)
            return;
        if (visibleTab is null) return;

        TabMintHost.Visibility        = Visibility.Collapsed;
        TabFingerprintHost.Visibility = Visibility.Collapsed;
        TabAboutHost.Visibility       = Visibility.Collapsed;
        visibleTab.Visibility         = Visibility.Visible;
        if (StatusText is not null) StatusText.Text = statusLabel;
    }

    /// <summary>Called by child tabs that want to update the status text.</summary>
    public void SetStatus(string text)
    {
        if (StatusText is not null) StatusText.Text = text;
    }
}
