using System.Windows;
using System.Windows.Media;

namespace PowerMIDI.Behaviors;

// An element cut to a rectangle with rounded corners of this radius (RoundedClip.Radius="5"), following its size: the
// content of a list inside its rounded border (Styles.xaml). Cheaper than an opacity mask, which draws the list again
// off screen at each change.
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);

    static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.SizeChanged -= OnSizeChanged;
        element.SizeChanged += OnSizeChanged;
        Clip(element);
    }

    static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Clip((FrameworkElement)sender);

    static void Clip(FrameworkElement element)
    {
        double radius = GetRadius(element);
        var clip = new RectangleGeometry(new Rect(element.RenderSize), radius, radius);
        clip.Freeze();
        element.Clip = clip;
    }
}
