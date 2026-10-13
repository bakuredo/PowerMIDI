using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Melanchall.DryWetMidi.Multimedia;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// Effects tab: GS Reverb / Chorus / Delay / EQ / EFX, XG Reverb / Chorus / Variation.
// Index = the one in GsXgEffects.Addresses. Blocks 0-2: GS (SysEx 40 01 xx); blocks 3-5: XG (02 01 xx);
// block 6: GS EFX of the SC-88Pro / SC-8850 (40 03 xx, and 40 4x 22 for the EFX on each channel);
// block 7: GS EQ of the SC-88 and later (40 02 xx, and 40 4x 20 for the EQ on each channel).
// The values live in GsXgEffects, Mt32Effects and Gm2Effects (shared with the playback thread); this view model shows them and fixes
// the ones the user changes.
public sealed class EffectsViewModel : ObservableObject
{
    public static int EffectCount => GsXgEffects.Addresses.Length;
    const int EfxTypeParam = 42;      // 42: EFX type; 43-65: its parameters and sends
    const int EfxChannelFirst = 66;   // 66-81: EFX on channels 1 to 16
    const int VarTypeParam = 35;      // 35: XG Variation type
    const int VarParamFirst = 82;     // 82-97: XG Variation parameters 1 to 16
    const int EqFirst = 126;          // 126-129: GS EQ low frequency, low gain, high frequency, high gain
    const int EqChannelFirst = 130;   // 130-145: GS EQ on channels 1 to 16

    static readonly string[] BlockNames = { "Reverb", "Chorus", "Delay", "Reverb", "Chorus", "Variation", "EFX (SC-88Pro / SC-8850)",
                                            "EQ (SC-88 and later)" };
    static readonly string[] Labels = new[]
        {
            "Type", "Character", "Pre-LPF", "Level", "Time", "Delay feedback", "Pre-delay",                 // GS Reverb
            "Type", "Pre-LPF", "Level", "Feedback", "Delay", "Rate", "Depth", "To reverb", "To delay",      // GS Chorus
            "Type", "Pre-LPF", "Time", "Left ratio", "Right ratio", "Level", "Feedback", "To reverb",       // GS Delay
            "Type", "Return", "Pan", "Time",                                                              // XG Reverb
            "Type", "Return", "Pan", "To reverb", "Rate", "Depth", "Feedback",                            // XG Chorus
            "Type", "Return", "Pan", "To reverb", "To chorus", "Connection", "Part",                      // XG Variation
            "Type",                                                                                       // EFX
        }
        .Concat(Enumerable.Range(1, 20).Select(i => $"Param. {i}")).Concat(new[] { "To reverb", "To chorus", "To delay" })
        .Concat(Enumerable.Range(1, 16).Select(i => $"Channel {i}"))   // EFX per channel
        .Concat(Enumerable.Range(1, 16).Select(i => $"Param. {i}"))    // XG Variation: type parameters
        .Concat(Enumerable.Range(2, 15).Select(i => $"Param. {i}"))    // XG Reverb (parameter 1 = "Time")
        .Concat(Enumerable.Range(4, 13).Select(i => $"Param. {i}"))    // XG Chorus (1-3 = rate, depth, feedback)
        .Concat(new[] { "Low freq", "Low gain", "High freq", "High gain" })   // GS EQ
        .Concat(Enumerable.Range(1, 16).Select(i => $"Channel {i}"))   // GS EQ per channel
        .ToArray();

    static readonly int[] PanParams = { 26, 30, 37 };   // XG pan: L63..C..R63
    static readonly int[] SignedParams = { 22, 34 };    // GS Delay feedback, XG Chorus feedback: -64..+63
    static readonly int[] EqGainParams = { EqFirst + 1, EqFirst + 3 };   // GS EQ gains: 52..76 = -12..+12 dB

    // Channel buttons of a block: EFX (66-81) or EQ (130-145) on channels 1 to 16; -1: not a channel button.
    static int ButtonChannel(int fx) => fx >= EqChannelFirst ? fx - EqChannelFirst
                                      : fx >= EfxChannelFirst && fx < VarParamFirst ? fx - EfxChannelFirst : -1;

    static int Xg(int msb, int lsb) => msb * 128 + lsb;

    // Choice lists. GS types: value = type number; XG types: MSB × 128 + LSB.
    static List<EffectChoice> Choices(params object[] pairs)
    {
        var list = new List<EffectChoice>();
        for (int i = 0; i < pairs.Length; i += 2) list.Add(new EffectChoice((string)pairs[i], (int)pairs[i + 1]));
        return list;
    }
    static List<EffectChoice> IndexedChoices(params string[] names) => names.Select((n, i) => new EffectChoice(n, i)).ToList();

    static readonly object[] XgChorusTypes =
    {
        "Chorus 1", Xg(65, 0), "Chorus 2", Xg(65, 1), "Chorus 3", Xg(65, 2), "Chorus 4", Xg(65, 8),
        "Celeste 1", Xg(66, 0), "Celeste 2", Xg(66, 1), "Celeste 3", Xg(66, 2), "Celeste 4", Xg(66, 8),
        "Flanger 1", Xg(67, 0), "Flanger 2", Xg(67, 1), "Flanger 3", Xg(67, 8),
    };
    static readonly object[] XgReverbTypes =
    {
        "Hall 1", Xg(1, 0), "Hall 2", Xg(1, 1), "Room 1", Xg(2, 0), "Room 2", Xg(2, 1), "Room 3", Xg(2, 2),
        "Stage 1", Xg(3, 0), "Stage 2", Xg(3, 1), "Plate", Xg(4, 0), "White Room", Xg(16, 0),
        "Tunnel", Xg(17, 0), "Basement", Xg(19, 0),
    };

    readonly Dictionary<int, List<EffectChoice>> choices = new()
    {
        [0] = IndexedChoices("Room 1", "Room 2", "Room 3", "Hall 1", "Hall 2", "Plate", "Delay", "Panning Delay"),
        [7] = IndexedChoices("Chorus 1", "Chorus 2", "Chorus 3", "Chorus 4", "Feedback Chorus", "Flanger", "Short Delay", "Short Delay (FB)"),
        [16] = IndexedChoices("Delay 1", "Delay 2", "Delay 3", "Delay 4", "Pan Delay 1", "Pan Delay 2", "Pan Delay 3",
                              "Pan Delay 4", "Delay to Reverb", "Pan Repeat"),
        [24] = Choices(new object[] { "Off", 0 }.Concat(XgReverbTypes).ToArray()),
        [28] = Choices(new object[] { "Off", 0 }.Concat(XgChorusTypes).ToArray()),
        [VarTypeParam] = Choices(new object[] { "Off", 0 }.Concat(XgReverbTypes).Concat(new object[]
        {
            "Delay L,C,R", Xg(5, 0), "Delay L,R", Xg(6, 0), "Echo", Xg(7, 0), "Cross Delay", Xg(8, 0),
            "Early Ref. 1", Xg(9, 0), "Early Ref. 2", Xg(9, 1), "Gate Reverb", Xg(10, 0), "Reverse Gate", Xg(11, 0),
            "Karaoke 1", Xg(20, 0), "Karaoke 2", Xg(20, 1), "Karaoke 3", Xg(20, 2), "Thru", Xg(64, 0),
        }).Concat(XgChorusTypes).Concat(new object[]
        {
            "Symphonic", Xg(68, 0), "Rotary Speaker", Xg(69, 0), "Tremolo", Xg(70, 0), "Auto Pan", Xg(71, 0),
            "Phaser 1", Xg(72, 0), "Phaser 2", Xg(72, 8), "Distortion", Xg(73, 0), "Overdrive", Xg(74, 0),
            "Amp Simulator", Xg(75, 0), "3-Band EQ", Xg(76, 0), "2-Band EQ", Xg(77, 0), "Auto Wah", Xg(78, 0),
        }).ToArray()),
        [40] = Choices("Insertion", 0, "System", 1),
        [41] = Enumerable.Range(0, 16).Select(p => new EffectChoice($"Part {p + 1}", p)).Append(new EffectChoice("Off", 127)).ToList(),
        [EqFirst] = IndexedChoices("200 Hz", "400 Hz"),
        [EqFirst + 2] = IndexedChoices("3 kHz", "6 kHz"),
        // SC-88Pro EFX types (MSB × 128 + LSB), replaced by the Domino list when available; a type missing
        // from the list is shown as a code (e.g. 02:0D).
        [EfxTypeParam] = Choices(
            "Thru", Xg(0, 0),
            "Stereo-EQ", Xg(1, 0x00), "Spectrum", Xg(1, 0x01), "Enhancer", Xg(1, 0x02), "Humanizer", Xg(1, 0x03),
            "Overdrive", Xg(1, 0x10), "Distortion", Xg(1, 0x11), "Phaser", Xg(1, 0x20), "Auto Wah", Xg(1, 0x21),
            "Rotary", Xg(1, 0x22), "Stereo Flanger", Xg(1, 0x23), "Step Flanger", Xg(1, 0x24), "Tremolo", Xg(1, 0x25),
            "Auto Pan", Xg(1, 0x26), "Compressor", Xg(1, 0x30), "Limiter", Xg(1, 0x31), "Hexa Chorus", Xg(1, 0x40),
            "Tremolo Chorus", Xg(1, 0x41), "Stereo Chorus", Xg(1, 0x42), "Space D", Xg(1, 0x43), "3D Chorus", Xg(1, 0x44),
            "Stereo Delay", Xg(1, 0x50), "Mod Delay", Xg(1, 0x51), "3 Tap Delay", Xg(1, 0x52), "4 Tap Delay", Xg(1, 0x53),
            "Tm Ctrl Delay", Xg(1, 0x54), "Reverb", Xg(1, 0x55), "Gate Reverb", Xg(1, 0x56), "3D Delay", Xg(1, 0x57),
            "2 Pitch Shifter", Xg(1, 0x60), "Fb P.Shifter", Xg(1, 0x61), "3D Auto", Xg(1, 0x70), "3D Manual", Xg(1, 0x71),
            "Lo-Fi 1", Xg(1, 0x72), "Lo-Fi 2", Xg(1, 0x73),
            "OD → Chorus", Xg(2, 0x00), "OD → Flanger", Xg(2, 0x01), "OD → Delay", Xg(2, 0x02), "DS → Chorus", Xg(2, 0x03),
            "DS → Flanger", Xg(2, 0x04), "DS → Delay", Xg(2, 0x05), "EH → Chorus", Xg(2, 0x06), "EH → Flanger", Xg(2, 0x07),
            "EH → Delay", Xg(2, 0x08), "Cho → Delay", Xg(2, 0x09), "FL → Delay", Xg(2, 0x0A), "Cho → Flanger", Xg(2, 0x0B),
            "Rotary Multi", Xg(2, 0x0C),
            "Cho / Delay", Xg(0x11, 0x00), "FL / Delay", Xg(0x11, 0x01), "Cho / Flanger", Xg(0x11, 0x02), "OD1 / OD2", Xg(0x11, 0x03),
            "OD / Rotary", Xg(0x11, 0x04), "OD / Phaser", Xg(0x11, 0x05), "OD / AutoWah", Xg(0x11, 0x06), "PH / Rotary", Xg(0x11, 0x07),
            "PH / AutoWah", Xg(0x11, 0x08)),
    };

    readonly InstrumentDefinitions defs;
    readonly Func<IOutputDevice> output;   // current MIDI port (null if none)
    readonly Action<Exception> showError;
    readonly EffectParamViewModel[] parameters = new EffectParamViewModel[EffectCount];
    readonly List<EffectBlockViewModel> gsBlocks = new(), xgBlocks = new(), mt32Blocks = new();

    // MT-32 page: parameter p of Mt32Effects has the index Mt32Base + p (the GS / XG ones are 0..145).
    const int Mt32Base = 1000;
    readonly EffectParamViewModel[] mt32Params = new EffectParamViewModel[Mt32Effects.Count];

    // GM2 page: parameter p of Gm2Effects has the index Gm2Base + p.
    const int Gm2Base = 2000;
    readonly EffectParamViewModel[] gm2Params = new EffectParamViewModel[Gm2Effects.Count];
    readonly List<EffectBlockViewModel> gm2Blocks = new();
    static readonly List<EffectChoice> Gm2ReverbTypes = Choices("Small Room", 0, "Medium Room", 1, "Large Room", 2,
                                                                "Medium Hall", 3, "Large Hall", 4, "Plate", 8);
    static readonly List<EffectChoice> Gm2ChorusTypes = IndexedChoices("Chorus 1", "Chorus 2", "Chorus 3", "Chorus 4", "FB Chorus", "Flanger");
    static readonly string[] Gm2Labels = { "Type", "Time", "Type", "Rate", "Depth", "Feedback", "To reverb" };

    // Value of a GM2 parameter in its unit (formulas of the GM2 specification).
    static string FormatGm2(int p, int v) => v < 0 ? "auto" : p switch
    {
        1 => string.Format(CultureInfo.InvariantCulture, "{0:0.00} s", Math.Exp((v - 40) * 0.025)),
        3 => string.Format(CultureInfo.InvariantCulture, "{0:0.00} Hz", v * 0.122),
        4 => string.Format(CultureInfo.InvariantCulture, "{0:0.0} ms", (v + 1) / 3.2),
        5 => string.Format(CultureInfo.InvariantCulture, "{0:0} %", v * 0.763),
        6 => string.Format(CultureInfo.InvariantCulture, "{0:0} %", v * 0.787),
        _ => (p == Gm2Effects.ReverbType ? Gm2ReverbTypes : Gm2ChorusTypes).FirstOrDefault(x => x.Value == v)?.Text ?? v.ToString(),
    };

    public EffectsViewModel(InstrumentDefinitions defs, Func<IOutputDevice> output, Action<Exception> showError)
    {
        this.defs = defs;
        this.output = output;
        this.showError = showError;
        ResetCommand = new RelayCommand(() => Run(Reset));

        // EFX types: list read from the Domino definitions (64 types, exact names) when present.
        if (defs.EfxTypes.Count > 0)
            choices[EfxTypeParam] = new[] { new EffectChoice("Thru", 0) }
                .Concat(defs.EfxTypes.OrderBy(kv => kv.Key).Select(kv => new EffectChoice(kv.Value.Name, kv.Key))).ToList();

        // XG types: those of the definition file (MU2000: 19 reverbs, 21 choruses, 118 variations) when present;
        // the lists above are only what an XG Level 1 module has.
        static List<EffectChoice> TypeChoices(Dictionary<int, EffectTypeDef> types) =>
            types.OrderBy(kv => kv.Key).Select(kv => new EffectChoice(kv.Key == 0 ? "Off" : kv.Value.Name, kv.Key)).ToList();
        if (defs.XgReverbTypes.Count > 0) choices[24] = TypeChoices(defs.XgReverbTypes);
        if (defs.XgChorusTypes.Count > 0) choices[28] = TypeChoices(defs.XgChorusTypes);
        if (defs.XgVariationTypes.Count > 0) choices[VarTypeParam] = TypeChoices(defs.XgVariationTypes);

        // Display order: GS (Reverb, Chorus, Delay, EQ, EFX) then XG (Reverb, Chorus, Variation); within each block,
        // by index (type first). Parameters added at the end of the list thus land in their block.
        var blockOrder = new[] { 0, 1, 2, 7, 6, 3, 4, 5 };
        var blocks = new Dictionary<int, EffectBlockViewModel>();
        foreach (int fx in Enumerable.Range(0, EffectCount).OrderBy(fx => Array.IndexOf(blockOrder, GsXgEffects.Block[fx])).ThenBy(fx => fx))
        {
            int b = GsXgEffects.Block[fx];
            var group = GsXgEffects.IsXg(fx) ? xgBlocks : gsBlocks;
            if (!blocks.TryGetValue(b, out var block))
            {
                block = new EffectBlockViewModel { Title = BlockNames[b], IsFirst = group.Count == 0, ChannelsLabel = b == 7 ? "EQ on channels" : "Channels" };
                blocks[b] = block;
                group.Add(block);
            }
            if (ButtonChannel(fx) >= 0)
            {
                var channel = new EffectChannelViewModel(this, fx, ButtonChannel(fx));
                block.Channels.Add(channel);
                parameters[fx] = channel;
                continue;
            }
            EffectParamViewModel p = choices.TryGetValue(fx, out var list) ? new EffectListViewModel(this, fx, list)
                                   : new EffectSliderViewModel(this, fx) { Minimum = GsXgEffects.Min[fx], Maximum = GsXgEffects.Max[fx] };
            p.Label = Labels[fx];
            block.Params.Add(p);
            parameters[fx] = p;
        }

        // MT-32 page: the reverb (mode, time, level) and the reverb switch of parts 1-8 and of the rhythm part.
        var reverb = new EffectBlockViewModel { Title = "Reverb", IsFirst = true, ChannelsLabel = "Reverb per part" };
        mt32Params[0] = new EffectListViewModel(this, Mt32Base, Mt32Effects.ModeNames.Select((n, i) => new EffectChoice(n, i)).ToList()) { Label = "Mode" };
        mt32Params[1] = new EffectSliderViewModel(this, Mt32Base + 1) { Label = "Time", Minimum = 0, Maximum = 7 };
        mt32Params[2] = new EffectSliderViewModel(this, Mt32Base + 2) { Label = "Level", Minimum = 0, Maximum = 7 };
        reverb.Params.AddRange(mt32Params.Take(3));
        for (int part = 0; part < 9; part++)
        {
            var button = new EffectChannelViewModel(this, Mt32Base + Mt32Effects.PartFirst + part, part < 8 ? (part + 1).ToString() : "R");
            mt32Params[Mt32Effects.PartFirst + part] = button;
            reverb.Channels.Add(button);
        }
        mt32Blocks.Add(reverb);

        // GM2 page: Reverb (type, time) and Chorus (type, rate, depth, feedback, send to reverb).
        for (int p = 0; p < Gm2Effects.Count; p++)
            gm2Params[p] = Gm2Effects.IsType(p)
                ? new EffectListViewModel(this, Gm2Base + p, p == Gm2Effects.ReverbType ? Gm2ReverbTypes : Gm2ChorusTypes) { Label = Gm2Labels[p] }
                : new EffectSliderViewModel(this, Gm2Base + p) { Label = Gm2Labels[p], Minimum = 0, Maximum = 127 };
        var gm2Reverb = new EffectBlockViewModel { Title = "Reverb", IsFirst = true };
        var gm2Chorus = new EffectBlockViewModel { Title = "Chorus" };
        gm2Reverb.Params.AddRange(gm2Params.Take(Gm2Effects.ChorusType));
        gm2Chorus.Params.AddRange(gm2Params.Skip(Gm2Effects.ChorusType));
        gm2Blocks.Add(gm2Reverb);
        gm2Blocks.Add(gm2Chorus);

        // Side by side, Reverb and Chorus both start at the top (same title margin).
        foreach (var group in new[] { gsBlocks, xgBlocks, gm2Blocks })
            if (group.Count >= 2) group[1].IsFirst = true;
    }

    public ICommand ResetCommand { get; }

    // Standard of the current track (no track: the default one); it chooses the page of effects shown.
    string standard = MidiTools.DefaultStandard;
    public string Standard
    {
        get => standard;
        set
        {
            if (!Set(ref standard, value)) return;
            OnPropertyChanged(nameof(Blocks));
            OnPropertyChanged(nameof(SideRows));
            OnPropertyChanged(nameof(OtherBlocks));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(InfoText));
        }
    }

    // GS effects in GS and None; MT-32 reverb in MT-32 (MT-32 SysEx, for a real MT-32 / CM-32L); XG effects in XG;
    // GM2 reverb and chorus in GM2 (universal SysEx).
    string Mode => standard switch { "XG" => "XG", "MT-32" => "MT-32", "GM2" => "GM2", "GS" or "None" => "GS", _ => "" };
    public bool IsAvailable => Mode != "";
    // Blocks of the current standard; none in a standard without effects (GM): showing the GS ones grayed out looked
    // as if the tab had not followed the mode.
    static readonly EffectBlockViewModel[] NoBlocks = Array.Empty<EffectBlockViewModel>();
    public IReadOnlyList<EffectBlockViewModel> Blocks =>
        Mode switch { "XG" => xgBlocks, "MT-32" => mt32Blocks, "GM2" => gm2Blocks, "GS" => gsBlocks, _ => NoBlocks };
    // Layout: the first blocks side by side, two per row (GS: Reverb | Chorus, then Delay | EQ; XG and GM2:
    // Reverb | Chorus), the others below them at full width (GS EFX, XG Variation, the MT-32 reverb alone).
    int SideBlockCount => Mode switch { "GS" => 4, "XG" or "GM2" => 2, _ => 0 };
    public IReadOnlyList<EffectBlockPair> SideRows =>
        Enumerable.Range(0, SideBlockCount / 2).Select(i => new EffectBlockPair(Blocks[2 * i], Blocks[2 * i + 1])).ToList();
    public IEnumerable<EffectBlockViewModel> OtherBlocks => Blocks.Skip(SideBlockCount);

    public string Title => Mode switch
    {
        "XG" => "XG effects", "MT-32" => "MT-32 effects (CM-32L / CM-64)", "GM2" => "GM2 effects",
        "GS" => "GS effects (SC-88 / SC-88Pro / SC-8850)",
        _ => $"{standard} effects",
    };
    public string InfoText => IsAvailable ? null : $"No effect settings in {standard} mode (GS, XG, GM2 and MT-32 only).";

    // Runs a user action; an error is shown instead of crashing.
    internal void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { showError(ex); }
    }

    internal void SetEffect(int fx, int value)
    {
        if (fx >= Gm2Base) Gm2Effects.Set(ChannelMixer.Gm2Mode ? output() : null, fx - Gm2Base, value);
        else if (fx >= Mt32Base) Mt32Effects.Set(ChannelMixer.Mt32Mode ? output() : null, fx - Mt32Base, value);
        else GsXgEffects.Set(output(), fx, value);
        Refresh();
    }

    internal void ClearEffect(int fx)
    {
        if (fx >= Gm2Base) Gm2Effects.Clear(ChannelMixer.Gm2Mode ? output() : null, fx - Gm2Base);
        else if (fx >= Mt32Base) Mt32Effects.Clear(ChannelMixer.Mt32Mode ? output() : null, fx - Mt32Base);
        else GsXgEffects.Clear(output(), fx);
        Refresh();
    }

    // Reset button: all effects return to the file values (types first), MT-32 and GM2 effects included.
    public void Reset()
    {
        var device = output();
        foreach (bool macroFirst in new[] { true, false })
            for (int fx = 0; fx < EffectCount; fx++)
                if (GsXgEffects.IsFixed(fx) && GsXgEffects.IsType(fx) == macroFirst) GsXgEffects.Clear(device, fx);
        for (int p = 0; p < Mt32Effects.Count; p++)
            if (Mt32Effects.IsFixed(p)) Mt32Effects.Clear(ChannelMixer.Mt32Mode ? device : null, p);
        foreach (bool typeFirst in new[] { true, false })
            for (int p = 0; p < Gm2Effects.Count; p++)
                if (Gm2Effects.IsFixed(p) && Gm2Effects.IsType(p) == typeFirst) Gm2Effects.Clear(ChannelMixer.Gm2Mode ? device : null, p);
        Refresh();
    }

    // Clears the fixed effects without sending anything to the module (a reset follows at the start of the track).
    // Returns true if there were any.
    public bool ClearManualSettings()
    {
        bool had = false;
        for (int fx = 0; fx < EffectCount; fx++)
            if (GsXgEffects.IsFixed(fx)) { GsXgEffects.Clear(null, fx); had = true; }
        for (int p = 0; p < Mt32Effects.Count; p++)
            if (Mt32Effects.IsFixed(p)) { Mt32Effects.Clear(null, p); had = true; }
        for (int p = 0; p < Gm2Effects.Count; p++)
            if (Gm2Effects.IsFixed(p)) { Gm2Effects.Clear(null, p); had = true; }
        return had;
    }

    // ---------- Display ----------

    // Type-dependent parameter (XG Reverb / Chorus / Variation, GS EFX): the definition of the current type and of
    // this parameter, or null if this parameter does not depend on it (return, pan, XG sends, EFX per channel,
    // GS effects without definitions). TypeDef null: type unknown to the Domino definitions.
    (EffectTypeDef TypeDef, EffectParamDef Def)? GetTypedParam(int fx)
    {
        if (GsXgEffects.IsType(fx) || !GsXgEffects.TypeDependent[fx]) return null;
        Dictionary<int, EffectTypeDef> types;
        int typeParam;
        switch (GsXgEffects.Block[fx])
        {
            case 3: types = defs.XgReverbTypes; typeParam = 24; break;
            case 4: types = defs.XgChorusTypes; typeParam = 28; break;
            case 5: types = defs.XgVariationTypes; typeParam = VarTypeParam; break;
            case 6: types = defs.EfxTypes; typeParam = EfxTypeParam; break;
            default: return null;
        }
        int code = GsXgEffects.Value(typeParam);
        var typeDef = code >= 0 && types.TryGetValue(code, out var t) ? t : null;
        var def = typeDef != null && typeDef.Params.TryGetValue(GsXgEffects.Addresses[fx], out var d) ? d : null;
        return (typeDef, def);
    }

    // Unit of an EFX parameter based on its name, when the Domino labels have none (frequencies,
    // times and feedbacks already have one: 250Hz, 0.1ms, -98%). Values 0..127 have no unit.
    static string GetEfxUnit(EffectParamDef def)
    {
        var n = def.Name;
        var ic = RegexOptions.IgnoreCase;
        if (Regex.IsMatch(n, @"Gain|^Band \d|-G$", ic)) return "dB";            // EQ gains: -12..+12 dB (XG: "EQ L-G")
        if (Regex.IsMatch(n, @"\b(Rate|Slow|Fast)\b|Mod RT", ic)) return "Hz";   // modulation rates (whole words: not "Separate")
        if (Regex.IsMatch(n, "Phase", ic)) return "°";                           // phase shifts: 0..180°
        if (Regex.IsMatch(n, "Coarse", ic)) return "st";                         // transposition in semitones
        if (Regex.IsMatch(n, "Fine", ic)) return "ct";                           // fine tuning in cents
        return "";
    }

    // Readable value of a typed parameter: label from the Domino table (the closest one below for stepped
    // tables, e.g. frequencies), otherwise displayed value = MIDI value - Offset (signed if centered);
    // pan as L/C/R; unit added if the label has none.
    public static string FormatEfxParam(EffectParamDef def, int raw)
    {
        if (raw < 0) return "auto";
        // XG Variation delays, in 1/10 ms; decimal point as in the module's tables ("4.0kHz").
        if (def.Unit == "1/10 ms") return string.Format(CultureInfo.InvariantCulture, "{0:0.0} ms", raw / 10.0);
        // XG balances (1..127, 64 = equal) in Yamaha notation: D63>W … D=W … D<W63 (Dry/Wet), E>R / E<R (Er/Rev).
        var balance = Regex.Match(def.Name, @"^(Dry)\s*/\s*(Wet)|^(Er)\s*/\s*(Rev)", RegexOptions.IgnoreCase);
        if (balance.Success)
        {
            bool dryWet = balance.Groups[1].Success;
            string a = dryWet ? "D" : "E", b = dryWet ? "W" : "R";
            if (raw < 64) return $"{a}{64 - raw}>{b}";
            if (raw == 64) return $"{a}={b}";
            return $"{a}<{b}{raw - 64}";
        }
        string text = null;
        if (def.Labels.Count > 0)
        {
            if (def.Labels.TryGetValue(raw, out var label)) text = label;
            else
            {
                var lower = def.Labels.Keys.Where(k => k <= raw).ToList();
                if (lower.Count > 0) text = def.Labels[lower.Max()];
            }
        }
        if (text == null)
        {
            if (def.Name.Contains("Pan", StringComparison.OrdinalIgnoreCase) && def.Offset == 64) return MixerViewModel.FormatControllerValue(1, raw);   // L63..C..R63
            int shown = raw - def.Offset;
            // "+" only for parameters that have negative values (gains, transposition…).
            text = def.Min - def.Offset < 0 && shown > 0 ? $"+{shown}" : shown.ToString();
        }
        // Numbers from XG tables: "+08" -> "+8"; the unit is also added after "±0".
        var number = Regex.Match(text, @"^([+\-±]?)0*(\d+)$");
        if (number.Success) text = number.Groups[1].Value + number.Groups[2].Value;
        var unit = GetEfxUnit(def);
        if (unit != "" && Regex.IsMatch(text, @"^[+\-±]?\d+(\.\d+)?$")) return $"{text} {unit}";
        return text;
    }

    // Text of a value; -1 = "auto" (set by the module according to the type).
    public string FormatEffectValue(int fx, int value)
    {
        if (value < 0) return "auto";
        if (choices.TryGetValue(fx, out var list))
        {
            var choice = list.FirstOrDefault(x => x.Value == value);
            if (choice != null) return choice.Text;
            if (value > 127) return $"{value / 128:X2}:{value % 128:X2}";   // XG type not in the list
            return value.ToString();
        }
        if (PanParams.Contains(fx)) return MixerViewModel.FormatControllerValue(1, value);
        if (SignedParams.Contains(fx)) { int d = value - 64; return d > 0 ? $"+{d}" : d.ToString(); }
        if (EqGainParams.Contains(fx)) { int d = value - 64; return (d > 0 ? $"+{d}" : d.ToString()) + " dB"; }
        return value.ToString();
    }

    // Descriptions of effect values (insertion descriptions).
    public string DescribeEffect(int fx, int value)
    {
        if (ButtonChannel(fx) >= 0)
            return $"{(fx >= EqChannelFirst ? "EQ" : "EFX")} channel {ButtonChannel(fx) + 1}: {(value == 1 ? "on" : "off")}";
        var name = GetTypedParam(fx) is (_, { } def) ? def.Name : Labels[fx];
        var text = GetTypedParam(fx) is (_, { } d) ? FormatEfxParam(d, value) : FormatEffectValue(fx, value);
        return $"{BlockNames[GsXgEffects.Block[fx]]} {name}: {text}";
    }

    public string DescribeMt32(int p, int value)
    {
        int part = p - Mt32Effects.PartFirst;
        return p == 0 ? $"Reverb mode: {Mt32Effects.ModeNames[value]}"
             : part >= 0 ? $"Reverb {(part < 8 ? $"part {part + 1}" : "rhythm part")}: {(value != 0 ? "on" : "off")}"
             : $"Reverb {(p == 1 ? "time" : "level")}: {value}";
    }

    public string DescribeGm2(int p, int value) => $"{(Gm2Effects.Block(p) == 0 ? "Reverb" : "Chorus")} {Gm2Labels[p]}: {FormatGm2(p, value)}";

    // MT-32 page: values from Mt32Effects (fixed in blue), part channels in the tooltips.
    void RefreshMt32()
    {
        for (int p = 0; p < Mt32Effects.Count; p++)
        {
            var vm = mt32Params[p];
            int v = Mt32Effects.Value(p);
            bool isFixed = Mt32Effects.IsFixed(p);
            int part = p - Mt32Effects.PartFirst;
            int channel = part >= 0 ? Mt32Effects.PartChannel[part] : -1;
            int fileValue = Mt32Effects.FileValue(p);
            var key = $"{v}/{isFixed}/{fileValue}/{channel}";   // the file value shows in the tooltip of a fixed value
            if (key == vm.Key) continue;
            vm.Key = key;
            vm.Updating = true;
            try
            {
                vm.IsFixed = isFixed;
                var fileText = p == 0 ? Mt32Effects.ModeNames[fileValue] : part >= 0 ? (fileValue != 0 ? "on" : "off") : fileValue.ToString();
                var fixedText = isFixed ? $"Fixed (file: {fileText})" : "File value";
                switch (vm)
                {
                    case EffectListViewModel list:
                        list.SelectedChoice = list.Choices.FirstOrDefault(x => x.Value == v);
                        list.ToolTip = fixedText;
                        break;
                    case EffectSliderViewModel slider:
                        slider.Value = v;
                        slider.ValueText = v.ToString();
                        slider.ValueState = isFixed ? ValueState.Fixed : ValueState.File;
                        slider.ToolTip = fixedText;
                        break;
                    case EffectChannelViewModel button:
                        button.IsChecked = v != 0;
                        var name = part < 8 ? $"Part {part + 1}" : "Rhythm part";
                        var where = channel <= 15 ? $"channel {channel + 1}" : "no channel";
                        button.ToolTip = $"{name} ({where}): reverb {(v != 0 ? "on" : "off")}";
                        break;
                }
            }
            finally { vm.Updating = false; }
        }

    }

    // GM2 page: values from Gm2Effects (fixed in blue, "auto" in gray = set by the type).
    void RefreshGm2()
    {
        for (int p = 0; p < Gm2Effects.Count; p++)
        {
            var vm = gm2Params[p];
            int v = Gm2Effects.Value(p);
            bool isFixed = Gm2Effects.IsFixed(p);
            int fileValue = Gm2Effects.FileValue(p);
            var key = $"{v}/{isFixed}/{fileValue}";
            if (key == vm.Key) continue;
            vm.Key = key;
            vm.Updating = true;
            try
            {
                vm.IsFixed = isFixed;
                var fileText = fileValue < 0 ? "the type's default" : FormatGm2(p, fileValue);
                vm.ToolTip = isFixed ? $"Fixed (file: {fileText})" : fileValue < 0 ? "Set by the module according to the type" : "File value";
                switch (vm)
                {
                    case EffectListViewModel list:
                        list.SelectedChoice = list.Choices.FirstOrDefault(x => x.Value == v);
                        list.ValueText = list.SelectedChoice == null ? v.ToString() : "";
                        break;
                    case EffectSliderViewModel slider:
                        if (v >= 0) slider.Value = v;
                        slider.ValueText = FormatGm2(p, v);
                        slider.ValueState = isFixed ? ValueState.Fixed : v < 0 ? ValueState.Auto : ValueState.File;
                        break;
                }
            }
            finally { vm.Updating = false; }
        }
    }

    // Updates every parameter from GsXgEffects: fixed value (blue), from the file, or "auto" (gray).
    // A parameter is only updated if something changed (the EFX / XG types change the name and range of theirs).
    public void Refresh()
    {
        RefreshMt32();
        RefreshGm2();
        for (int fx = 0; fx < EffectCount; fx++)
        {
            var p = parameters[fx];
            int v = GsXgEffects.Value(fx);
            bool isFixed = GsXgEffects.IsFixed(fx);
            var key = $"{v}/{isFixed}/{GsXgEffects.Value(EfxTypeParam)}/{GsXgEffects.Value(VarTypeParam)}/" +
                      $"{GsXgEffects.Value(24)}/{GsXgEffects.Value(28)}";
            if (key == p.Key) continue;
            p.Key = key;
            int fileValue = GsXgEffects.FileValue(fx);

            p.Updating = true;
            try
            {
                p.IsFixed = isFixed;
                if (p is EffectChannelViewModel channel)
                {
                    // EFX on a channel: button pressed if the part goes through the EFX.
                    channel.IsChecked = v == 1;
                    bool eq = fx >= EqChannelFirst;
                    channel.ToolTip = $"Channel {ButtonChannel(fx) + 1}: " + (v == 1 ? (eq ? "goes through the EQ" : "goes through the EFX") : (eq ? "no EQ" : "no EFX"));
                    continue;
                }

                // Parameter of a type: name, range and default value for the current type (Domino definitions).
                // A parameter unused by this type is hidden; unknown type: generic "Param. n".
                EffectParamDef def = null;
                if (GetTypedParam(fx) is (var typeDef, var paramDef) && p is EffectSliderViewModel typed)
                {
                    def = paramDef;
                    typed.IsVisible = typeDef == null || def != null;
                    typed.Label = def != null ? def.Name : Labels[fx];
                    typed.Minimum = def?.Min ?? GsXgEffects.Min[fx];
                    typed.Maximum = def?.Max ?? GsXgEffects.Max[fx];
                    if (v < 0 && def != null) v = def.Default;   // value set by the type: we know it
                }

                if (p is EffectListViewModel list)
                {
                    list.SelectedChoice = list.Choices.FirstOrDefault(x => x.Value == v);
                    // Value not in the list (type from another model): its code on the right; unknown: "auto".
                    list.ValueText = v < 0 ? "auto" : list.SelectedChoice == null ? FormatEffectValue(fx, v) : "";
                }
                else if (p is EffectSliderViewModel slider)
                {
                    if (v >= 0) slider.Value = v;
                    slider.ValueText = def != null ? FormatEfxParam(def, v) : FormatEffectValue(fx, v);
                    slider.ValueState = isFixed ? ValueState.Fixed : v < 0 ? ValueState.Auto : ValueState.File;
                }
                var fileText = def != null && fileValue < 0 ? $"{FormatEfxParam(def, def.Default)} (type default)"
                             : def != null ? FormatEfxParam(def, fileValue) : FormatEffectValue(fx, fileValue);
                p.ToolTip = isFixed ? $"Fixed (file: {fileText})"
                          : fileValue < 0 && def != null ? "Default value of the type"
                          : fileValue < 0 ? "Set by the module according to the type"
                          : "File value";
            }
            finally { p.Updating = false; }
        }
    }
}
