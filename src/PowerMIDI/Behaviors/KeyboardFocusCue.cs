using System.Windows;
using System.Windows.Input;

namespace PowerMIDI.Behaviors;

// Frame of keyboard focus of a drop-down list, shown only when the focus came from the keyboard (Tab), as the focus
// frames of Windows: not after a click (a list chosen with the mouse keeps the focus). KeyboardFocusCue.Track="True"
// (ComboBox style of Styles.xaml) keeps KeyboardFocusCue.IsShown up to date; the template shows its frame from it.
public static class KeyboardFocusCue
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(bool), typeof(KeyboardFocusCue), new PropertyMetadata(false, OnTrackChanged));

    public static bool GetTrack(DependencyObject d) => (bool)d.GetValue(TrackProperty);
    public static void SetTrack(DependencyObject d, bool value) => d.SetValue(TrackProperty, value);

    public static readonly DependencyProperty IsShownProperty = DependencyProperty.RegisterAttached(
        "IsShown", typeof(bool), typeof(KeyboardFocusCue), new PropertyMetadata(false));

    public static bool GetIsShown(DependencyObject d) => (bool)d.GetValue(IsShownProperty);
    public static void SetIsShown(DependencyObject d, bool value) => d.SetValue(IsShownProperty, value);

    static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.GotKeyboardFocus -= OnGotFocus;
        element.LostKeyboardFocus -= OnLostFocus;
        element.PreviewMouseDown -= OnMouseDown;
        if (!(bool)e.NewValue) return;
        element.GotKeyboardFocus += OnGotFocus;
        element.LostKeyboardFocus += OnLostFocus;
        element.PreviewMouseDown += OnMouseDown;
    }

    // The focus comes in (the list or an element of it): from the keyboard, the frame is shown.
    static void OnGotFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        SetIsShown((DependencyObject)sender, InputManager.Current.MostRecentInputDevice is KeyboardDevice);

    static void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is UIElement { IsKeyboardFocusWithin: false } element) SetIsShown(element, false);
    }

    static void OnMouseDown(object sender, MouseButtonEventArgs e) => SetIsShown((DependencyObject)sender, false);
}
