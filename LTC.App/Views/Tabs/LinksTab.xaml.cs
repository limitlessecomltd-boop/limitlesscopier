using System.Windows;
using System.Windows.Controls;
using LTC.App.ViewModels;

namespace LTC.App.Views.Tabs;

public partial class LinksTab : UserControl
{
    public LinksTab() { InitializeComponent(); }

    private MainShell? OwnerShell => TabHelpers.FindOwnerShell(this);

    private void OnAddLinkClick(object sender, RoutedEventArgs e)
        => OwnerShell?.InvokeAddLink();

    private void OnEditLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CopyLinkViewModel link)
            OwnerShell?.InvokeEditLink(link);
    }

    private void OnDeleteLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CopyLinkViewModel link)
            OwnerShell?.InvokeDeleteLink(link);
    }
}
