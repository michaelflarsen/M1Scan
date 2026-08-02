using System;
using System.Globalization;
using System.Windows.Data;

namespace M1Scan.Utils
{
    public class LossDisplayConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
                return "—";

            if (values[0] is not string lossDisplay)
                return "—";

            if (values[1] is not bool isReachable)
                return lossDisplay;

            if (lossDisplay == "—" || !lossDisplay.Contains("%"))
                return lossDisplay;

            if (double.TryParse(lossDisplay.TrimEnd('%'), out var loss) && loss >= 100 && !isReachable)
                return "Ikke svarende ⓘ";

            return lossDisplay;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
