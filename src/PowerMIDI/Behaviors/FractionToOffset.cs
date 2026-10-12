using System.Globalization;
using System.Windows.Data;

namespace PowerMIDI.Behaviors;

// Horizontal offset of a marker (insertion, loop start / end) above the position bar: values = [fraction 0..1, width of the bar].
// The slider track starts and ends half a thumb (about 5 px) inside the bar; the marker (8 px) is centered.
public sealed class FractionToOffset : IMultiValueConverter
{
    const double Inset = 5, MarkerWidth = 8;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double fraction || values[1] is not double width) return 0.0;
        return Inset + fraction * Math.Max(0, width - 2 * Inset) - MarkerWidth / 2;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}