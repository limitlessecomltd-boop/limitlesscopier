using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LTC.App.Converters;

/// <summary>
/// Converts a resource-key string (e.g. "StatusOkBrush") into the actual Brush
/// looked up from application resources. Lets view models expose a string color
/// hint instead of having to import WPF types into the VM layer.
/// </summary>
public sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrEmpty(key))
            return DependencyProperty.UnsetValue;

        var resource = Application.Current?.TryFindResource(key);
        if (resource is Brush brush) return brush;
        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Bool/Int → Visibility, with optional inversion via parameter "Invert".
/// Int values: 0 is false, anything else is true. Useful for "show empty state when Count = 0".
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value switch
        {
            bool x   => x,
            int i    => i > 0,
            long l   => l > 0,
            double d => d > 0,
            null     => false,
            _        => true,   // any other non-null is "truthy"
        };
        if (parameter as string == "Invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>Empty/null string -> Collapsed, else Visible.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string;
        return string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a percentage (0-100) + a container width into a pixel width
/// for a progress-bar fill rectangle. WPF doesn't have a built-in
/// "progress as fraction of parent" primitive, so we compute it here.
///
/// Usage:
///   &lt;MultiBinding Converter="{StaticResource PercentToWidth}"&gt;
///     &lt;Binding Path="MyPercent" /&gt;
///     &lt;Binding Path="ActualWidth" RelativeSource="{...}" /&gt;
///   &lt;/MultiBinding&gt;
///
/// Returns 0 if either value is missing/invalid, so a bar that's not
/// yet measured collapses cleanly instead of rendering at full width.
/// </summary>
public sealed class PercentToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return 0d;

        double pct;
        switch (values[0])
        {
            case double d:    pct = d; break;
            case decimal dec: pct = (double)dec; break;
            case int i:       pct = i; break;
            case float f:     pct = f; break;
            default:          pct = 0d; break;
        }

        double total = values[1] is double w ? w : 0d;
        if (total <= 0 || double.IsNaN(total) || double.IsInfinity(total)) return 0d;
        if (pct < 0) pct = 0;
        if (pct > 100) pct = 100;
        return total * (pct / 100d);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
