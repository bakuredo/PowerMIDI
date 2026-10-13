using Melanchall.DryWetMidi.Multimedia;
using PowerMIDI.Behaviors;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// Mixer tab: 16 channels with LED, GS map, instrument, slider of the chosen controller, value and mute.
// The state itself lives in ChannelMixer (shared with the playback thread); this view model shows it and
// turns the user's actions into ChannelMixer calls.
public sealed class MixerViewModel : ObservableObject
{
    // Controllers shown by the sliders (ChannelMixer index; the "Sliders" menu gives it in the Tag of each item):
    // 0 = Volume (CC7), 1 = Pan (CC10), 2 = Reverb (CC91), 3 = Chorus (CC93), 4 = Variation / Delay (CC94),
    // 5 = Attack (CC73), 6 = Decay (CC75), 7 = Release (CC72), 8 = Cutoff (CC74), 9 = Resonance (CC71),
    // 10 = Vibrato Rate (CC76), 11 = Vibrato Depth (CC77), 12 = Vibrato Delay (CC78), 13 = Modulation (CC1, first in the menu).
    public static readonly string[] ControllerNames = { "Volume", "Pan", "Reverb", "Chorus", "Variation / Delay", "Attack Time",
        "Decay Time", "Release Time", "Filter Cutoff", "Filter Resonance", "Vibrato Rate", "Vibrato Depth", "Vibrato Delay", "Modulation" };
    public static int ControllerCount => ControllerNames.Length;

    public static readonly MapChoice[] GsMapChoices = { new("8850", 4), new("Pro", 3), new("88", 2), new("55", 1) };
    public static readonly MapChoice[] XgMapChoices = { new("2000", 2), new("MEx", 3), new("SFX", 4), new("Spl", 5), new("GM2", 6) };
    // With the MU Basic voice map: what the MU50 has.
    public static readonly MapChoice[] XgMapChoicesMu50 = { new("50", InstrumentDefinitions.XgMu50Map), new("SFX", 4) };

    static readonly long LedHoldTicks = TimeSpan.FromMilliseconds(100).Ticks;

    readonly InstrumentDefinitions defs;
    readonly Func<IOutputDevice> output;   // current MIDI port (null if none)
    readonly Action<string> setStatus;
    readonly Action<Exception> showError;

    public MixerViewModel(InstrumentDefinitions defs, Func<IOutputDevice> output, Action<string> setStatus, Action<Exception> showError)
    {
        this.defs = defs;
        this.output = output;
        this.setStatus = setStatus;
        this.showError = showError;
        Channels = Enumerable.Range(0, 16).Select(c => new ChannelViewModel(this, c)).ToList();
        ResetCommand = new RelayCommand(() => Run(Reset));
    }

    // Reset button.
    public System.Windows.Input.ICommand ResetCommand { get; }

    public IReadOnlyList<ChannelViewModel> Channels { get; }

    // Standard of the current track (GS, XG, GM…), used to name the instruments. No track: the default one.
    string standard = MidiTools.DefaultStandard;
    public string Standard
    {
        get => standard;
        set { if (Set(ref standard, value)) OnPropertyChanged(nameof(IsXg)); }
    }

    // XG mode: the voice map list is shown under the channels.
    public bool IsXg => HasXgBanks;

    // ---------- XG voice map (the list under the channels): Auto, MU Basic, MU100 Native ----------
    // It tells which version of the 128 capital voices bank LSB 0 plays (see ChannelMixer.VoiceMap): sent to the
    // module, and followed by the names and the maps shown. It belongs to the module, not to the track: kept from
    // one track to the next and remembered in PowerMIDI.ini; it is not written into the saved files.
    public static readonly string[] VoiceMapNames = { "Auto", "MU Basic", "MU100 Native" };
    static readonly string[] VoiceMapKeys = { "Auto", "Basic", "Native" };

    public int VoiceMapIndex
    {
        get => ChannelMixer.VoiceMapOverride + 1;
        set
        {
            if (value < 0 || value >= VoiceMapNames.Length || value == VoiceMapIndex) return;
            Run(() =>
            {
                ChannelMixer.SetVoiceMap(output(), value - 1);
                setStatus(value == 0 ? "XG voice map: Auto (file choice)" : $"XG voice map: {VoiceMapNames[value]}");
                foreach (var ch in Channels) { ch.InstrumentKey = ""; ch.MapKey = ""; }
                Refresh();
            });
            OnPropertyChanged();
        }
    }

    // Value written in PowerMIDI.ini, and read from it at startup (nothing is sent: no track yet).
    public string VoiceMapSetting => VoiceMapKeys[VoiceMapIndex];
    public void LoadVoiceMap(string setting)
    {
        int index = Array.FindIndex(VoiceMapKeys, k => string.Equals(k, setting, StringComparison.OrdinalIgnoreCase));
        ChannelMixer.VoiceMapOverride = Math.Max(index, 0) - 1;
        OnPropertyChanged(nameof(VoiceMapIndex));
    }

    // Bank LSB of an XG capital voice as it sounds: LSB 0 follows the voice map, LSB 126 is the MU100 Native one
    // (written 0, as in the definition file), LSB 127 the MU Basic one.
    int EffectiveXgLsb(int msb, int lsb)
    {
        if (!HasXgBanks || msb != 0) return lsb;
        if (lsb == 0) return ChannelMixer.VoiceMap == ChannelMixer.VoiceMapBasic ? InstrumentDefinitions.XgBasicLsb : 0;
        return lsb == InstrumentDefinitions.XgNativeLsb ? 0 : lsb;
    }

    // MU50 map offered: the module plays the MU Basic voice map (and the file tells which voices the MU50 has).
    bool HasMu50Map => HasXgBanks && ChannelMixer.VoiceMap == ChannelMixer.VoiceMapBasic && defs.XgMu50Voices.Count > 0;

    // XG map of a voice (see InstrumentDefinitions.XgMapNames).
    int XgMapOfSound(int msb, int lsb, int program) =>
        HasMu50Map ? defs.XgMap(msb, lsb, program, true) : InstrumentDefinitions.XgMap(msb, lsb);

    // With the MU50 map (MU Basic voice map), the lists only offer what the MU50 has: the MU50 map, the SFX voices
    // and the drum kits it has. A voice of another map is still named, with no map selected.
    static bool IsMu50ModeMap(int map) => map is InstrumentDefinitions.XgMu50Map or XgSfxMap;
    const int XgSfxMap = 4;

    static bool IsXgCapital(int msb, int lsb) => msb == 0 && lsb is 0 or InstrumentDefinitions.XgNativeLsb or InstrumentDefinitions.XgBasicLsb;

    // MT-32 track whose SysEx redefine the patches / sounds: the instrument names may not match what is heard.
    bool hasCustomPatches;
    public bool HasCustomPatches { get => hasCustomPatches; set => Set(ref hasCustomPatches, value); }

    int shownController;
    public int ShownController
    {
        get => shownController;
        set
        {
            if (!Set(ref shownController, value)) return;
            OnPropertyChanged(nameof(ShownMenuIndex));
            foreach (var ch in Channels) RefreshController(ch);
        }
    }

    // Controller index of each item of the "Sliders" menu (Modulation first, then the ChannelMixer order).
    static readonly int[] MenuOrder = new[] { ChannelMixer.Modulation }.Concat(Enumerable.Range(0, ChannelMixer.Modulation)).ToArray();

    // Selected item of the "Sliders" menu.
    public int ShownMenuIndex
    {
        get => Array.IndexOf(MenuOrder, shownController);
        set { if (value >= 0 && value < MenuOrder.Length) ShownController = MenuOrder[value]; }
    }

    // Runs a user action; an error is shown instead of crashing.
    internal void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { showError(ex); }
    }

    // Updates a row without its setters calling the mixer back.
    static void Update(ChannelViewModel ch, Action action)
    {
        ch.Updating = true;
        try { action(); }
        finally { ch.Updating = false; }
    }

    // ---------- Display (main timer, 200 ms) ----------

    public void Refresh()
    {
        foreach (var ch in Channels)
        {
            RefreshController(ch);
            RefreshMap(ch);
            // Only updates the list if something changed, and never while it is open.
            if (!ch.IsListOpen) RefreshInstrument(ch, false);
        }
    }

    // Activity LEDs (fast timer, 30 ms): an LED stays lit for 100 ms after each Note On,
    // longer than the update interval, so that even a very short note is visible.
    // heldChannel: channel with a note held on the computer keyboard (lit meanwhile), -1 if none.
    public void UpdateLeds(bool playing, int heldChannel = -1)
    {
        long now = DateTime.UtcNow.Ticks;
        foreach (var ch in Channels)
            ch.IsActive = (playing && now - ChannelMixer.LastNoteOn[ch.Channel] < LedHoldTicks) || ch.Channel == heldChannel;
    }

    // Click on a channel number: keyboard mode (set by the main view model).
    public Action<int> KeyboardToggle { get; set; }

    // Text of a value: 0..127; pan: L64..C..R63 (64 = center);
    // envelope, filter and vibrato (index 5 to 12): relative to 64, from -64 to +63 (0 = original sound).
    public static string FormatControllerValue(int k, int value)
    {
        if (k >= 5 && k != ChannelMixer.Modulation)
        {
            int delta = value - 64;
            return delta > 0 ? $"+{delta}" : delta.ToString();
        }
        if (k != 1) return value.ToString();
        if (value == 64) return "C";
        return value < 64 ? $"L{64 - value}" : $"R{value - 64}";
    }

    // MT-32: its pan is reversed compared to GS / XG / GM (CC10 0 = right, 127 = left). The values sent and
    // saved stay the MT-32 ones; only the slider and the text are flipped, so that left stays on the left.
    bool IsReversedPan(int k) => k == 1 && Standard == "MT-32";

    // Slider position <-> MIDI value (the same, except for the MT-32 pan: 128 - value, 64 = center).
    int ToSlider(int k, int value) => IsReversedPan(k) ? Math.Min(127, 128 - value) : value;
    int FromSlider(int k, int position) => IsReversedPan(k) ? Math.Min(127, 128 - position) : position;

    // Text of a value of the displayed controller, as heard (MT-32 pan: 0 = R64).
    string FormatShownValue(int k, int value) =>
        !IsReversedPan(k) ? FormatControllerValue(k, value) : value == 64 ? "C" : value < 64 ? $"R{64 - value}" : $"L{value - 64}";

    // Slider and displayed value: fixed value (in blue) or last value from the file.
    void RefreshController(ChannelViewModel ch)
    {
        int k = shownController, c = ch.Channel;
        int value = ChannelMixer.Value(k, c);
        bool isFixed = ChannelMixer.IsFixed(k, c);
        int fileValue = ChannelMixer.FileValue(k, c);
        int position = ToSlider(k, value);
        var key = $"{k}/{value}/{isFixed}/{fileValue}/{IsReversedPan(k)}";
        if (key == ch.ShownKey && (int)Math.Round(ch.Value) == position) return;
        ch.ShownKey = key;

        var text = FormatShownValue(k, value);
        var name = ControllerNames[k];
        Update(ch, () =>
        {
            ch.Value = position;
            ch.ValueText = text;
            ch.IsFixed = isFixed;
            ch.SliderToolTip = isFixed ? $"{name} fixed at {text} (file: {FormatShownValue(k, fileValue)})" : $"{name} from the file: {text}";
        });
    }

    // Instrument of a channel: name only (closed list). force: even if nothing changed.
    internal void RefreshInstrument(ChannelViewModel ch, bool force = true)
    {
        int c = ch.Channel;
        if (ch.ListShown) { ch.ListShown = false; force = true; }   // list closed since it was last opened
        var key = $"{Standard}/{ChannelMixer.VoiceMap}/{ChannelMixer.Used[c]}/{ChannelMixer.Program[c]}/{ChannelMixer.BankMsb[c]}/{ChannelMixer.BankLsb[c]}/" +
                  $"{ChannelMixer.RhythmPart[c]}/{ChannelMixer.RhythmOverride[c]}/{ChannelMixer.Override[c]}/" +
                  $"{ChannelMixer.OverrideMsb[c]}/{ChannelMixer.OverrideLsb[c]}/{ChannelMixer.OverrideProgram[c]}/{GetSharedKit(c)}/{HasCustomPatches}";
        if (!force && key == ch.InstrumentKey) return;
        ch.InstrumentKey = key;

        var fileInfo = GetFileInstrument(c);
        var currentInfo = GetCurrentInstrument(c);
        // Kit set by another channel of the same drum part (the module plays it on this channel too).
        var shared = GetSharedKit(c);
        var ownSound = ChannelMixer.Override[c]
            ? (ChannelMixer.OverrideMsb[c], ChannelMixer.OverrideLsb[c], ChannelMixer.OverrideProgram[c])
            : (ChannelMixer.BankMsb[c], ChannelMixer.BankLsb[c], ChannelMixer.Program[c]);
        var sharedText = shared is { } kit && kit != ownSound ? $" · kit of drum part {ChannelMixer.Rhythm(c)}" : "";
        string name, tooltip;
        InstrumentState state;
        if (!ChannelMixer.Used[c])
        {
            (name, state, tooltip) = ("—", InstrumentState.Unused, "Channel unused so far");
        }
        else if (ChannelMixer.Override[c] || ChannelMixer.RhythmOverride[c] >= 0)
        {
            int part = ChannelMixer.RhythmOverride[c];
            var partText = part > 0 ? $" · drum part {part}" : part == 0 ? " · normal part" : "";
            (name, state, tooltip) = (currentInfo.Name, InstrumentState.Manual, $"{currentInfo.Detail}{partText}{sharedText} (file: {fileInfo.Name})");
        }
        else (name, state, tooltip) = (currentInfo.Name, InstrumentState.File, $"{currentInfo.Detail}{sharedText}");

        // MT-32 file that redefines its sounds: the programs no longer match the factory names.
        bool customPatch = Standard == "MT-32" && HasCustomPatches && ChannelMixer.Used[c] && !currentInfo.Drum;
        if (customPatch) tooltip += " · custom patches: the name may be wrong";

        var display = new InstrumentChoice { Text = name, IsDisplay = true };
        Update(ch, () =>
        {
            ch.IsDrum = currentInfo.Drum;
            ch.InstrumentItems = new[] { display };
            ch.SelectedInstrument = display;
            ch.InstrumentState = state;
            ch.IsInstrumentMissing = state != InstrumentState.Unused && (currentInfo.Missing || customPatch);
            ch.InstrumentToolTip = tooltip;
        });
    }

    // Is a channel playing drums right now? (Same rules as the instrument names; called for each note by the
    // playback thread when the pitch is shifted: only reads the mixer state.)
    public bool IsDrumChannel(int c) => Standard switch
    {
        "XG" => GetCurrentSound(c).Msb is 126 or 127,
        "GM2" => InstrumentDefinitions.IsGm2Drum(c, GetCurrentSound(c).Msb),
        "GS" or "None" or "MT-32" => ChannelMixer.Rhythm(c) > 0,
        _ => c == 9,
    };

    // Name of a sound on a channel, in the current standard (insertion descriptions).
    public string InstrumentName(int c, int msb, int lsb, int program) => ResolveSound(c, program, msb, lsb, ChannelMixer.Rhythm(c)).Name;

    // Instrument of a sound on a channel. XG: the capital voice follows the voice map, also when it is the fallback
    // of a variation the module does not have.
    InstrumentInfo ResolveSound(int c, int program, int msb, int lsb, int rhythm)
    {
        var info = defs.Resolve(Standard, c, program, msb, EffectiveXgLsb(msb, lsb), rhythm);
        if (info.Missing && !info.Drum && HasXgBanks && msb == 0 && ChannelMixer.VoiceMap == ChannelMixer.VoiceMapBasic)
            info = info with { Name = defs.Resolve(Standard, c, program, 0, InstrumentDefinitions.XgBasicLsb, rhythm).Name };
        return info;
    }

    // Instrument requested by the file on a channel (ignoring the manual choice).
    InstrumentInfo GetFileInstrument(int c) =>
        ResolveSound(c, ChannelMixer.Program[c], ChannelMixer.BankMsb[c], ChannelMixer.BankLsb[c], ChannelMixer.RhythmPart[c]);

    // Kit currently played by the GS drum part of a channel (the last one sent to any of its channels),
    // or null if the channel is not on a drum part or the kit is not known yet.
    (int Msb, int Lsb, int Program)? GetSharedKit(int c)
    {
        if (Standard is not ("GS" or "None")) return null;
        int part = ChannelMixer.Rhythm(c);
        if (part is not (1 or 2) || ChannelMixer.DrumKitProgram[part] < 0) return null;
        return (ChannelMixer.DrumKitMsb[part], ChannelMixer.DrumKitLsb[part], ChannelMixer.DrumKitProgram[part]);
    }

    // Current sound on a channel: on a drum part, the kit of the part; otherwise the manual choice, or the file's.
    (int Msb, int Lsb, int Program) GetCurrentSound(int c) => GetSharedKit(c) ?? (ChannelMixer.Override[c]
        ? (ChannelMixer.OverrideMsb[c], ChannelMixer.OverrideLsb[c], ChannelMixer.OverrideProgram[c])
        : (ChannelMixer.BankMsb[c], ChannelMixer.BankLsb[c], ChannelMixer.Program[c]));

    // Instrument actually played on a channel: current sound and drum part (manual choices included).
    InstrumentInfo GetCurrentInstrument(int c)
    {
        var (msb, lsb, program) = GetCurrentSound(c);
        return ResolveSound(c, program, msb, lsb, ChannelMixer.Rhythm(c));
    }

    // Sounds offered for a channel (drum kits on a drum part) and position of the current sound in that list (-1 if missing).
    (IReadOnlyList<InstrumentChoice> Choices, int Index, int Program, InstrumentInfo FileInfo) GetChannelChoices(int c)
    {
        var fileInfo = GetFileInstrument(c);
        var (msb, lsb, program) = GetCurrentSound(c);
        bool drum = GetCurrentInstrument(c).Drum;
        // XG: the list of the map of the sound; a capital voice is listed once per map (bank LSB 0), whatever
        // bank asked for it.
        int xgMap = HasXgBanks ? XgMapOfSound(msb, lsb, program) : 0;
        if (HasXgBanks && IsXgCapital(msb, lsb)) lsb = 0;
        if (HasMu50Map && !IsMu50ModeMap(xgMap)) xgMap = InstrumentDefinitions.XgMu50Map;
        var choices = defs.GetChoices(Standard, drum, HasXgBanks ? xgMap : lsb, HasMu50Map);
        // GM2: the list has MSB 120 (kits) / 121 (sounds); a file without these banks plays the same sounds.
        if (HasGm2Banks) (msb, lsb) = drum ? (InstrumentDefinitions.Gm2DrumMsb, 0)
                                           : (InstrumentDefinitions.Gm2MelodicMsb, msb == InstrumentDefinitions.Gm2MelodicMsb ? lsb : 0);
        // In GS, the LSB only selects the map: compare program and MSB only.
        bool compareLsb = Standard is "XG" or "GM2";
        int index = -1;
        for (int i = 0; i < choices.Count; i++)
        {
            var x = choices[i];
            if (x.Program == program && x.Msb == msb && (!compareLsb || x.Lsb == lsb)) { index = i; break; }
        }
        return (choices, index, program, fileInfo);
    }

    // Opening the list: back to the file's instrument, in GS the normal part and the two drum parts (in XG the
    // normal voices, drum kits and SFX kits; in GM2 the normal sounds and drum kits), then all the sounds (or kits)
    // of the current standard / map,
    // with the current one selected.
    internal void OpenInstrumentList(ChannelViewModel ch)
    {
        var (choices, index, _, fileInfo) = GetChannelChoices(ch.Channel);
        var items = new List<InstrumentChoice> { new() { Text = $"↺ File instrument ({fileInfo.Name})", IsReset = true } };
        if (HasGsMaps)
        {
            int rhythm = ChannelMixer.Rhythm(ch.Channel);
            foreach (int part in new[] { 0, 1, 2 })
            {
                var text = part == 0 ? "Assign to Normal part" : $"Assign to Drum part {part}";
                items.Add(new InstrumentChoice { Text = text + (rhythm == part ? "  ✓" : ""), AssignPart = part });
            }
        }
        else if (BankKinds is { } kinds)
        {
            int kind = CurrentBankKind(ch.Channel);
            foreach (var (bank, text) in kinds)
                items.Add(new InstrumentChoice { Text = text + (kind == bank ? "  ✓" : ""), AssignXgBank = bank });
        }
        items.AddRange(choices);
        Update(ch, () =>
        {
            ch.InstrumentItems = items;
            ch.SelectedInstrument = index >= 0 ? choices[index] : null;
        });
        ch.ListShown = true;
    }

    internal void SelectInstrument(int c, InstrumentChoice item)
    {
        if (item == null || item.IsDisplay) return;
        if (item.IsReset)
        {
            // Back to the file: its instrument and its drum part.
            ChannelMixer.ClearOverride(output(), c);
            if (ChannelMixer.RhythmOverride[c] >= 0) ChannelMixer.ClearRhythmOverride(output(), c);
            setStatus($"Channel {c + 1}: file instrument");
        }
        else if (item.AssignPart is int part)
        {
            // Joining a drum part whose kit was chosen by hand on another channel: same kit (the module
            // has a single kit per drum part), instead of changing it for the whole part.
            int source = part > 0 ? DrumPartChannels(part).FirstOrDefault(o => o != c && ChannelMixer.Override[o], -1) : -1;
            if (source >= 0)
                ChannelMixer.SetOverride(null, c, ChannelMixer.OverrideMsb[source], ChannelMixer.OverrideLsb[source], ChannelMixer.OverrideProgram[source]);
            ChannelMixer.SetRhythmOverride(output(), c, part);
            setStatus(part == 0 ? $"Channel {c + 1}: normal part — choose its sound in the list"
                    : source >= 0 ? $"Channel {c + 1}: drum part {part}, with its kit (chosen on channel {source + 1})"
                    : $"Channel {c + 1}: drum part {part} — choose its drum kit in the list");
        }
        else if (item.AssignXgBank is int bank)
        {
            SelectBankKind(c, bank);
        }
        else
        {
            // A drum kit is shared by all the channels of a drum part: the choice applies to each of them.
            int drumPart = HasGsMaps ? ChannelMixer.Rhythm(c) : 0;
            var targets = drumPart > 0 ? DrumPartChannels(drumPart) : new List<int> { c };
            int sentLsb = item.Lsb;
            foreach (int t in targets) ChannelMixer.SetOverride(output(), t, item.Msb, sentLsb, item.Program);
            var others = targets.Where(t => t != c).Select(t => (t + 1).ToString()).ToList();
            setStatus($"Channel {c + 1}: {item.Text.Trim(' ', '·').Trim()} (MSB {item.Msb} / LSB {sentLsb} / PC {item.Program + 1})" +
                      (others.Count > 0 ? $" — also on channel {string.Join(", ", others)} (drum part {drumPart})" : ""));
            foreach (int t in targets.Where(t => t != c)) RefreshInstrument(Channels[t]);
        }
        Channels[c].InstrumentKey = "";   // refresh the name (now, or when the list closes)
        if (!Channels[c].IsListOpen) RefreshInstrument(Channels[c]);
    }

    // ---------- XG and GM2: normal sounds, drum kits (XG: SFX kits) ----------
    // The bank makes a channel a drum channel. XG: MSB 127 = drum kits, 126 = SFX kits, other = voices.
    // GM2: MSB 120 = drum kits, 121 = melodic sounds (without either, channel 10 = drums).
    // Each channel has its own kit (no sharing as in GS).

    bool HasXgBanks => Standard == "XG" && defs.XgVoices.Count > 0;
    bool HasGm2Banks => Standard == "GM2" && defs.Gm2Voices.Count > 0;

    static readonly (int Bank, string Text)[] XgKinds = { (0, "Assign to Normal part"), (127, "Assign to Drum kit"), (126, "Assign to SFX kit") };
    static readonly (int Bank, string Text)[] Gm2Kinds =
        { (InstrumentDefinitions.Gm2MelodicMsb, "Assign to Normal part"), (InstrumentDefinitions.Gm2DrumMsb, "Assign to Drum kit") };

    // "Assign to …" entries of the current standard (null: none, or GS parts).
    (int Bank, string Text)[] BankKinds => HasXgBanks ? XgKinds : HasGm2Banks ? Gm2Kinds : null;

    // Bank kind (the MSB of its "Assign to …" entry) of a sound played on a channel.
    int BankKind(int c, int msb) => HasGm2Banks
        ? (InstrumentDefinitions.IsGm2Drum(c, msb) ? InstrumentDefinitions.Gm2DrumMsb : InstrumentDefinitions.Gm2MelodicMsb)
        : msb is 126 or 127 ? msb : 0;
    int CurrentBankKind(int c) => BankKind(c, GetCurrentSound(c).Msb);

    static string KindName(int bank) => bank switch { 127 or InstrumentDefinitions.Gm2DrumMsb => "drum kit", 126 => "SFX kit", _ => "normal part" };

    // Moves a channel to normal sounds, drum kits or (XG) SFX kits. Already there: nothing changes. If the file's
    // own instrument is of that kind, back to it; otherwise the first one of the kind (the first piano, the
    // Standard Kit, the first SFX kit), to be changed in the list.
    void SelectBankKind(int c, int bank)
    {
        if (CurrentBankKind(c) == bank)
        {
            setStatus($"Channel {c + 1}: already on {KindName(bank)}");
            return;
        }
        if (BankKind(c, ChannelMixer.BankMsb[c]) == bank)
        {
            ChannelMixer.ClearOverride(output(), c);
            setStatus($"Channel {c + 1}: {KindName(bank)} — the file's instrument");
            return;
        }
        ChannelMixer.SetOverride(output(), c, bank, 0, 0);
        setStatus($"Channel {c + 1}: {KindName(bank)} — choose it in the list");
    }

    // Channels currently assigned to a GS drum part (1 or 2).
    static List<int> DrumPartChannels(int part) => Enumerable.Range(0, 16).Where(i => ChannelMixer.Rhythm(i) == part).ToList();

    // Wheel on the closed list: next / previous sound (with Ctrl: next / previous program; with Shift: map).
    internal void StepInstrumentOrMap(int c, WheelStep step)
    {
        if (step.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) StepMap(c, step.Direction);
        else StepInstrument(c, step.Direction, step.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control));
    }

    // Next (+1) or previous (-1) sound. With byProgram, skips the variations and moves to the next / previous program.
    void StepInstrument(int c, int direction, bool byProgram)
    {
        var (choices, currentIndex, currentProgram, _) = GetChannelChoices(c);
        if (choices.Count == 0) return;
        int index = currentIndex;
        if (index < 0)
        {
            // Current sound missing from the list: start from the first sound with the same program (or the next one).
            index = 0;
            while (index < choices.Count - 1 && choices[index].Program < currentProgram) index++;
            if (direction > 0) index--;
        }

        int target;
        if (byProgram)
        {
            int program = choices[Math.Max(index, 0)].Program;
            target = index;
            do { target += direction; } while (target >= 0 && target < choices.Count && choices[target].Program == program);
            if (target < 0 || target >= choices.Count) return;
            // Previous program: go to its first sound (the base sound), not its last variation.
            program = choices[target].Program;
            while (direction < 0 && target > 0 && choices[target - 1].Program == program) target--;
        }
        else target = Math.Max(0, Math.Min(choices.Count - 1, index + direction));
        if (target == currentIndex) return;
        SelectInstrument(c, choices[target]);
        Refresh();
    }

    // ---------- Maps: Sound Canvas maps in GS, MU2000 maps in XG ----------

    // GS maps in GS (and None, played as GS by the SC-8850).
    bool HasGsMaps => Standard is "GS" or "None" && defs.GsVoices.Count > 0;

    // XG map of the sound of a channel; 0 on a drum kit (the maps are for the voices).
    int XgMapOf(int c)
    {
        var (msb, lsb, program) = GetCurrentSound(c);
        return msb is 126 or 127 ? 0 : XgMapOfSound(msb, lsb, program);
    }

    static int MapFromLsb(int lsb) => lsb >= 1 && lsb <= 4 ? lsb : InstrumentDefinitions.DefaultGsMap;

    // Map selector of a channel: map of the current sound; disabled outside GS and XG, and on an XG drum kit.
    void RefreshMap(ChannelViewModel ch)
    {
        bool gs = HasGsMaps, xg = HasXgBanks;
        int map = gs ? MapFromLsb(GetCurrentSound(ch.Channel).Lsb) : xg ? XgMapOf(ch.Channel) : 0;
        var key = $"{gs}/{xg}/{map}/{ChannelMixer.VoiceMap}";
        if (key == ch.MapKey) return;
        ch.MapKey = key;
        var items = HasMu50Map ? XgMapChoicesMu50 : xg ? XgMapChoices : GsMapChoices;
        Update(ch, () =>
        {
            ch.MapItems = items;
            ch.MapEnabled = map > 0;
            ch.SelectedMap = items.FirstOrDefault(m => m.Map == map);
            ch.MapToolTip = gs ? $"Map of channel {ch.Channel + 1}: {InstrumentDefinitions.GsMapNames[map]}"
                          : xg ? (map > 0 ? $"Map of channel {ch.Channel + 1}: {InstrumentDefinitions.XgMapNames[map]}" : "Maps: not for drum kits")
                          : "Maps: GS and XG only";
        });
    }

    // Next / previous map: the same sound in GS (SC-55 → SC-88 → SC-88Pro → SC-8850); in XG, see SetXgMap.
    internal void StepMap(int c, int direction)
    {
        if (HasGsMaps)
        {
            int target = MapFromLsb(GetCurrentSound(c).Lsb) + direction;
            if (target >= 1 && target <= 4) SetChannelMap(c, target);
        }
        else if (HasXgBanks)
        {
            // In the order of the list.
            var items = Channels[c].MapItems;
            int current = XgMapOf(c), index = current > 0 ? items.ToList().FindIndex(m => m.Map == current) : -1;
            if (index >= 0 && index + direction >= 0 && index + direction < items.Count) SetChannelMap(c, items[index + direction].Map);
        }
    }

    // XG: moves a channel to another map of the MU2000.
    // - From a voice of bank MSB 0 the MU50 does not have to the MU50 map: the capital voice of the same program
    //   (plain bank LSB 0).
    // - To another family (ModEx, SFX, Sampl, GM2, or back to the voices): the file's own sound if it is of that
    //   family, otherwise its first sound, to be changed in the list.
    void SetXgMap(int c, int target)
    {
        var (_, _, program) = GetCurrentSound(c);
        int current = XgMapOf(c);
        if (current == 0 || target == current) return;
        var name = InstrumentDefinitions.XgMapNames[target];
        if (current == InstrumentDefinitions.XgNativeMap && target == InstrumentDefinitions.XgMu50Map)
        {
            ChannelMixer.SetOverride(output(), c, 0, 0, program);
            setStatus($"Channel {c + 1}: map {name} — {GetCurrentInstrument(c).Name}");
        }
        else if (!(ChannelMixer.BankMsb[c] is 126 or 127) && XgMapOfSound(ChannelMixer.BankMsb[c], ChannelMixer.BankLsb[c], ChannelMixer.Program[c]) == target)
        {
            ChannelMixer.ClearOverride(output(), c);
            setStatus($"Channel {c + 1}: map {name} — file instrument");
        }
        else
        {
            var first = defs.GetChoices("XG", false, target, HasMu50Map).FirstOrDefault();
            if (first == null) return;
            ChannelMixer.SetOverride(output(), c, first.Msb, first.Lsb, first.Program);
            setStatus($"Channel {c + 1}: map {name} — choose sound in list");
        }
        Channels[c].InstrumentKey = "";
    }

    // Moves a channel to another map. GS: the same sound (program and variation) in that map; if the variation
    // does not exist there: base sound of the same family, then the capital tone. XG: see SetXgMap.
    internal void SetChannelMap(int c, int target)
    {
        var ch = Channels[c];
        ch.MapKey = "";   // resync the selector (e.g. map without this kit)
        if (HasGsMaps)
        {
            var (msb, lsb, program) = GetCurrentSound(c);
            if (target != MapFromLsb(lsb))
            {
                bool drum = GetCurrentInstrument(c).Drum;
                var choices = defs.GetChoices("GS", drum, target);
                InstrumentChoice found = null;
                if (drum) found = choices.FirstOrDefault(x => x.Program == program) ?? choices.FirstOrDefault();
                else
                    foreach (var m in new[] { msb, msb - msb % 8, 0 })
                    {
                        found = choices.FirstOrDefault(x => x.Program == program && x.Msb == m);
                        if (found != null) break;
                    }
                if (found != null) SelectInstrument(c, found);
            }
        }
        else if (HasXgBanks) SetXgMap(c, target);
        Refresh();
    }

    // ---------- Controllers ----------

    // Moved by the user: the value of the displayed controller is fixed, the file no longer changes it.
    internal void SetController(int c, int position)
    {
        ChannelMixer.SetController(output(), shownController, c, FromSlider(shownController, position));
        RefreshController(Channels[c]);
    }

    // Right click: back to the file value.
    internal void ClearController(int c)
    {
        ChannelMixer.ClearController(output(), shownController, c);
        RefreshController(Channels[c]);
    }

    // ---------- Mute / solo ----------

    // Mutes or unmutes a channel and sends its volume (0 if muted) to the module if send.
    internal void SetMute(int c, bool muted, bool send = true)
    {
        var ch = Channels[c];
        Update(ch, () => ch.IsMuted = muted);
        ChannelMixer.Muted[c] = muted;
        var device = output();
        if (send && device != null) ChannelMixer.Apply(device, c);
        UpdateSolo();
    }

    // Soloed channel: the only unmuted channel when the other 15 are muted; -1 otherwise.
    static int GetSoloChannel()
    {
        var open = Enumerable.Range(0, 16).Where(i => !ChannelMixer.Muted[i]).ToList();
        return open.Count == 1 ? open[0] : -1;
    }

    // Solo on a channel: mutes all the others. If it is already soloed, unmutes all channels.
    internal void ToggleSolo(int c)
    {
        bool alreadySolo = GetSoloChannel() == c;
        for (int i = 0; i < 16; i++)
        {
            bool muted = !alreadySolo && i != c;
            if (ChannelMixer.Muted[i] != muted) SetMute(i, muted);
        }
        setStatus(alreadySolo ? "Solo cancelled: all channels unmuted" : $"Solo on channel {c + 1}");
    }

    // The soloed channel shows "S" in green; the others "M".
    void UpdateSolo()
    {
        int solo = GetSoloChannel();
        foreach (var ch in Channels) ch.IsSolo = ch.Channel == solo;
    }

    // ---------- Reset ----------

    // Reset button: file controllers, instruments and drum parts, no muted channel.
    public void Reset()
    {
        var device = output();
        foreach (var ch in Channels)
        {
            for (int k = 0; k < ControllerCount; k++) ChannelMixer.ClearController(device, k, ch.Channel);
            RefreshController(ch);
        }
        foreach (var ch in Channels)
            if (ChannelMixer.Muted[ch.Channel]) SetMute(ch.Channel, false);
        foreach (var ch in Channels)
        {
            if (ChannelMixer.Override[ch.Channel]) ChannelMixer.ClearOverride(device, ch.Channel);
            if (ChannelMixer.RhythmOverride[ch.Channel] >= 0) ChannelMixer.ClearRhythmOverride(device, ch.Channel);
        }
        Refresh();
    }

    // Clears the manual mixer settings (instruments, drum parts, fixed controllers, muted channels) without
    // sending anything to the module: a reset follows at the start of the next track. Returns true if there were any.
    public bool ClearManualSettings()
    {
        bool had = false;
        foreach (var ch in Channels)
        {
            int c = ch.Channel;
            if (ChannelMixer.Override[c]) { ChannelMixer.ClearOverride(null, c); had = true; }
            if (ChannelMixer.RhythmOverride[c] >= 0) { ChannelMixer.ClearRhythmOverride(null, c); had = true; }
            for (int k = 0; k < ControllerCount; k++)
                if (ChannelMixer.IsFixed(k, c)) { ChannelMixer.ClearController(null, k, c); had = true; }
            if (ChannelMixer.Muted[c]) { SetMute(c, false, false); had = true; }
        }
        return had;
    }
}
