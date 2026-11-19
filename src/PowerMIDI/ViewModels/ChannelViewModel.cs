using System.Windows.Input;
using PowerMIDI.Behaviors;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// How the instrument of a channel is shown: not used yet (gray), from the file, or chosen by hand (blue).
public enum InstrumentState { Unused, File, Manual }

public sealed record MapChoice(string Text, int Map);

// One row of the mixer. Properties set by the user (slider, mute, map, instrument) call the mixer;
// MixerViewModel.Refresh updates them from ChannelMixer without calling it back (see Updating).
// A row of another MIDI port (Port > 0: a file on several ports, with the SoundFont engine) shows that channel as the
// file plays it (OtherPortsPlayer); its instrument, mute, solo and keyboard mode act, not its slider nor its map.
public sealed class ChannelViewModel : ObservableObject
{
    readonly MixerViewModel mixer;

    public ChannelViewModel(MixerViewModel mixer, int channel, int port = 0)
    {
        this.mixer = mixer;
        Channel = channel;
        Port = port;
        // The controllers and map of a row of another port do nothing: they are the file's. Its instrument (the
        // SoundFont's presets), mute, solo and keyboard mode act as on port A.
        RelayCommand Command(Action action) => new(() => { if (port == 0) mixer.Run(action); });
        ClearControllerCommand = Command(() => mixer.ClearController(Channel));
        WholeTrackCommand = Command(() => mixer.ToggleWholeTrack(Channel));
        SoloCommand = new RelayCommand(() => mixer.Run(() => mixer.ToggleSolo(this)));
        InstrumentWheelCommand = new RelayCommand(p => mixer.Run(() =>
        {
            if (port == 0) mixer.StepInstrumentOrMap(Channel, (WheelStep)p); else mixer.StepOtherInstrument(this, (WheelStep)p);
        }));
        MapWheelCommand = new RelayCommand(p => { if (port == 0) mixer.Run(() => mixer.StepMap(Channel, ((WheelStep)p).Direction)); });
        KeyboardCommand = new RelayCommand(() => mixer.Run(() => mixer.KeyboardToggle?.Invoke(Port, Channel)));
    }

    public int Channel { get; }
    public int Port { get; }
    public bool IsEditable => Port == 0;

    // Number: "1".."16"; with several ports shown, "A1".."A16", "B1".."B16".
    public string Number => mixer.ShowsOtherPorts ? $"{(char)('A' + Port)}{Channel + 1,-2}" : $"{Channel + 1,2}";
    public string NumberToolTip => Port > 0
        ? $"Click to play port {(char)('A' + Port)} channel {Channel + 1}{(Channel == 9 ? " (drums)" : "")} with computer keyboard (instrument and controllers: file's)"
        : $"Click to play channel {Channel + 1}{(Channel == 9 ? " (drums)" : "")} with computer keyboard";
    internal void NumberChanged() => OnPropertyChanged(nameof(Number));

    // Shown: the rows of the first port always; those of another port while a file on several ports plays.
    bool isVisible = true;
    public bool IsVisible { get => isVisible; internal set => Set(ref isVisible, value); }

    // Click on the number: keyboard mode on this channel (see MainViewModel.Keyboard.cs).
    public ICommand KeyboardCommand { get; }

    bool isKeyboard;
    public bool IsKeyboard { get => isKeyboard; internal set => Set(ref isKeyboard, value); }
    public string MuteToolTip => Port > 0 ? $"Mute port {(char)('A' + Port)} channel {Channel + 1} (right click: solo)" : $"Mute channel {Channel + 1} (right click: solo)";

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
            if (Set(ref this.value, value) && !Updating && Port == 0)
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
                mixer.Run(() => { if (Port == 0) mixer.SetMute(Channel, value); else mixer.SetOtherPortMute(this, value); });
        }
    }

    // Row of the Mixer (solo, keyboard mode): its port letter and channel number, as shown.
    internal string Name => mixer.ShowsOtherPorts ? $"{(char)('A' + Port)}{Channel + 1}" : $"{Channel + 1}";

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
            if (Set(ref selectedMap, value) && !Updating && value != null && Port == 0)
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
                mixer.Run(() => { if (Port == 0) mixer.SelectInstrument(Channel, value); else mixer.SelectOtherInstrument(this, value); });
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
            // Opening: sounds of the channel's map (port B: the SoundFont's presets); closing: back to showing the name.
            mixer.Run(() =>
            {
                if (Port > 0) { if (value) mixer.OpenOtherInstrumentList(this); else mixer.RefreshOtherPorts(); }
                else if (value) mixer.OpenInstrumentList(this);
                else mixer.RefreshInstrument(this);
            });
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
