using System.Windows.Input;
using PowerMIDI.Behaviors;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// How the instrument of a channel is shown: not used yet (gray), from the file, or chosen by hand (blue).
public enum InstrumentState { Unused, File, Manual }

public sealed record MapChoice(string Text, int Map);

// One row of the mixer. Properties set by the user (slider, mute, map, instrument) call the mixer;
// MixerViewModel.Refresh updates them from ChannelMixer without calling it back (see Updating).
public sealed class ChannelViewModel : ObservableObject
{
    readonly MixerViewModel mixer;

    public ChannelViewModel(MixerViewModel mixer, int channel)
    {
        this.mixer = mixer;
        Channel = channel;
        ClearControllerCommand = new RelayCommand(() => mixer.Run(() => mixer.ClearController(Channel)));
        WholeTrackCommand = new RelayCommand(() => mixer.Run(() => mixer.ToggleWholeTrack(Channel)));
        SoloCommand = new RelayCommand(() => mixer.Run(() => mixer.ToggleSolo(Channel)));
        InstrumentWheelCommand = new RelayCommand(p => mixer.Run(() => mixer.StepInstrumentOrMap(Channel, (WheelStep)p)));
        MapWheelCommand = new RelayCommand(p => mixer.Run(() => mixer.StepMap(Channel, ((WheelStep)p).Direction)));
        KeyboardCommand = new RelayCommand(() => mixer.Run(() => mixer.KeyboardToggle?.Invoke(Channel)));
    }

    public int Channel { get; }
    public string Number => $"{Channel + 1,2}";
    public string NumberToolTip => $"Click to play channel {Channel + 1}{(Channel == 9 ? " (drums)" : "")} with the computer keyboard";

    // Click on the number: keyboard mode on this channel (see MainViewModel.Keyboard.cs).
    public ICommand KeyboardCommand { get; }

    bool isKeyboard;
    public bool IsKeyboard { get => isKeyboard; internal set => Set(ref isKeyboard, value); }
    public string MuteToolTip => $"Mute channel {Channel + 1} (right click: solo)";

    public ICommand ClearControllerCommand { get; }
    public ICommand SoloCommand { get; }
    public ICommand InstrumentWheelCommand { get; }
    public ICommand MapWheelCommand { get; }

    // True while the mixer updates this row: the setters then only notify the view.
    internal bool Updating;
    internal string ShownKey = "", InstrumentKey = "", MapKey = "";
    internal bool ListShown;   // the full instrument list was loaded (list opened)

    // ---------- Activity LED ----------
    bool isActive;
    public bool IsActive { get => isActive; internal set => Set(ref isActive, value); }

    // ---------- Slider of the displayed controller ----------
    double value = 100;   // 100 = volume after a reset
    public double Value
    {
        get => value;
        set
        {
            if (Set(ref this.value, value) && !Updating)
                mixer.Run(() => mixer.SetController(Channel, (int)Math.Round(value)));
        }
    }

    string valueText = "100";
    public string ValueText { get => valueText; internal set => Set(ref valueText, value); }

    bool isFixed;
    public bool IsFixed { get => isFixed; internal set => Set(ref isFixed, value); }

    // Fixed for the whole track, not only until the first change of the controller (value underlined).
    bool isWholeTrack;
    public bool IsWholeTrack { get => isWholeTrack; internal set => Set(ref isWholeTrack, value); }

    // Right click on the value: whole track or until the first change (see MixerViewModel.ToggleWholeTrack).
    public ICommand WholeTrackCommand { get; }

    string valueToolTip;
    public string ValueToolTip { get => valueToolTip; internal set => Set(ref valueToolTip, value); }

    string sliderToolTip;
    public string SliderToolTip { get => sliderToolTip; internal set => Set(ref sliderToolTip, value); }

    // ---------- Mute / solo ----------
    bool isMuted;
    public bool IsMuted
    {
        get => isMuted;
        set
        {
            if (Set(ref isMuted, value) && !Updating)
                mixer.Run(() => mixer.SetMute(Channel, value));
        }
    }

    bool isSolo;
    public bool IsSolo
    {
        get => isSolo;
        internal set { if (Set(ref isSolo, value)) OnPropertyChanged(nameof(MuteText)); }
    }

    public string MuteText => IsSolo ? "S" : "M";

    // ---------- Map (GS: Sound Canvas maps; XG: MU2000 maps) ----------
    IReadOnlyList<MapChoice> mapItems = MixerViewModel.GsMapChoices;
    public IReadOnlyList<MapChoice> MapItems { get => mapItems; internal set => Set(ref mapItems, value); }

    MapChoice selectedMap;
    public MapChoice SelectedMap
    {
        get => selectedMap;
        set
        {
            if (Set(ref selectedMap, value) && !Updating && value != null)
                mixer.Run(() => mixer.SetChannelMap(Channel, value.Map));
        }
    }

    bool mapEnabled;
    public bool MapEnabled { get => mapEnabled; internal set => Set(ref mapEnabled, value); }

    string mapToolTip;
    public string MapToolTip { get => mapToolTip; internal set => Set(ref mapToolTip, value); }

    // ---------- Instrument ----------
    // Closed list: a single item, the name to display; open list: all the sounds of the standard / map.
    IReadOnlyList<InstrumentChoice> instrumentItems = Array.Empty<InstrumentChoice>();
    public IReadOnlyList<InstrumentChoice> InstrumentItems { get => instrumentItems; internal set => Set(ref instrumentItems, value); }

    InstrumentChoice selectedInstrument;
    public InstrumentChoice SelectedInstrument
    {
        get => selectedInstrument;
        set
        {
            if (Set(ref selectedInstrument, value) && !Updating && value != null)
                mixer.Run(() => mixer.SelectInstrument(Channel, value));
        }
    }

    // Search box of the open list: the sounds whose name contains it (all of them when empty).
    internal IReadOnlyList<InstrumentChoice> AllItems = Array.Empty<InstrumentChoice>();
    string searchText = "";
    public string SearchText
    {
        get => searchText;
        set
        {
            if (Set(ref searchText, value ?? "") && !Updating) mixer.Run(() => mixer.FilterInstrumentList(this));
        }
    }

    // Enter in the search box.
    internal void ChooseFirstMatch() => mixer.ChooseFirstMatch(this);
    bool isListOpen;
    public bool IsListOpen
    {
        get => isListOpen;
        set
        {
            if (!Set(ref isListOpen, value)) return;
            // Opening: sounds of the channel's map; closing: back to showing the name.
            mixer.Run(() => { if (value) mixer.OpenInstrumentList(this); else mixer.RefreshInstrument(this); });
        }
    }

    InstrumentState instrumentState;
    public InstrumentState InstrumentState { get => instrumentState; internal set => Set(ref instrumentState, value); }

    // The sound asked for is not in the list of the mode (name shown in red: the likely fallback of the module).
    bool isInstrumentMissing;
    public bool IsInstrumentMissing { get => isInstrumentMissing; internal set => Set(ref isInstrumentMissing, value); }

    // XG instrument set by a Multi Part SysEx of the file (name shown in green).
    bool isSysExPatch;
    public bool IsSysExPatch { get => isSysExPatch; internal set => Set(ref isSysExPatch, value); }

    string instrumentToolTip;
    public string InstrumentToolTip { get => instrumentToolTip; internal set => Set(ref instrumentToolTip, value); }

    bool isDrum;
    public bool IsDrum { get => isDrum; internal set => Set(ref isDrum, value); }
}
