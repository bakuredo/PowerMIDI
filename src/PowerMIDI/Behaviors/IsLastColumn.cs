using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace PowerMIDI.Behaviors;

// Whether a column header is the one of the last column of its list: values = [its column, the columns of the list].
// The separator after that column is not drawn (it would show against the rounded border of the list, see Styles.xaml).
public sealed class IsLastColumn : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 && values[0] is GridViewColumn column && values[1] is GridViewColumnCollection columns &&
        columns.Count > 0 && columns[^1] == column;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
