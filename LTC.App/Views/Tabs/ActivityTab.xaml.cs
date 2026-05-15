using System.Windows;
using System.Windows.Controls;

namespace LTC.App.Views.Tabs;

public partial class ActivityTab : UserControl
{
    public ActivityTab() { InitializeComponent(); }

    private MainShell? OwnerShell => TabHelpers.FindOwnerShell(this);

    private void OnLogsClick(object sender, RoutedEventArgs e)
        => OwnerShell?.InvokeLogsView();

    private void OnClearLogsClick(object sender, RoutedEventArgs e)
        => OwnerShell?.InvokeClearLogs();
}
