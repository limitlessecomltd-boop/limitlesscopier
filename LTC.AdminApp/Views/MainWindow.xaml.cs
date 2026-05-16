using System;
using System.Windows;
using LTC.AdminApp.Services;

namespace LTC.AdminApp.Views;

/// <summary>
/// Admin app shell. Hosts four tab content controls (Mint, Fingerprint,
/// Settings, About) and swaps their visibility based on the sidebar
/// radio buttons.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        TabMintHost.Content        = new Tabs.MintTab();
        TabFingerprintHost.Content = new Tabs.FingerprintTab();
        TabSettingsHost.Content    = new Tabs.SettingsTab();
        TabAboutHost.Content       = new Tabs.AboutTab();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var bundle = FingerprintReader.Compute();
            OperatorMachineText.Text = FingerprintReader.FormatShort(bundle);
        }
        catch
        {
            OperatorMachineText.Text = "unknown";
        }

        // If settings aren't configured yet, jump straight to the Settings
        // tab so the operator knows what to do on first run.
        var settings = AdminSettings.Load();
        if (!settings.IsConfigured)
        {
            NavSettings.IsChecked = true;
            SetStatus("configure the server connection to enable minting");
        }
    }

    // -------- TAB SWITCHING --------
    private void OnNavMintChecked(object sender, RoutedEventArgs e) =>
        ShowOnly(TabMintHost, "mint license");

    private void OnNavFingerprintChecked(object sender, RoutedEventArgs e) =>
        ShowOnly(TabFingerprintHost, "this machine's fingerprint");

    private void OnNavSettingsChecked(object sender, RoutedEventArgs e) =>
        ShowOnly(TabSettingsHost, "settings");

    private void OnNavAboutChecked(object sender, RoutedEventArgs e) =>
        ShowOnly(TabAboutHost, "about");

    private void ShowOnly(System.Windows.Controls.ContentControl? visibleTab, string statusLabel)
    {
        // Defensive — early IsChecked events fire before XAML is finished.
        if (TabMintHost is null || TabFingerprintHost is null ||
            TabSettingsHost is null || TabAboutHost is null)
            return;
        if (visibleTab is null) return;

        TabMintHost.Visibility        = Visibility.Collapsed;
        TabFingerprintHost.Visibility = Visibility.Collapsed;
        TabSettingsHost.Visibility    = Visibility.Collapsed;
        TabAboutHost.Visibility       = Visibility.Collapsed;

        visibleTab.Visibility = Visibility.Visible;
        if (StatusText is not null) StatusText.Text = statusLabel;
    }

    /// <summary>Called by child tabs to update the bottom status text.</summary>
    public void SetStatus(string text)
    {
        if (StatusText is not null) StatusText.Text = text;
    }
}
