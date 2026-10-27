using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PowerMIDI.Behaviors;

// Fill of a themed slider (Styles.xaml, ThemedSlider): from the start of the rail up to the thumb, or, with
// SliderFill.FromZero="True" (a value on both sides of 0, as the pitch), from 0 to the thumb.
public static class SliderFill
{
    public static readonly DependencyProperty FromZeroProperty = DependencyProperty.RegisterAttached(
        "FromZero", typeof(bool), typeof(SliderFill), new PropertyMetadata(false));

    public static bool GetFromZero(DependencyObject d) => (bool)d.GetValue(FromZeroProperty);
    public static void SetFromZero(DependencyObject d, bool value) => d.SetValue(FromZeroProperty, value);

    // Drawn in the colors of the theme (resource ThemedSliders: false with the light controls, then the slider of Windows).
    public static readonly DependencyProperty ThemedProperty = DependencyProperty.RegisterAttached(
        "Themed", typeof(bool), typeof(SliderFill), new PropertyMetadata(false));

    public static bool GetThemed(DependencyObject d) => (bool)d.GetValue(ThemedProperty);
    public static void SetThemed(DependencyObject d, bool value) => d.SetValue(ThemedProperty, value);
}

// Margins of the fill from 0 to the thumb: values = [value, minimum, maximum, width of the slider]. The thumb (10 px)
// moves between half a thumb from each end of the rail.
public sealed class ZeroFillMargin : IMultiValueConverter
{
    const double Inset = 5;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values[0] is not double value || values[1] is not double minimum ||
            values[2] is not double maximum || values[3] is not double width || maximum <= minimum)
            return new Thickness();
        double X(double v) => Inset + (Math.Clamp(v, minimum, maximum) - minimum) / (maximum - minimum) * Math.Max(0, width - 2 * Inset);
        double zero = X(0), thumb = X(value);
        return new Thickness(Math.Min(zero, thumb), 0, width - Math.Max(zero, thumb), 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
