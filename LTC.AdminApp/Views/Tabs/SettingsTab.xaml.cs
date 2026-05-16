using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LTC.AdminApp.Services;

namespace LTC.AdminApp.Views.Tabs;

/// <summary>
/// Settings tab — operator-side configuration for the activation server
/// connection. Loads from <see cref="AdminSettings"/> on entry, writes
/// back on Save. The "Test" button calls the server's /health endpoint
/// to confirm the URL is reachable.
/// </summary>
public partial class SettingsTab : UserControl
{
    public SettingsTab()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = AdminSettings.Load();
        ApiUrlBox.Text = string.IsNullOrWhiteSpace(settings.ApiUrl)
            ? AdminSettings.DefaultApiUrl
            : settings.ApiUrl;
        BearerTokenBox.Text = settings.BearerToken;
        HideStatus();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var url = (ApiUrlBox.Text ?? "").Trim();
        var token = (BearerTokenBox.Text ?? "").Trim();

        if (string.IsNullOrEmpty(url))
        {
            ShowError("Enter the activation server URL.");
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != "http" && parsed.Scheme != "https"))
        {
            ShowError("URL must start with http:// or https://");
            return;
        }
        if (string.IsNullOrEmpty(token))
        {
            ShowError("Enter the admin bearer token.");
            return;
        }

        try
        {
            var settings = new AdminSettings
            {
                ApiUrl = url,
                BearerToken = token,
            };
            settings.Save();
            ShowSuccess("Saved.");
            if (Window.GetWindow(this) is MainWindow mw)
                mw.SetStatus("settings saved");
        }
        catch (Exception ex)
        {
            ShowError($"Could not save: {ex.Message}");
        }
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        var url = (ApiUrlBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(url))
        {
            ShowError("Enter the URL first.");
            return;
        }

        HideStatus();
        var origText = ((Button)sender).Content;
        ((Button)sender).Content = "Testing…";
        ((Button)sender).IsEnabled = false;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var healthUrl = url.TrimEnd('/') + "/health";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await http.GetAsync(healthUrl, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                ShowSuccess($"Reached server at {healthUrl} (HTTP {(int)resp.StatusCode}).");
            }
            else
            {
                ShowError($"Server responded HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} at {healthUrl}.");
            }
        }
        catch (TaskCanceledException)
        {
            ShowError("Timed out reaching the server. Check the URL.");
        }
        catch (Exception ex)
        {
            ShowError($"Could not reach server: {ex.Message}");
        }
        finally
        {
            ((Button)sender).Content = origText;
            ((Button)sender).IsEnabled = true;
        }
    }

    private void ShowSuccess(string message)
    {
        StatusPanel.Visibility = Visibility.Visible;
        StatusPanel.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x0E, 0x2A, 0x22));
        StatusPanel.BorderBrush = (System.Windows.Media.Brush)FindResource("StatusOkBrush");
        StatusIcon.Text = "✓";
        StatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("StatusOkBrush");
        StatusText.Text = message;
    }

    private void ShowError(string message)
    {
        StatusPanel.Visibility = Visibility.Visible;
        StatusPanel.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x2A, 0x0E, 0x14));
        StatusPanel.BorderBrush = (System.Windows.Media.Brush)FindResource("StatusErrBrush");
        StatusIcon.Text = "✕";
        StatusIcon.Foreground = (System.Windows.Media.Brush)FindResource("StatusErrBrush");
        StatusText.Text = message;
    }

    private void HideStatus() => StatusPanel.Visibility = Visibility.Collapsed;
}
