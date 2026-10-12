using System.Windows.Input;
using PowerMIDI.Behaviors;

namespace PowerMIDI.ViewModels;

// How an effect value is shown: from the file, fixed in the interface (blue), or set by the module (gray "auto").
public enum ValueState { File, Fixed, Auto }

public sealed record EffectChoice(string Text, int Value);

// One effect parameter (index e in ChannelMixer.EffectAddresses). Setters called by the view fix the value;
// EffectsViewModel.Refresh updates the properties from ChannelMixer without calling it back (see Updating).
public abstract class EffectParamViewModel : ObservableObject
{
    protected readonly EffectsViewModel effects;

    protected EffectParamViewModel(EffectsViewModel effects, int index)
    {
        this.effects = effects;
        Index = index;
        ClearCommand = new RelayCommand(() => effects.Run(() => effects.ClearEffect(Index)));
    }

    public int Index { get; }

    // Right click: back to the file value.
    public ICommand ClearCommand { get; }

    internal bool Updating;
    internal string Key = "";

    string label;
    public string Label { get => label; internal set => Set(ref label, value); }

    bool isVisible = true;
    public bool IsVisible { get => isVisible; internal set => Set(ref isVisible, value); }

    bool isFixed;
    public bool IsFixed { get => isFixed; internal set => Set(ref isFixed, value); }

    string toolTip;
    public string ToolTip { get => toolTip; internal set => Set(ref toolTip, value); }

    // Sets a value chosen by the user (ignored while the view model updates itself).
    protected void Fix(int value)
    {
        if (!Updating) effects.Run(() => effects.SetEffect(Index, value));
    }
}

// Effect type, XG Variation connection or part: a list.
public sealed class EffectListViewModel : EffectParamViewModel
{
    public EffectListViewModel(EffectsViewModel effects, int index, IReadOnlyList<EffectChoice> choices) : base(effects, index)
    {
        Choices = choices;
        WheelCommand = new RelayCommand(p => effects.Run(() => Step(((WheelStep)p).Direction)));
    }

    public IReadOnlyList<EffectChoice> Choices { get; }
    public ICommand WheelCommand { get; }

    EffectChoice selectedChoice;
    public EffectChoice SelectedChoice
    {
        get => selectedChoice;
        set { if (Set(ref selectedChoice, value) && value != null) Fix(value.Value); }
    }

    // Value not in the list (type of another model): its code; unknown: "auto".
    string valueText = "";
    public string ValueText { get => valueText; internal set => Set(ref valueText, value); }

    // Wheel on the closed list: next / previous choice.
    void Step(int direction)
    {
        int index = Choices.ToList().IndexOf(SelectedChoice);
        int target = Math.Max(0, Math.Min(Choices.Count - 1, index + direction));
        if (target != index) SelectedChoice = Choices[target];
    }
}

// Numeric parameter: a slider and its value (with unit).
public sealed class EffectSliderViewModel : EffectParamViewModel
{
    public EffectSliderViewModel(EffectsViewModel effects, int index) : base(effects, index) { }

    double minimum, maximum = 127;
    public double Minimum { get => minimum; internal set => Set(ref minimum, value); }
    public double Maximum { get => maximum; internal set => Set(ref maximum, value); }

    double value;
    public double Value
    {
        get => value;
        set { if (Set(ref this.value, value)) Fix((int)Math.Round(value)); }
    }

    string valueText = "";
    public string ValueText { get => valueText; internal set => Set(ref valueText, value); }

    ValueState valueState;
    public ValueState ValueState { get => valueState; internal set => Set(ref valueState, value); }
}

// EFX on a channel: a toggle button (the part goes through the EFX or not).
public sealed class EffectChannelViewModel : EffectParamViewModel
{
    public EffectChannelViewModel(EffectsViewModel effects, int index, int channel) : this(effects, index, (channel + 1).ToString()) { }

    // Button with its own label (MT-32 parts: "1".."8", "R").
    public EffectChannelViewModel(EffectsViewModel effects, int index, string number) : base(effects, index)
    {
        Number = number;
    }

    public string Number { get; }

    bool isChecked;
    public bool IsChecked
    {
        get => isChecked;
        set { if (Set(ref isChecked, value)) Fix(value ? 1 : 0); }
    }
}

// Two blocks of the Effects tab shown side by side.
public sealed record EffectBlockPair(EffectBlockViewModel Left, EffectBlockViewModel Right);

// Block of the Effects tab (Reverb, Chorus, Delay, EFX, Variation): its title, its parameters and,
// for the EFX (and the MT-32 reverb), the channel (part) buttons.
public sealed class EffectBlockViewModel
{
    public string Title { get; init; }
    public bool IsFirst { get; set; }   // at the top of the tab (no space above its title)
    public string ChannelsLabel { get; init; } = "Channels";
    public List<EffectParamViewModel> Params { get; } = new();
    public List<EffectChannelViewModel> Channels { get; } = new();
    public bool HasChannels => Channels.Count > 0;
}
