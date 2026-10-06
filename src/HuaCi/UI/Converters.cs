using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HuaCi.UI;

/// <summary>true → Visible. ConverterParameter="Invert" flips it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool v = value is true;
        if (parameter as string == "Invert") v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible ^ (parameter as string == "Invert");
}

/// <summary>Non-empty string / non-empty collection / non-null → Visible. ConverterParameter="Invert" flips it.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "Invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
