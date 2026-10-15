using System.Windows.Input;
using PowerMIDI.Behaviors;

namespace PowerMIDI.ViewModels;

// How an effect value is shown: from the file, fixed in the interface (blue), or set by the module (gray "auto").
public enum ValueState { File, Fixed, Auto }

public sealed record EffectChoice(string Text, int Value);

// One effect parameter (index e in GsXgEffects.Addresses). Setters called by the view fix the value;
// EffectsViewModel.Refresh updates the properties from GsXgEffects without calling it back (see Updating).
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

    // The drum setup notes change with the kit (names) and the fixed values (marks).
    IReadOnlyList<EffectChoice> choices;
    public IReadOnlyList<EffectChoice> Choices { get => choices; internal set => Set(ref choices, value); }
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

    // Button of a part of an XG Variation / Insertion effect: the parameter (index) is the part the effect is
    // inserted on, one at most. Pressed = this part; pressing it again takes the effect off every part.
    public EffectChannelViewModel(EffectsViewModel effects, int index, int channel, int offValue) : this(effects, index, channel)
    {
        Part = channel;
        this.offValue = offValue;
    }

    public string Number { get; }
    public int Part { get; } = -1;   // -1: a switch of its own (0 / 1)
    readonly int offValue;

    bool isChecked;
    public bool IsChecked
    {
        get => isChecked;
        set { if (Set(ref isChecked, value)) Fix(Part >= 0 ? (value ? Part : offValue) : value ? 1 : 0); }
    }
}

// Two blocks of the Effects tab shown side by side.
public sealed record EffectBlockPair(EffectBlockViewModel Left, EffectBlockViewModel Right);

// Block of the Effects tab (Reverb, Chorus, Delay, EFX, Variation): its title, its parameters and,
// for the EFX (and the MT-32 reverb), the channel (part) buttons.
public sealed class EffectBlockViewModel : ObservableObject
{
    public string Title { get; init; }
    public bool IsFirst { get; set; }   // at the top of the tab (no space above its title)
    public bool ShowTitle { get; init; } = true;   // false: the block is a page of the tab strip, which names it
    public string TabTitle { get; init; }          // on its button of the tab strip (the full title in its tooltip)
    // Page shown (blocks of a tab strip, see EffectsViewModel.Tabs): pressing its button selects it; the button of
    // the page shown stays pressed.
    internal Action Selected;
    bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set { if (value && Set(ref isSelected, true)) Selected?.Invoke(); else OnPropertyChanged(); }
    }
    internal void Deselect() { isSelected = false; OnPropertyChanged(nameof(IsSelected)); }
    public string ChannelsLabel { get; init; } = "Channels";
    public List<EffectParamViewModel> Params { get; } = new();
    public List<EffectChannelViewModel> Channels { get; } = new();
    // Second row of buttons, under the first (XG Drums: the channels that use the drum setup shown).
    public string PartsLabel { get; init; }
    public List<EffectChannelViewModel> Parts { get; } = new();
    public bool HasParts => Parts.Count > 0;
    // XG Drums: Follow button, at the right of the first row of buttons (the note shown follows the notes played).
    public bool HasFollow { get; init; }
    bool follow;
    public bool Follow { get => follow; set => Set(ref follow, value); }
    public bool HasChannels => Channels.Count > 0 && channelsShown;
    // False: the buttons do not apply now (XG Variation connected as a system effect).
    bool channelsShown = true;
    internal bool ChannelsShown { set { if (Set(ref channelsShown, value)) OnPropertyChanged(nameof(HasChannels)); } }
}

// Heading inside a block, above a group of parameters (XG Part page: EQ), with a line above unless it is the first.
public sealed class EffectHeadingViewModel : EffectParamViewModel
{
    public EffectHeadingViewModel(EffectsViewModel effects, string text) : base(effects, -1) { Label = text; }
    bool hasLine = true;
    public bool HasLine { get => hasLine; internal set => Set(ref hasLine, value); }
}
