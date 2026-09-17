using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace JBZUniveresalLunix.Converters;

public sealed class IntEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (!int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int expected))
            return Visibility.Collapsed;

        int actual = value switch
        {
            int number => number,
            _ when int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => int.MinValue
        };

        return actual == expected ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
