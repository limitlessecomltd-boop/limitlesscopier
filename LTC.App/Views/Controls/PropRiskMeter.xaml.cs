using System.Windows.Controls;

namespace LTC.App.Views.Controls;

/// <summary>
/// Risk meter for one prop firm account. DataContext should be an
/// AccountViewModel; binds to its DailyMeterValue / OverallMeterValue
/// / DailyHeadroomText etc. computed properties.
///
/// Pure data-binding; no code needed beyond InitializeComponent.
/// </summary>
public partial class PropRiskMeter : UserControl
{
    public PropRiskMeter()
    {
        InitializeComponent();
    }
}
