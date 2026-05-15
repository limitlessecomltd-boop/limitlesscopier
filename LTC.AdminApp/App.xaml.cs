using System;
using System.Windows;
using System.Windows.Threading;

namespace LTC.AdminApp;

/// <summary>
/// Limitless Trade Copier — Admin App.
///
/// Operator-only tool for issuing license activation files. NEVER ship
/// this to customers; never include it in the customer installer. The
/// private signing key (keygen-private.key) must be in the app's working
/// directory or at a path configured in Settings.
///
/// Two design choices worth noting:
///
///  1. We reuse the customer app's theme dictionaries via linked files
///     in the csproj. The two apps stay visually consistent without
///     duplicating XAML.
///
///  2. We mark this app visually with an "ADMIN" badge in the top bar
///     so when both apps are open on the same machine, you instantly
///     know which window you're in.
///
/// IMPORTANT: we register a global exception handler so that startup
/// failures (XAML parse errors, theme dictionary not found, missing
/// resources etc.) surface as a MessageBox instead of disappearing
/// silently. The default WPF behavior is to crash without any feedback
/// if an exception fires before the main window is fully realized.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Catch anything that goes wrong on the UI thread.
        DispatcherUnhandledException += (s, ev) =>
        {
            MessageBox.Show(
                $"Limitless Admin failed to start:\n\n{ev.Exception.Message}\n\n{ev.Exception}",
                "Startup error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ev.Handled = true;
            Shutdown(1);
        };

        // Catch anything on background threads too (shouldn't happen at
        // startup but good defense for the future).
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            var ex = ev.ExceptionObject as Exception;
            MessageBox.Show(
                $"Limitless Admin: unhandled error\n\n{ex?.Message}\n\n{ex}",
                "Fatal error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };

        base.OnStartup(e);
    }
}
