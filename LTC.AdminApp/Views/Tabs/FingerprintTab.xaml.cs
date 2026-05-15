using System;
using System.Windows;
using System.Windows.Controls;
using LTC.AdminApp.Services;

namespace LTC.AdminApp.Views.Tabs;

public partial class FingerprintTab : UserControl
{
    public FingerprintTab()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var bundle = FingerprintReader.Compute();
            ShortText.Text       = FingerprintReader.FormatShort(bundle);
            FullText.Text        = FingerprintReader.FormatFull(bundle);
            MachineGuidText.Text = bundle.MachineGuid;
            CpuIdText.Text       = bundle.CpuId;
            BaseboardText.Text   = bundle.BaseboardSerial;
            BiosUuidText.Text    = bundle.BiosUuid;
        }
        catch (Exception ex)
        {
            ShortText.Text = "ERROR";
            FullText.Text  = $"Could not compute fingerprint: {ex.Message}";
        }
    }

    private void OnCopyFullClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(FullText.Text);
            if (Window.GetWindow(this) is MainWindow mw)
                mw.SetStatus("fingerprint copied to clipboard");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not copy: {ex.Message}", "Clipboard error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
