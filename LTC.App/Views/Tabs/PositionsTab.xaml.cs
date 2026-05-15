using System.Windows;
using System.Windows.Controls;
using LTC.App.ViewModels;

namespace LTC.App.Views.Tabs;

public partial class PositionsTab : UserControl
{
    public PositionsTab() { InitializeComponent(); }

    private MainShell? OwnerShell => TabHelpers.FindOwnerShell(this);

    private async void OnClosePositionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is PositionViewModel pos
            && OwnerShell is not null)
        {
            await OwnerShell.InvokeClosePosition(pos);
        }
    }
}
