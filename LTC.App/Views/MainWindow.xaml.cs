using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using LTC.App.ViewModels;
using LTC.Core.Models;
using LTC.Persistence;

namespace LTC.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? VM => DataContext as MainViewModel;

    private void OnAddAccountClick(object sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null) return;

        var dlg = new AddAccountDialog { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is Account newAccount)
        {
            vm.AddAccount(newAccount);
        }
    }

    private void OnAddLinkClick(object sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null) return;

        if (vm.Masters.Count == 0 || vm.Slaves.Count == 0)
        {
            MessageBox.Show(this,
                "You need at least one master and one slave account before creating a link.",
                "Add link", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new EditLinkDialog(vm.Masters, vm.Slaves) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is CopyLink newLink)
        {
            vm.AddLink(newLink);
        }
    }

    private void OnViewSymbolsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm) return;
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not ViewModels.AccountViewModel acct) return;

        // Pull the connection's symbol catalog. If the account isn't connected yet
        // we still open the dialog — it shows an explanatory empty state.
        var conn = vm.Engine.Connections.Get(acct.Model.Id);
        var symbols = conn?.AvailableSymbols;

        var dlg = new SymbolsExplorerDialog(acct.DisplayName, symbols) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.AppliedPrefix is not null && dlg.AppliedSuffix is not null)
        {
            // Persist the suggestion as the account's affixes.
            acct.Model.SymbolPrefix = dlg.AppliedPrefix;
            acct.Model.SymbolSuffix = dlg.AppliedSuffix;
            vm.Persistence.SaveAccount(acct.Model);
            MessageBox.Show(this,
                $"Saved.\nPrefix: \"{dlg.AppliedPrefix}\"\nSuffix: \"{dlg.AppliedSuffix}\"",
                "Symbol naming applied", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnDeleteAccountClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm) return;
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        if (mi.Tag is not ViewModels.AccountViewModel acct) return;

        // Count copy links that touch this account so we can warn the user.
        var linkCount = vm.Links.Count(l =>
            l.Model.MasterAccountId == acct.Model.Id ||
            l.Model.SlaveAccountId  == acct.Model.Id);

        var msg = linkCount == 0
            ? $"Delete account \"{acct.DisplayName}\"?\n\nThis disconnects from the broker and removes the account permanently."
            : $"Delete account \"{acct.DisplayName}\"?\n\n" +
              $"This will also delete {linkCount} copy link{(linkCount == 1 ? "" : "s")} that reference this account.\n\n" +
              $"This action cannot be undone.";

        var result = MessageBox.Show(this, msg, "Delete account",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK) return;

        try
        {
            vm.RemoveAccount(acct);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Failed to delete account: {ex.Message}",
                "Delete account", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnLogsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm) return;
        // Toggle the right pane between Activity and Logs.
        vm.ShowLogs = !vm.ShowLogs;
    }

    private void OnClearLogsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm) return;
        var result = MessageBox.Show(this,
            "Clear all log entries from this session?",
            "Clear logs", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (result == MessageBoxResult.OK) vm.ClearLogs();
    }

    // ========================================================================
    // Theme toggle — flips dark/light. Opens a 3-second transition overlay
    // that visually demonstrates the swap. The brush mutation itself happens
    // mid-animation (at 1.5s) so the user sees both colors crossfade through
    // the overlay window. See ThemeTransitionWindow for the exact timeline.
    // ========================================================================
    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        ShowThemeTransition(App.Theme.Current == Services.AppTheme.Dark
            ? Services.AppTheme.Light
            : Services.AppTheme.Dark);
    }

    /// <summary>
    /// Show the theme transition overlay. Idempotent: if a transition is
    /// already running we ignore the click rather than stacking overlays.
    /// </summary>
    internal void ShowThemeTransition(Services.AppTheme target)
    {
        // Don't open a second overlay on top of an existing one.
        foreach (Window w in Application.Current.Windows)
        {
            if (w is ThemeTransitionWindow) return;
        }

        // Skip the overlay if the user is already on the target theme.
        if (App.Theme.Current == target) return;

        var overlay = new ThemeTransitionWindow(target) { Owner = this };
        overlay.Show();
    }

    // ========================================================================
    // Settings dialog
    // ========================================================================
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm) return;
        var dlg = new SettingsDialog(vm) { Owner = this };
        var ok = dlg.ShowDialog() == true;
        if (ok && dlg.ResetRequested)
        {
            try
            {
                // Delete every account (cascades links via FK in SQLite).
                // We snapshot the account list first because RemoveAccount
                // mutates Masters/Slaves while we iterate.
                var allAccounts = vm.Masters.Concat(vm.Slaves).ToList();
                foreach (var a in allAccounts) vm.RemoveAccount(a);
                MessageBox.Show(this,
                    "All data deleted. The app will close now.",
                    "Reset complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Reset failed: {ex.Message}",
                    "Reset", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Application.Current.Shutdown();
        }
    }

    // ========================================================================
    // Edit account
    // ========================================================================
    private void OnEditAccountClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm) return;
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        if (mi.Tag is not ViewModels.AccountViewModel acct) return;

        var dlg = new AddAccountDialog(acct.Model) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not LTC.Core.Models.Account updated) return;

        try
        {
            // Strategy: remove the old engine connection, save the updated account,
            // then re-add it. This guarantees the engine picks up changes to
            // server/login/password/role without us needing a special reload path.
            vm.ReplaceAccount(acct, updated);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to update account: {ex.Message}",
                "Edit account", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ========================================================================
    // Edit / delete copy link
    // ========================================================================
    private void OnEditLinkClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm) return;
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        if (mi.Tag is not ViewModels.CopyLinkViewModel link) return;

        var dlg = new EditLinkDialog(vm.Masters, vm.Slaves, link.Model) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not LTC.Core.Models.CopyLink updated) return;

        try
        {
            // Mutate the existing link's model in place so persistence (UpdateLink)
            // updates the same row by Id.
            link.Model.MasterAccountId   = updated.MasterAccountId;
            link.Model.SlaveAccountId    = updated.SlaveAccountId;
            link.Model.LotSizing         = updated.LotSizing;
            link.Model.ReverseCopy       = updated.ReverseCopy;
            link.Model.Enabled           = updated.Enabled;
            link.Model.UpdatedAt         = DateTime.UtcNow;
            vm.UpdateLink(link);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to update link: {ex.Message}",
                "Edit link", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDeleteLinkClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm) return;
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        if (mi.Tag is not ViewModels.CopyLinkViewModel link) return;

        var msg = $"Delete copy link from \"{link.MasterName}\" to \"{link.SlaveName}\"?\n\n" +
                  $"Trades on {link.MasterName} will no longer be copied to {link.SlaveName}.";
        var result = MessageBox.Show(this, msg, "Delete copy link",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK) return;

        try
        {
            vm.RemoveLink(link);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to delete link: {ex.Message}",
                "Delete copy link", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ========================================================================
    // Positions pane toggle + close handlers
    // ========================================================================
    private void OnPositionsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm) return;
        // Toggle: clicking again from Positions takes you back to Activity.
        vm.ShowPositions = !vm.ShowPositions;
        // If we're showing Positions, hide Logs (the right pane is mutually exclusive).
        if (vm.ShowPositions) vm.ShowLogs = false;
    }

    private async void OnClosePositionClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm) return;
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not ViewModels.PositionViewModel pos) return;

        var confirm = MessageBox.Show(this,
            $"Close {pos.SideText} {pos.Symbol} ({pos.Volume} lots, ticket #{pos.Ticket})?\n\n" +
            $"Current P&L: {pos.ProfitText}",
            "Close position",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        btn.IsEnabled = false;
        try
        {
            var ok = await vm.CloseSinglePositionAsync(pos);
            if (!ok)
            {
                MessageBox.Show(this,
                    "The broker rejected the close. Check the Logs panel for details.",
                    "Close position", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { btn.IsEnabled = true; }
    }

    private async void OnCloseAllOnAccountClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm) return;
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not ViewModels.AccountViewModel acct) return;

        // Count how many positions belong to this account so the warning has weight.
        var count = vm.Positions.Count(p => p.AccountId == acct.Model.Id);
        if (count == 0)
        {
            MessageBox.Show(this, $"No open positions on {acct.DisplayName}.",
                "Close all", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Close ALL {count} open position{(count == 1 ? "" : "s")} on {acct.DisplayName}?",
            "Close all on account",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        btn.IsEnabled = false;
        try
        {
            var closed = await vm.CloseAllOnAccountAsync(acct.Model.Id);
            MessageBox.Show(this, $"Closed {closed} of {count} positions on {acct.DisplayName}.",
                "Close all", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { btn.IsEnabled = true; }
    }

    private async void OnCloseAllEverywhereClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel vm) return;
        if (sender is not System.Windows.Controls.Button btn) return;

        var count = vm.Positions.Count;
        if (count == 0)
        {
            MessageBox.Show(this, "No open positions on any account.",
                "Close all", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Two-step confirmation for the panic button — this affects every account.
        var first = MessageBox.Show(this,
            $"Close ALL {count} open position{(count == 1 ? "" : "s")} across every connected account?\n\n" +
            "This is the panic button. It will not undo trades that have already settled.",
            "Close all everywhere",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (first != MessageBoxResult.OK) return;

        var second = MessageBox.Show(this,
            "Are you absolutely sure? This cannot be undone.",
            "Final confirmation",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (second != MessageBoxResult.OK) return;

        btn.IsEnabled = false;
        try
        {
            var closed = await vm.CloseAllEverywhereAsync();
            MessageBox.Show(this, $"Closed {closed} of {count} positions.",
                "Close all everywhere", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { btn.IsEnabled = true; }
    }
}
