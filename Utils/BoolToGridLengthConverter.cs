using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace M1Scan.Utils
{
    /// <summary>
    /// true → GridLength angivet i ConverterParameter (fx "1*"), false → 0.
    /// Bruges til at lade en kolonne/række helt kollapse sin plads (i modsætning til
    /// blot at skjule dens indhold, hvor Grid'et stadig reserverer pladsen).
    /// </summary>
    public class BoolToGridLengthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is true)
            {
                var text = parameter as string ?? "*";
                return (GridLength)new GridLengthConverter().ConvertFromString(text)!;
            }
            return new GridLength(0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
