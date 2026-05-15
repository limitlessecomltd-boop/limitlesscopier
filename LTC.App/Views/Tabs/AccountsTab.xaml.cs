using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LTC.App.ViewModels;

namespace LTC.App.Views.Tabs;

public partial class AccountsTab : UserControl
{
    public AccountsTab() { InitializeComponent(); }

    private MainShell? OwnerShell => TabHelpers.FindOwnerShell(this);

    // Toolbar
    private void OnAddAccountClick(object sender, RoutedEventArgs e)
        => OwnerShell?.InvokeAddAccount();

    // -------------------------------------------------------------------
    // Row context menu / row interactions
    //
    // Each row template above has a ContextMenu whose MenuItems set
    // Tag="{Binding}" to the row's AccountViewModel and Click=... to one
    // of the handlers below. The handlers just extract the VM from
    // sender.Tag and forward to MainShell's public Invoke* helpers.
    // -------------------------------------------------------------------

    private void OnEditAccountClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is AccountViewModel vm)
            OwnerShell?.InvokeEditAccount(vm);
    }

    private void OnDeleteAccountClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is AccountViewModel vm)
            OwnerShell?.InvokeDeleteAccount(vm);
    }

    private void OnCloseAllOnAccountClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is AccountViewModel vm)
            OwnerShell?.InvokeCloseAllOnAccount(vm);
    }

    /// <summary>
    /// Double-clicking an account row opens the Edit dialog. Implemented
    /// as MouseLeftButtonDown with ClickCount==2 so we don't need a
    /// separate event subscription — keeps the row template lighter.
    /// </summary>
    private void OnAccountRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is FrameworkElement fe && fe.DataContext is AccountViewModel vm)
            OwnerShell?.InvokeEditAccount(vm);
    }
}

/// <summary>
/// Tabs are UserControls hosted inside the MainShell window. Most of their
/// buttons need to trigger dialogs that live on the shell. Rather than
/// duplicating dialog code in every tab, we walk up the visual tree to
/// find the shell and call its public Invoke* methods.
/// </summary>
internal static class TabHelpers
{
    public static MainShell? FindOwnerShell(DependencyObject child)
    {
        var p = child;
        while (p is not null && p is not MainShell)
            p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p);
        return p as MainShell;
    }
}
