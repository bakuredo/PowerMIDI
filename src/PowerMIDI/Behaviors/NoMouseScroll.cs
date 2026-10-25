using System.Windows;
using System.Windows.Input;

namespace PowerMIDI.Behaviors;

// An item of an open list does not scroll the list when the mouse moves onto it. The list gives the focus to the item
// under the mouse, and a focused item asks to be brought into view: a row cut at the bottom of the list scrolled it by
// one row, the next row came under the mouse cut in turn, and so on (the list scrolled by itself while the mouse was
// at its bottom). An item reached with the arrow keys still comes into view. Set for all the items of the drop-down
// lists by the ComboBoxItem style of Styles.xaml.
public static class NoMouseScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(NoMouseScroll), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.RequestBringIntoView -= OnRequestBringIntoView;
        if ((bool)e.NewValue) element.RequestBringIntoView += OnRequestBringIntoView;
    }

    // The item itself asks (not an element inside it), with the mouse on it and no key pressed: the mouse brought it.
    static void OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject == sender && sender is UIElement { IsMouseOver: true } && !IsArrowKeyDown()) e.Handled = true;
    }

    static bool IsArrowKeyDown() =>
        Keyboard.IsKeyDown(Key.Up) || Keyboard.IsKeyDown(Key.Down) || Keyboard.IsKeyDown(Key.PageUp) || Keyboard.IsKeyDown(Key.PageDown) ||
        Keyboard.IsKeyDown(Key.Home) || Keyboard.IsKeyDown(Key.End);
}
