using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace LTC.App.Views;

/// <summary>
/// Boot splash. Animates green circle + red circle merging into amber,
/// then reveals "LIMITLESS / TRADE COPIER" and the tagline word-by-word.
/// Auto-closes after the storyboard finishes (~3.6s) and OnClosed fires
/// so App.OnStartup can chain into showing MainWindow.
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>Total splash duration in milliseconds. The storyboard plays
    /// out by 3.5s; we hold the final composition for ~1.3s so the user has
    /// time to actually READ "Copy your Limitless Opportunities" before the
    /// main window opens. Earlier 3.6s wasn't enough — the last word barely
    /// finished fading in before the splash dismissed.</summary>
    private const int SplashDurationMs = 4800;

    public SplashWindow()
    {
        InitializeComponent();

        // Version slug at bottom — matches the format used in Settings.
        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetName().Version?.ToString() ?? "1.0.0";
        VersionText.Text = $"VERSION {version}  ·  ENGINE READY";

        // Schedule self-close after the animation completes. We use a
        // DispatcherTimer instead of Task.Delay so we stay on the UI thread
        // and don't risk closing during a render frame.
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(SplashDurationMs)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        timer.Start();
    }
}
