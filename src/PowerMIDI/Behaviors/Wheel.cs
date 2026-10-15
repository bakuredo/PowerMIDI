using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PowerMIDI.Behaviors;

// Mouse wheel step: direction (+1 = down / next, -1 = up / previous) and the modifier keys held.
public sealed record WheelStep(int Direction, ModifierKeys Modifiers);

// Attached mouse wheel behaviors, set in XAML:
// - Wheel.SliderStep="True": the wheel moves a slider by ±1, or ±8 with Ctrl;
// - Wheel.Command="{Binding …}": the wheel runs a command with a WheelStep parameter
//   (on a combo box, only while its list is closed: an open list keeps its normal scrolling).
public static class Wheel
{
    public static readonly DependencyProperty SliderStepProperty = DependencyProperty.RegisterAttached(
        "SliderStep", typeof(bool), typeof(Wheel), new PropertyMetadata(false, OnSliderStepChanged));

    public static bool GetSliderStep(DependencyObject d) => (bool)d.GetValue(SliderStepProperty);
    public static void SetSliderStep(DependencyObject d, bool value) => d.SetValue(SliderStepProperty, value);

    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(Wheel), new PropertyMetadata(null, OnCommandChanged));

    public static ICommand GetCommand(DependencyObject d) => (ICommand)d.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject d, ICommand value) => d.SetValue(CommandProperty, value);

    public static readonly DependencyProperty ComboStepProperty = DependencyProperty.RegisterAttached(
        "ComboStep", typeof(bool), typeof(Wheel), new PropertyMetadata(false, OnComboStepChanged));

    public static bool GetComboStep(DependencyObject d) => (bool)d.GetValue(ComboStepProperty);
    public static void SetComboStep(DependencyObject d, bool value) => d.SetValue(ComboStepProperty, value);

    static void OnSliderStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RangeBase slider) return;
        slider.PreviewMouseWheel -= StepSlider;
        if ((bool)e.NewValue) slider.PreviewMouseWheel += StepSlider;
    }

    // ±1, or ±8 with Ctrl. The rest (sending, fixed value) goes through the Value binding.
    public static void StepSlider(object sender, MouseWheelEventArgs e)
    {
        var slider = (RangeBase)sender;
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 8 : 1;
        double value = Math.Round(slider.Value) + Math.Sign(e.Delta) * step;
        slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, value));
        e.Handled = true;
    }

    static void OnComboStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo) return;
        combo.PreviewMouseWheel -= StepCombo;
        if ((bool)e.NewValue) combo.PreviewMouseWheel += StepCombo;
    }

    // Next / previous item (stops at both ends); an open list keeps its normal scrolling.
    static void StepCombo(object sender, MouseWheelEventArgs e)
    {
        var combo = (ComboBox)sender;
        if (combo.IsDropDownOpen) return;
        int index = Math.Max(0, Math.Min(combo.Items.Count - 1, combo.SelectedIndex - Math.Sign(e.Delta)));
        if (index != combo.SelectedIndex) combo.SelectedIndex = index;
        e.Handled = true;
    }

    static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewMouseWheel -= RunCommand;
        if (e.NewValue != null) element.PreviewMouseWheel += RunCommand;
    }

    static void RunCommand(object sender, MouseWheelEventArgs e)
    {
        if (sender is ComboBox { IsDropDownOpen: true }) return;
        var command = GetCommand((DependencyObject)sender);
        var step = new WheelStep(-Math.Sign(e.Delta), Keyboard.Modifiers);
        if (command?.CanExecute(step) == true) command.Execute(step);
        e.Handled = true;
    }
}
