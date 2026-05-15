using System.Windows;
using System.Windows.Controls;
using LTC.App.ViewModels;

namespace LTC.App.Views.Tabs;

/// <summary>
/// Prop Journal tab — live risk dashboard. Most of the work is data binding;
/// the only handler we need is for the account-switcher tab clicks, which
/// updates the VM's SelectedAccount.
/// </summary>
public partial class PropJournalTab : UserControl
{
    public PropJournalTab()
    {
        InitializeComponent();
    }

    private void OnAccountTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe
            && fe.Tag is AccountViewModel acct
            && DataContext is PropJournalViewModel vm)
        {
            vm.SelectedAccount = acct;
        }
    }

    /// <summary>
    /// User toggled the "Close all on target hit" checkbox on the meter
    /// card. The IsChecked binding already mutated PropConfig in memory;
    /// here we just persist that change to disk. We DON'T reload or
    /// reconnect — it's a flag flip.
    /// </summary>
    private void OnAutoCloseOnTargetClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PropJournalViewModel vm) return;
        if (vm.SelectedAccount is null) return;
        // Persist via the MainViewModel reference exposed through the VM.
        vm.PersistSelectedAccount();
    }
}
