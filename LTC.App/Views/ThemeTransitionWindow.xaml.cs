using System.Windows;
using System.Windows.Threading;
using LTC.App.Services;

namespace LTC.App.Views;

/// <summary>
/// Theme transition overlay. Total window duration is 3 seconds:
///   - 0.0s  Window appears, progress fill starts moving
///   - 1.5s  Brush swap fires; the entire app retints in one frame
///   - 3.0s  Window auto-closes
/// The brush swap deliberately happens MID-animation so the transition feels
/// continuous instead of "click → freeze → suddenly different".
/// </summary>
public partial class ThemeTransitionWindow : Window
{
    private const int TotalMs = 3000;
    private const int SwapAtMs = 1500;

    private readonly AppTheme _targetTheme;

    public ThemeTransitionWindow(AppTheme targetTheme)
    {
        InitializeComponent();

        _targetTheme = targetTheme;

        // Title and subtitle — "Switching to dark / light mode" — and which
        // palette we're actually loading. We set these BEFORE Loaded fires so
        // the user sees the right text on the very first paint.
        TitleText.Text = targetTheme == AppTheme.Light
            ? "Switching to light mode"
            : "Switching to dark mode";

        SubtitleText.Text = targetTheme == AppTheme.Light
            ? "Swiss minimal palette · cream + orange"
            : "Trader cockpit palette · black + amber";

        // Schedule the brush mutation at the 1.5s midpoint. We use a
        // DispatcherTimer so it stays on the UI thread — brush mutations
        // must happen on the same thread WPF renders on.
        var swapTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(SwapAtMs)
        };
        swapTimer.Tick += (_, _) =>
        {
            swapTimer.Stop();
            // Apply the actual theme. Brushes mutate in place so this window
            // AND the main window behind it both retint in the same frame.
            App.Theme.Apply(_targetTheme);
        };
        swapTimer.Start();

        // Close timer at the full duration.
        var closeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(TotalMs)
        };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            Close();
        };
        closeTimer.Start();
    }
}
