using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WinOsUtils.Services;

namespace WinOsUtils.Converters;

public sealed class StateToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var code = value is CheckState s
            ? s switch
            {
                CheckState.Compliant => 0xE73E,
                CheckState.NeedsChange => 0xE7BA,
                CheckState.Error => 0xEA39,
                _ => 0xE738,
            }
            : 0xE738;

        return ((char)code).ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is CheckState s
            ? s switch
            {
                CheckState.Compliant => "SuccessBrush",
                CheckState.NeedsChange => "CautionBrush",
                CheckState.Error => "CriticalBrush",
                _ => "TextSecondaryBrush",
            }
            : "TextSecondaryBrush";

        return System.Windows.Application.Current.TryFindResource(key) is Brush brush
            ? brush
            : Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
