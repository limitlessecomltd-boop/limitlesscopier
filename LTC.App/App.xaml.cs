using System.IO;
using System.Windows;
using LTC.App.Licensing;
using LTC.App.Services;
using LTC.App.ViewModels;
using LTC.App.Views;
using LTC.Core;
using LTC.Persistence;
using LTC.Persistence.Encryption;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace LTC.App;

public partial class App : Application
{
    private LtcDatabase? _db;
    private CopierEngine? _engine;
    private MainViewModel? _viewModel;
    private ILoggerFactory? _loggerFactory;

    /// <summary>App-wide theme manager. Public so views can call Toggle()
    /// from button click handlers.</summary>
    public static ThemeManager Theme { get; } = new ThemeManager();

    /// <summary>Legacy license service — accepts old LTC-… signed keys.
    /// Kept for backward compatibility with customers who activated under
    /// the simple licensing system before the activation infrastructure
    /// was added. New activations go through <see cref="Activation"/>.</summary>
    public static LicenseService License { get; } = new LicenseService();

    /// <summary>Modern activation service — hardware-bound .lic files.
    /// Public so Settings can display the activation status and offer a
    /// Deactivate action.</summary>
    public static ActivationService Activation { get; } = new ActivationService();

    /// <summary>The legacy license that's currently active for this session.
    /// Null if the modern activation path was used instead.</summary>
    public static LicenseInfo? ActiveLicense { get; private set; }

    /// <summary>The modern activation token (if any) that gated this session.
    /// Null if the legacy path was used.</summary>
    public static LTC.Core.Licensing.ActivationToken? ActiveToken { get; private set; }

    /// <summary>
    /// Static constructor — runs before any instance of App is created, before
    /// OnStartup, and before any code path that might try to JIT a method
    /// referencing the broker DLL. This is the earliest reliable point we can
    /// hook AssemblyResolve.
    /// </summary>
    static App()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveMt5Assembly;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Resolver was registered in the static constructor above.

        // Apply persisted theme preference before any window opens. The default
        // dark palette is already loaded via App.xaml's MergedDictionaries; if
        // the user's preference is Light we swap it in now.
        var preferred = Theme.LoadPreference();
        if (preferred != AppTheme.Dark)
            Theme.Apply(preferred);
        else
            Theme.Apply(AppTheme.Dark); // explicitly set to keep Current in sync

        // -----------------------------------------------------------
        // LICENSE GATE  (hybrid: modern activation + legacy keys)
        // -----------------------------------------------------------
        // Order of precedence:
        //   1. Modern activation.dat (Active state)  → proceed silently
        //   2. Legacy license.dat (signed key, no hardware bind) → proceed silently
        //   3. Modern activation.dat present but HardwareMismatch/Expired → show dialog
        //   4. Neither found → show dialog
        //
        // The dialog lets the user EITHER install a .lic file (modern path)
        // OR paste a legacy key. Both successful paths set DialogResult=true.
        // If the user quits the dialog we exit silently without launching.

        var activationStatus = Activation.GetStatus();
        if (activationStatus.State == ActivationState.Active)
        {
            // Modern path: pre-activated and healthy.
            ActiveToken = activationStatus.Token;
        }
        else
        {
            // No valid modern activation. Fall back to legacy license check.
            ActiveLicense = License.TryLoadActiveLicense();
            if (ActiveLicense is null)
            {
                // Neither modern nor legacy works — show the activation dialog.
                var dlg = new LicenseDialog(Activation, License);
                var ok = dlg.ShowDialog() == true;
                if (!ok || !dlg.DidActivate)
                {
                    // User clicked Quit or closed without activating.
                    Shutdown(0);
                    return;
                }

                if (dlg.IsLegacyMode)
                {
                    ActiveLicense = dlg.LegacyLicense;
                }
                else
                {
                    // Modern path: dialog wrote activation.dat. Re-read to
                    // get the canonical token (status check picks up the
                    // newly-installed file).
                    var newStatus = Activation.GetStatus();
                    ActiveToken = newStatus.Token;
                }
            }
        }
        // From here on, exactly one of ActiveLicense / ActiveToken is set.

        try
        {
            // Show the boot splash IMMEDIATELY so the user sees motion right away.
            // The splash window auto-closes itself after ~3.6s via a DispatcherTimer
            // in its code-behind. While it's animating we kick off the heavy init
            // (logger, DB, engine, view model) on a background dispatcher pri so
            // the splash storyboard isn't starved.
            var splash = new SplashWindow();
            splash.Show();

            // Defer heavy init until after the splash has rendered its first frame,
            // otherwise the work blocks the UI thread and the animation jitters or
            // doesn't appear at all on slow machines.
            Dispatcher.BeginInvoke(new Action(() => InitializeAndShowMain(splash)),
                                   System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            // Last resort: pop a message so the user sees what happened.
            // Log will be in the log file too.
            try { Log.Fatal(ex, "Fatal error during startup"); } catch { }
            MessageBox.Show(
                $"Limitless Trade Copier failed to start:\n\n{ex.Message}\n\n{ex}",
                "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Heavy startup work — runs after the splash has rendered. Builds logger,
    /// DB, engine, and view model, then waits for the splash to self-close
    /// before opening MainWindow. If init outpaces the splash animation
    /// (typical) the user still sees the full 3.6s reveal; if init is slower
    /// than the splash (cold start, slow disk) the main window opens as soon
    /// as it's ready and we close the splash early to avoid a stall.
    /// </summary>
    private void InitializeAndShowMain(SplashWindow splash)
    {
        try
        {
            // Logging — file rolling per day
            var logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LimitlessTradeCopier", "logs");
            Directory.CreateDirectory(logsDir);
            var logPath = Path.Combine(logsDir, "ltc-.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day,
                              outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
            _loggerFactory = new SerilogLoggerFactory(Log.Logger);
            var logger = _loggerFactory.CreateLogger("LTC");
            logger.LogInformation("App starting up");

            // Persistence
            ICredentialProtector protector = OperatingSystem.IsWindows()
                ? new DpapiCredentialProtector()
                : new NoOpCredentialProtector();
            _db = new LtcDatabase(LtcDatabase.DefaultPath(), protector);
            var persistence = new PersistedConfig(_db);
            logger.LogInformation("Database: {Path}", LtcDatabase.DefaultPath());

            // Engine
            _engine = new CopierEngine(logger);

            // View model — wires the engine + DB into something the window can bind to
            _viewModel = new MainViewModel(_engine, persistence, logger);
            _viewModel.LoadFromPersistence();

            // Build but DON'T show the shell yet. We want the splash to finish
            // its animation first, otherwise the main window pops in mid-reveal
            // and the splash effect is lost.
            // NOTE: We now launch MainShell (the new tab-based UI) instead of
            // the original MainWindow. MainWindow.xaml remains in the codebase
            // for one release as a safety net but is no longer started.
            var window = new MainShell { DataContext = _viewModel };

            // When the splash closes (its own 3.6s timer), bring up the main window.
            splash.Closed += (_, _) =>
            {
                window.Show();
                window.Activate();
            };

            // Edge case: if init was slower than the splash, the splash already
            // closed by the time we got here — show the window immediately.
            if (!splash.IsLoaded || splash.Visibility != Visibility.Visible)
            {
                window.Show();
                window.Activate();
            }
        }
        catch (Exception ex)
        {
            try { Log.Fatal(ex, "Fatal error during startup (deferred init)"); } catch { }
            MessageBox.Show(
                $"Limitless Trade Copier failed to start:\n\n{ex.Message}\n\n{ex}",
                "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _viewModel?.Dispose();
            _engine?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _db?.Dispose();
        }
        catch { /* tolerate */ }
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// Map any request for an assembly with "mt5" or "mtapi" in its name to the actual
    /// broker DLL file on disk. Handles the case where the DLL's internal assembly
    /// identity ("mt5api") doesn't match the file name ("mtapi.mt5.dll").
    /// </summary>
    private static System.Reflection.Assembly? ResolveMt5Assembly(object? sender, ResolveEventArgs args)
    {
        var requested = new System.Reflection.AssemblyName(args.Name).Name ?? "";
        if (!requested.Contains("mt5", StringComparison.OrdinalIgnoreCase) &&
            !requested.Contains("mtapi", StringComparison.OrdinalIgnoreCase))
            return null;

        // Try several known-good filenames.
        string[] tryFiles =
        {
            "mtapi.mt5.dll",
            "mt5api.dll",
            $"{requested}.dll",
        };

        foreach (var fn in tryFiles)
        {
            var path = Path.Combine(AppContext.BaseDirectory, fn);
            if (File.Exists(path))
            {
                try { return System.Reflection.Assembly.LoadFrom(path); }
                catch { /* keep trying */ }
            }
            var libPath = Path.Combine(AppContext.BaseDirectory, "lib", fn);
            if (File.Exists(libPath))
            {
                try { return System.Reflection.Assembly.LoadFrom(libPath); }
                catch { /* keep trying */ }
            }
        }

        return null;
    }
}
