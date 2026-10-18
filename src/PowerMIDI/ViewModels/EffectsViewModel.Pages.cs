using System.Globalization;
using Melanchall.DryWetMidi.Multimedia;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// Part and Drums pages of the Effects tab (see SettingsPage): what each one shows, read from its settings module.
// XG: Drums (drum setups, plus the part modes of the channels) and Part; GS: Drums (drum maps) and Part (with the EQ
// switch of the GS EQ page); GM2: Drums (Key-Based Instrument Control) and Part (RPN); MT-32: Drums (rhythm setup)
// and Part (patch values, partial reserve).
public sealed partial class EffectsViewModel
{
    SettingsPage xgDrums, xgPart, gsDrums, gsPart, gm2Drums, gm2Part, mt32Drums, mt32Part;
    readonly List<SettingsPage> pages = new();

    // XG Drums page: buttons of the channels under the setup buttons, pressed if their part uses the setup shown.
    const int DrumPartBase = 3100;   // + channel
    readonly List<EffectChannelViewModel> drumPartButtons = new();

    static readonly IReadOnlyList<string> Channels16 = Enumerable.Range(1, 16).Select(c => c.ToString()).ToList();
    static string NoteName(int n) => SettingsPage.NoteName(n);
    static string ChannelList(IEnumerable<int> channels) =>
        channels.Any() ? "channel " + string.Join(", ", channels.Select(c => c + 1)) : "no channel";

    internal IOutputDevice Output() => output();

    // Creates the pages and adds their blocks to the tab strips of their modes (after the effects).
    void BuildPages()
    {
        BuildXgPages();
        BuildGsPages();
        BuildGm2Pages();
        BuildMt32Pages();
        gsBlocks.Add(gsDrums.Block);
        gsBlocks.Add(gsPart.Block);
        xgBlocks.Add(xgDrums.Block);
        xgBlocks.Add(xgPart.Block);
        gm2Blocks.Add(gm2Drums.Block);
        gm2Blocks.Add(gm2Part.Block);
        mt32Blocks.Insert(1, mt32Drums.Block);   // Reverb, Drums, Part
        mt32Blocks.Add(mt32Part.Block);
        pages.AddRange(new[] { xgDrums, xgPart, gsDrums, gsPart, gm2Drums, gm2Part, mt32Drums, mt32Part });

        // Texts of the settings (Event Editor, Append / Replace), with the names and units of the pages.
        ModuleSettings.GsXgFx.Describe = (k, v) => DescribeEffect(k.C, v);
        ModuleSettings.Mt32Reverb.Describe = (k, v) => DescribeMt32(k.C, v);
        ModuleSettings.Gm2Fx.Describe = (k, v) => DescribeGm2(k.C, v);
        ModuleSettings.XgPart.Describe = (k, v) => xgPart.Describe(k.A, 0, k.C, v);
        ModuleSettings.XgPartModes.Describe = (k, v) => $"Part mode ch {k.A + 1}: {PartModeName(v)}";
        ModuleSettings.XgDrums.Describe = (k, v) => xgDrums.Describe(k.A, k.B, k.C, v);
        ModuleSettings.GsPart.Describe = (k, v) => gsPart.Describe(k.A, 0, k.C + 1, v);   // after the EQ switch
        ModuleSettings.GsDrums.Describe = (k, v) => gsDrums.Describe(k.A, k.B, k.C, v);
        ModuleSettings.Gm2Part.Describe = (k, v) => gm2Part.Describe(k.A, 0, k.C, v);
        ModuleSettings.Gm2Drums.Describe = (k, v) => gm2Drums.Describe(k.A, k.B, k.C, v);
        ModuleSettings.Mt32Part.Describe = (k, v) => mt32Part.Describe(k.A, 0, k.C, v);
        ModuleSettings.Mt32Drums.Describe = (k, v) => mt32Drums.Describe(0, k.B, k.C, v);
    }

    // ---------- XG ----------

    void BuildXgPages()
    {
        // Drums: setups 1-4 (1-2 with the MU Basic voice map: an XG Level 1 module has 2), notes 13-91 of the kit of the
        // parts assigned to the setup; pitch, filter, envelope, output.
        int Setups() => ChannelMixer.VoiceMap == ChannelMixer.VoiceMapBasic ? XgDrumSetup.Level1Setups : XgDrumSetup.Setups;
        xgDrums = new SettingsPage(this, 3000, XgDrumSetup.Count)
        {
            Params = XgDrumSetup.Names.Select((name, p) => new PageParam
            {
                Label = name, Format = v => FormatDrum(p, v), AutoPosition = XgDrumSetup.IsOffset(p) || p == XgDrumSetup.Pan ? 64 : 0,
            }).ToList(),
            GroupLabels = new[] { "1", "2", "3", "4" },
            Value = XgDrumSetup.Value, FileValue = XgDrumSetup.FileValue, IsFixed = XgDrumSetup.IsFixed,
            IsNoteFixed = XgDrumSetup.IsNoteFixed, IsGroupFixed = XgDrumSetup.IsSetupFixed,
            Set = XgDrumSetup.Set, Clear = (d, s, n, p) => XgDrumSetup.Clear(d, s, n, p), IsActive = () => ChannelMixer.XgMode,
            GroupVisible = s => s < Setups(),
            GroupToolTip = s => $"Drum setup {s + 1}: " + (SetupChannels(s).Count == 0 ? "no part" : ChannelList(SetupChannels(s))),
            GroupKey = s => string.Join(",", SetupChannels(s)),
            HasNotes = true, FirstNote = XgDrumSetup.FirstNote, LastNote = XgDrumSetup.LastNote,
            Notes = _ => Enumerable.Range(XgDrumSetup.FirstNote, XgDrumSetup.LastNote - XgDrumSetup.FirstNote + 1),
            Tones = s => KitTones(SetupKit(s)), KitKey = s => SetupKit(s).ToString(),
            NotesToolTip = s => $"Kit: {defs.XgDrums.GetValueOrDefault(SetupKit(s), "?")}. Right click: note back to file",
            GroupHasChannel = (s, c) => XgDrumSetup.SetupOf(c) == s,
            AutoText = "Kit default",
            DescribePrefix = (s, n) => $"Drum setup {s + 1} note {n} ({NoteName(n)})",
        }.Build("Drum setup", "Drums", "Drum setup", "Channels");
        for (int c = 0; c < 16; c++)
        {
            var button = new EffectChannelViewModel(this, DrumPartBase + c, c);
            drumPartButtons.Add(button);
            xgDrums.Block.Parts.Add(button);
        }

        // Part: EQ, tuning, play, filter, pitch EG of a channel; EQ and filter (MU100 and later) hidden with the MU Basic
        // voice map.
        xgPart = new SettingsPage(this, 4000, XgPartParams.Count)
        {
            Params = XgPartParams.Params.Select((x, p) => new PageParam
            {
                Label = x.Name, Section = x.Group, Min = x.Min, Max = x.Max, Format = v => FormatPart(p, v),
                Choices = p == XgPartParams.MonoPoly ? IndexedChoices("Mono", "Poly") : null,
                Visible = x.Mu100 ? _ => ChannelMixer.VoiceMap != ChannelMixer.VoiceMapBasic : null,
            }).ToList(),
            GroupLabels = Channels16,
            Value = (c, _, p) => XgPartParams.Value(c, p), FileValue = (c, _, p) => XgPartParams.FileValue(c, p),
            IsFixed = (c, _, p) => XgPartParams.IsFixed(c, p), IsNoteFixed = (c, _) => XgPartParams.IsChannelFixed(c),
            IsGroupFixed = XgPartParams.IsChannelFixed,
            Set = (d, c, _, p, v) => XgPartParams.Set(d, c, p, v), Clear = (d, c, _, p) => XgPartParams.Clear(d, c, p),
            IsActive = () => ChannelMixer.XgMode,
            GroupToolTip = c => $"Part settings of channel {c + 1}" + (XgPartParams.IsChannelFixed(c) ? " (changed)" : ""),
            DescribePrefix = (c, _) => $"Part ch {c + 1}",
        }.Build("Part settings", "Part", "Channel");
    }

    // Channel button of the XG Drums page pressed: its part uses the drum setup shown (PART MODE DRUMS1..4). Released:
    // back to the file's part mode if it is another one, otherwise DRUM for a part that plays a drum kit (bank MSB
    // 126 / 127), NORMAL for the others.
    void SetDrumPart(int channel, bool on)
    {
        var device = ChannelMixer.XgMode ? output() : null;
        int mode = 2 + xgDrums.Group;
        if (on) XgDrumSetup.SetPartMode(device, channel, mode);
        else if (XgDrumSetup.FilePartMode(channel) != mode) XgDrumSetup.ClearPartMode(device, channel);
        else XgDrumSetup.SetPartMode(device, channel, PlaysKit(channel) ? XgDrumSetup.Drum : XgDrumSetup.Normal);
    }

    // Channels of the XG Drums page: pressed if their part uses the setup shown; blue when the part mode is chosen here.
    void RefreshDrumParts()
    {
        for (int c = 0; c < 16; c++)
        {
            int mode = XgDrumSetup.PartMode(c), fileMode = XgDrumSetup.FilePartMode(c);
            bool on = mode == 2 + xgDrums.Group, isFixed = XgDrumSetup.PartModeOverride[c] >= 0;
            drumPartButtons[c].Show($"{on}/{isFixed}/{mode}/{fileMode}", on, isFixed,
                                    $"Channel {c + 1}: {PartModeName(mode)}" + (isFixed ? $" (file: {PartModeName(fileMode)})" : ""));
        }
    }

    static bool PlaysKit(int c) => (ChannelMixer.Override[c] ? ChannelMixer.OverrideMsb[c] : ChannelMixer.BankMsb[c]) is 126 or 127;

    static string PartModeName(int mode) => mode switch { 0 => "normal", 1 => "drum", _ => $"drum setup {mode - 1}" };

    // Channels of the parts assigned to a setup, and the kit they play (that of the first one playing a kit; Standard
    // Kit if none).
    static List<int> SetupChannels(int s) => Enumerable.Range(0, 16).Where(c => XgDrumSetup.SetupOf(c) == s).ToList();

    static (int Msb, int Program) SetupKit(int s)
    {
        var channels = SetupChannels(s);
        if (channels.Count == 0) return (127, 0);
        int c = channels.FirstOrDefault(PlaysKit, channels[0]);
        return ChannelMixer.Override[c] ? (ChannelMixer.OverrideMsb[c], ChannelMixer.OverrideProgram[c]) : (ChannelMixer.BankMsb[c], ChannelMixer.Program[c]);
    }

    IReadOnlyDictionary<int, string> KitTones((int Msb, int Program) kit) =>
        defs.XgDrumTones.TryGetValue(kit, out var tones) ? tones
        : defs.XgDrumTones.TryGetValue((kit.Msb, 0), out tones) ? tones
        : defs.XgDrumTones.GetValueOrDefault((127, 0), new Dictionary<int, string>());

    // Drum setup: offsets -64..+63 (pitch in semitones / cents), pan L/C/R (0 = random), levels 0..127.
    static string FormatDrum(int p, int v)
    {
        if (XgDrumSetup.IsOffset(p)) return MidiTools.FormatSigned(v - 64) + (p == 0 ? " st" : p == 1 ? " ct" : "");
        if (p == XgDrumSetup.Pan) return v == 0 ? "Rnd" : MixerViewModel.FormatControllerValue(1, v);
        return v.ToString();
    }

    // Part: EQ gains in dB and frequencies; note shift and bend range in semitones; detune in Hz; velocity sense, dry
    // level: 0..127; high pass filter and pitch EG: -64..+63.
    string FormatPart(int p, int v)
    {
        var name = XgPartParams.Params[p].Name;
        return p switch
        {
            XgPartParams.BassGain or XgPartParams.TrebleGain => MidiTools.FormatSigned(v - 64) + " dB",
            _ when name.EndsWith("Frequency") => xgEqFrequencies.TryGetValue(v, out var hz) ? hz : v.ToString(),
            XgPartParams.NoteShift or XgPartParams.BendRange => MidiTools.FormatSigned(v - 64) + " st",
            XgPartParams.Detune => (v > 128 ? "+" : "") + string.Format(CultureInfo.InvariantCulture, "{0:0.0} Hz", (v - 128) / 10.0),
            XgPartParams.MonoPoly => v == 0 ? "Mono" : "Poly",
            _ when XgPartParams.Params[p].Group is "Filter" or "Pitch EG" => MidiTools.FormatSigned(v - 64),
            _ => v.ToString(),
        };
    }

    // ---------- GS ----------

    void BuildGsPages()
    {
        // Drums: drum maps 1-2 (drum parts 1-2), notes of their drum set; pitch, level, pan, sends, assign group, receive.
        gsDrums = new SettingsPage(this, 6000, GsDrumMap.Count)
        {
            Params = GsDrumMap.Names.Select((name, p) => new PageParam
            {
                Label = name, Format = v => FormatGsDrum(p, v), AutoPosition = p is GsDrumMap.PitchCoarse or GsDrumMap.Pan ? 64 : 0,
                Choices = GsDrumMap.IsSwitch(p) ? IndexedChoices("Off", "On") : null,
            }).ToList(),
            GroupLabels = new[] { "1", "2" },
            Value = GsDrumMap.Value, FileValue = GsDrumMap.FileValue, IsFixed = GsDrumMap.IsFixed,
            IsNoteFixed = GsDrumMap.IsNoteFixed, IsGroupFixed = GsDrumMap.IsMapFixed,
            Set = GsDrumMap.Set, IsActive = () => ChannelMixer.GsMode,
            Clear = (d, m, n, p) => { foreach (int reset in GsDrumMap.Clear(d, m, n, p)) ChannelMixer.ResetGsDrumMap(d, reset); },
            GroupToolTip = m => $"Drum map {m + 1} (drum part {m + 1}): " + ChannelList(MapChannels(m)),
            GroupKey = m => string.Join(",", MapChannels(m)),
            HasNotes = true, FirstNote = 0, LastNote = 127,
            Notes = m => GsTones(m).Count > 0 ? GsTones(m).Keys : Enumerable.Range(27, 61),
            Tones = GsTones, KitKey = m => GsDrumKit(m).ToString(),
            NotesToolTip = m => $"Drum set: {defs.GsDrums.GetValueOrDefault(GsDrumKit(m), "?")}. Right click: note back to file",
            GroupHasChannel = (m, c) => GsDrumMap.MapOf(c) == m,
            AutoText = "Drum set default",
            DescribePrefix = (m, n) => $"Drum map {m + 1} note {n} ({NoteName(n)})",
        }.Build("Drum map", "Drums", "Drum map");

        // Part: the EQ switch of a channel (40 4x 20: the part goes through the GS EQ of the EQ page), tuning, play.
        const int eq = EqChannelFirst;
        var eqSwitch = new PageParam
        {
            Label = "Part EQ", Section = "EQ", Choices = IndexedChoices("Off", "On"), Format = v => v == 1 ? "on" : "off",
            OwnValue = c => GsXgEffects.Value(eq + c), OwnFileValue = c => GsXgEffects.FileValue(eq + c),
            OwnIsFixed = c => GsXgEffects.IsFixed(eq + c), OwnSet = (c, v) => GsXgEffects.Set(output(), eq + c, v),
            OwnClear = c => { if (GsXgEffects.IsFixed(eq + c)) GsXgEffects.Clear(output(), eq + c); },
            ToolTipSuffix = () => ". Part goes through EQ settings of EQ tab",
        };
        gsPart = new SettingsPage(this, 7000, GsPartParams.Count + 1)
        {
            Params = new[] { eqSwitch }.Concat(GsPartParams.Params.Select((x, p) => new PageParam
            {
                Label = x.Name, Section = x.Group, Min = x.Min, Max = x.Max, Format = v => FormatGsPart(p, v),
                Choices = p switch
                {
                    GsPartParams.MonoPoly => IndexedChoices("Mono", "Poly"),
                    GsPartParams.AssignMode => IndexedChoices("Single", "Limited Multi", "Full Multi"),
                    _ => null,
                },
            })).ToList(),
            GroupLabels = Channels16,
            // Parameter p of the page: GsPartParams p - 1 (the EQ switch first).
            Value = (c, _, p) => GsPartParams.Value(c, p - 1), FileValue = (c, _, p) => GsPartParams.FileValue(c, p - 1),
            IsFixed = (c, _, p) => GsPartParams.IsFixed(c, p - 1), IsNoteFixed = (c, _) => GsPartParams.IsChannelFixed(c),
            IsGroupFixed = GsPartParams.IsChannelFixed,
            Set = (d, c, _, p, v) => GsPartParams.Set(d, c, p - 1, v),
            Clear = (d, c, _, p) => GsPartParams.Clear(d, c, p < 0 ? -1 : p - 1),
            IsActive = () => ChannelMixer.GsMode,
            GroupToolTip = c => $"Part settings of channel {c + 1}" +
                                (GsPartParams.IsChannelFixed(c) || GsXgEffects.IsFixed(eq + c) ? " (changed)" : ""),
            AutoText = "Set by module according to part",
            DescribePrefix = (c, _) => $"Part ch {c + 1}",
        }.Build("Part settings", "Part", "Channel");
    }

    static List<int> MapChannels(int m) => Enumerable.Range(0, 16).Where(c => GsDrumMap.MapOf(c) == m).ToList();

    // Drum set of a map: the last one selected on its drum part (STANDARD if none), in the map of its bank LSB.
    static (int Map, int Program) GsDrumKit(int m)
    {
        int part = m + 1, lsb = ChannelMixer.DrumKitLsb[part];
        return (lsb is >= 1 and <= 4 ? lsb : ChannelMixer.GsDefaultMap, Math.Max(0, ChannelMixer.DrumKitProgram[part]));
    }

    IReadOnlyDictionary<int, string> GsTones(int m)
    {
        var kit = GsDrumKit(m);
        return defs.GsDrumTones.GetValueOrDefault(kit) ?? defs.GsDrumTones.GetValueOrDefault((kit.Map, 0)) ?? new Dictionary<int, string>();
    }

    // Drum map: pitch in semitones, pan L/C/R (0 = random), assign group (0 = off), levels 0..127.
    static string FormatGsDrum(int p, int v) => p switch
    {
        GsDrumMap.PitchCoarse => MidiTools.FormatSigned(v - 64) + " st",
        GsDrumMap.Pan => v == 0 ? "Rnd" : MixerViewModel.FormatControllerValue(1, v),
        GsDrumMap.AssignGroup => v == 0 ? "Off" : v.ToString(),
        GsDrumMap.RxNoteOff or GsDrumMap.RxNoteOn => v == 0 ? "Off" : "On",
        _ => v.ToString(),
    };

    // Part: key shift and bend range in semitones, offset fine in Hz, fine tune in cents, key range as notes.
    static string FormatGsPart(int p, int v) => p switch
    {
        GsPartParams.KeyShift or GsPartParams.BendRange => MidiTools.FormatSigned(v - 64) + " st",
        GsPartParams.OffsetFine => (v > 128 ? "+" : "") + string.Format(CultureInfo.InvariantCulture, "{0:0.0} Hz", (v - 128) / 10.0),
        GsPartParams.FineTune => MidiTools.FormatSigned((int)Math.Round((v - 64) * 100 / 64.0)) + " ct",
        GsPartParams.MonoPoly => v == 0 ? "Mono" : "Poly",
        GsPartParams.AssignMode => v switch { 0 => "Single", 1 => "Limited Multi", _ => "Full Multi" },
        GsPartParams.KeyRangeLow or GsPartParams.KeyRangeHigh => NoteName(v + 12),
        _ => v.ToString(),
    };

    // ---------- GM2 ----------

    void BuildGm2Pages()
    {
        // Drums: the drum channels (bank MSB 120, or channel 10), notes of their kit; Key-Based Instrument Control.
        gm2Drums = new SettingsPage(this, 8000, Gm2DrumParams.Count)
        {
            Params = Gm2DrumParams.Names.Select((name, p) => new PageParam
            {
                Label = name, Format = v => FormatGm2Drum(p, v), AutoPosition = p == Gm2DrumParams.Pan ? 64 : 0,
            }).ToList(),
            GroupLabels = Channels16, Group = 9,
            Value = Gm2DrumParams.Value, FileValue = Gm2DrumParams.FileValue, IsFixed = Gm2DrumParams.IsFixed,
            IsNoteFixed = Gm2DrumParams.IsNoteFixed, IsGroupFixed = Gm2DrumParams.IsChannelFixed,
            Set = Gm2DrumParams.Set, Clear = (d, c, n, p) => Gm2DrumParams.Clear(d, c, n, p), IsActive = () => ChannelMixer.Gm2Mode,
            GroupVisible = c => IsGm2DrumChannel(c) || Gm2DrumParams.IsChannelFixed(c),
            GroupToolTip = c => $"Drums of channel {c + 1}" + (Gm2DrumParams.IsChannelFixed(c) ? " (changed)" : ""),
            HasNotes = true, FirstNote = 0, LastNote = 127,
            Notes = c => Gm2Tones(c).Count > 0 ? Gm2Tones(c).Keys : Enumerable.Range(27, 61),
            Tones = Gm2Tones, KitKey = c => Gm2Kit(c).ToString(),
            NotesToolTip = c => $"Kit: {defs.Gm2Drums.GetValueOrDefault(Gm2Kit(c), "?")}. Right click: note back to file",
            GroupHasChannel = (g, c) => c == g,
            AutoText = "Kit default",
            DescribePrefix = (c, n) => $"Drums ch {c + 1} note {n} ({NoteName(n)})",
        }.Build("Drums", "Drums", "Channel");

        // Part: the RPN of a channel (tuning, modulation depth range).
        gm2Part = new SettingsPage(this, 9000, Gm2PartParams.Count)
        {
            Params = Gm2PartParams.Params.Select((x, p) => new PageParam
            {
                Label = x.Name, Section = x.Group, Min = x.Min, Max = x.Max, Format = v => FormatGm2Part(p, v),
            }).ToList(),
            GroupLabels = Channels16,
            Value = (c, _, p) => Gm2PartParams.Value(c, p), FileValue = (c, _, p) => Gm2PartParams.FileValue(c, p),
            IsFixed = (c, _, p) => Gm2PartParams.IsFixed(c, p), IsNoteFixed = (c, _) => Gm2PartParams.IsChannelFixed(c),
            IsGroupFixed = Gm2PartParams.IsChannelFixed,
            Set = (d, c, _, p, v) => Gm2PartParams.Set(d, c, p, v), Clear = (d, c, _, p) => Gm2PartParams.Clear(d, c, p),
            IsActive = () => ChannelMixer.Gm2Mode,
            GroupToolTip = c => $"Part settings of channel {c + 1}" + (Gm2PartParams.IsChannelFixed(c) ? " (changed)" : ""),
            DescribePrefix = (c, _) => $"Part ch {c + 1}",
        }.Build("Part settings", "Part", "Channel");
    }

    // A GM2 drum channel: bank MSB 120, or channel 10 unless it plays a melodic bank (121).
    static bool IsGm2DrumChannel(int c) =>
        InstrumentDefinitions.IsGm2Drum(c, ChannelMixer.Override[c] ? ChannelMixer.OverrideMsb[c] : ChannelMixer.BankMsb[c]);

    static int Gm2Kit(int c) => ChannelMixer.Override[c] ? ChannelMixer.OverrideProgram[c] : ChannelMixer.Program[c];

    IReadOnlyDictionary<int, string> Gm2Tones(int c) =>
        defs.Gm2DrumTones.GetValueOrDefault(Gm2Kit(c)) ?? defs.Gm2DrumTones.GetValueOrDefault(0) ?? new Dictionary<int, string>();

    // Level: relative, 0..200 % (40h = 100 %); pan L/C/R; sends 0..127.
    static string FormatGm2Drum(int p, int v) => p switch
    {
        Gm2DrumParams.Level => $"{(int)Math.Round(v * 200 / 128.0)} %",
        Gm2DrumParams.Pan => MixerViewModel.FormatControllerValue(1, v),
        _ => v.ToString(),
    };

    // Bend range and coarse tune in semitones, fine tune and modulation depth range in cents.
    static string FormatGm2Part(int p, int v) => p switch
    {
        Gm2PartParams.BendRange => $"{v} st",
        Gm2PartParams.FineTune => MidiTools.FormatSigned((int)Math.Round((v - 64) * 100 / 64.0)) + " ct",
        Gm2PartParams.CoarseTune => MidiTools.FormatSigned(v - 64) + " st",
        _ => $"{(int)Math.Round(v * 100 / 128.0)} ct",
    };

    // ---------- MT-32 ----------

    void BuildMt32Pages()
    {
        // Drums: the rhythm part (one button), notes 24-108; timbre (memory timbres with the names loaded by the file,
        // rhythm timbres), level, pan, reverb.
        int RhythmChannel() => Mt32Effects.PartChannel[Mt32PartParams.RhythmPart];
        mt32Drums = new SettingsPage(this, 11000, Mt32DrumParams.Count)
        {
            Params = Mt32DrumParams.Names.Select((name, p) => new PageParam
            {
                Label = name, Max = Mt32DrumParams.Max[p], Format = v => FormatMt32Drum(p, v), AutoPosition = p == Mt32DrumParams.Pan ? 7 : 0,
                Choices = p == Mt32DrumParams.Reverb ? IndexedChoices("Off", "On") : null,
                DynamicChoices = p == Mt32DrumParams.Timbre
                    ? () => Enumerable.Range(0, Mt32DrumParams.TimbreOff + 1).Select(v => new EffectChoice(Mt32TimbreName(v), v)).ToList()
                    : null,
                ChoicesKey = p == Mt32DrumParams.Timbre ? () => Mt32Patches.Version.ToString() : null,
            }).ToList(),
            GroupLabels = new[] { "R" },
            Value = (_, n, p) => Mt32DrumParams.Value(n, p), FileValue = (_, n, p) => Mt32DrumParams.FileValue(n, p),
            IsFixed = (_, n, p) => Mt32DrumParams.IsFixed(n, p), IsNoteFixed = (_, n) => Mt32DrumParams.IsNoteFixed(n),
            IsGroupFixed = _ => Mt32DrumParams.HasFixed,
            Set = (d, _, n, p, v) => Mt32DrumParams.Set(d, n, p, v), Clear = (d, _, n, p) => Mt32DrumParams.Clear(d, n, p),
            IsActive = () => ChannelMixer.Mt32Mode,
            GroupToolTip = _ => $"Rhythm part ({(RhythmChannel() <= 15 ? $"channel {RhythmChannel() + 1}" : "no channel")}). Right click: all notes back to file",
            GroupKey = _ => RhythmChannel().ToString(),
            HasNotes = true, FirstNote = Mt32DrumParams.FirstKey, LastNote = Mt32DrumParams.LastKey,
            Notes = _ => Enumerable.Range(Mt32DrumParams.FirstKey, Mt32DrumParams.Keys),
            Tones = _ => defs.Mt32DrumTones, NotesToolTip = _ => "Right click: note back to file",
            GroupHasChannel = (_, c) => c == RhythmChannel(),
            AutoText = "Module's rhythm map",
            DescribePrefix = (_, n) => $"MT-32 rhythm note {n} ({NoteName(n)})",
        }.Build("Rhythm setup", "Drums", "Rhythm part");

        // Part: parts 1-8 (patch values) and the rhythm part (R: partial reserve only).
        mt32Part = new SettingsPage(this, 10000, Mt32PartParams.Count)
        {
            Params = Mt32PartParams.Names.Select((name, p) => new PageParam
            {
                Label = name, Max = Mt32PartParams.Max[p], Format = v => FormatMt32Part(p, v),
                AutoPosition = p == Mt32PartParams.KeyShift ? 24 : p == Mt32PartParams.FineTune ? 50 : 0,
                Choices = p == Mt32PartParams.AssignMode ? IndexedChoices("POLY 1", "POLY 2", "POLY 3", "POLY 4") : null,
                Visible = Mt32PartParams.IsPatchParam(p) ? part => part < 8 : null,
                ToolTipSuffix = p == Mt32PartParams.PartialReserve ? () => $". Partials reserved by all parts: {Mt32PartParams.ReserveTotal} / 32" : null,
            }).ToList(),
            GroupLabels = Enumerable.Range(1, 8).Select(x => x.ToString()).Append("R").ToList(),
            Value = (part, _, p) => Mt32PartParams.Value(part, p), FileValue = (part, _, p) => Mt32PartParams.FileValue(part, p),
            IsFixed = (part, _, p) => Mt32PartParams.IsFixed(part, p), IsNoteFixed = (part, _) => Mt32PartParams.IsPartFixed(part),
            IsGroupFixed = Mt32PartParams.IsPartFixed,
            Set = (d, part, _, p, v) => Mt32PartParams.Set(d, part, p, v), Clear = (d, part, _, p) => Mt32PartParams.Clear(d, part, p),
            IsActive = () => ChannelMixer.Mt32Mode,
            GroupToolTip = part =>
            {
                int channel = Mt32Effects.PartChannel[part];
                var name = Mt32PartName(part);
                return $"{char.ToUpper(name[0])}{name[1..]} ({(channel <= 15 ? $"channel {channel + 1}" : "no channel")})" +
                       (Mt32PartParams.IsPartFixed(part) ? ", changed" : "");
            },
            GroupKey = part => Mt32Effects.PartChannel[part].ToString(),
            AutoText = "Patch value",
            DescribePrefix = (part, _) => $"MT-32 {Mt32PartName(part)}",
        }.Build("Part settings", "Part", "Part");
    }

    static string Mt32PartName(int part) => part < 8 ? $"part {part + 1}" : "rhythm part";

    // Name of a timbre of the rhythm setup: memory timbres (M1-M64, names loaded by the file), rhythm timbres (R1-R30).
    static string Mt32TimbreName(int v) =>
        v < 64 ? $"M{v + 1}" + (Mt32Patches.MemoryTimbreName(v) is { } name ? $" · {name}" : "")
        : v < Mt32DrumParams.TimbreOff ? $"R{v - 63} · {Mt32DrumParams.RhythmTimbreNames[v - 64]}" : "Off";

    // Rhythm setup: level 0..100, pan R7..C..L7 (0..14 = right..left), reverb off / on.
    static string FormatMt32Drum(int p, int v) => p switch
    {
        Mt32DrumParams.Timbre => Mt32TimbreName(v),
        Mt32DrumParams.Pan => v == 7 ? "C" : v < 7 ? $"R{7 - v}" : $"L{v - 7}",
        Mt32DrumParams.Reverb => v == 0 ? "Off" : "On",
        _ => v.ToString(),
    };

    // Part: key shift in semitones and fine tune in cents (both centered), bender range in semitones, partial reserve.
    static string FormatMt32Part(int p, int v) => p switch
    {
        Mt32PartParams.KeyShift => MidiTools.FormatSigned(v - 24) + " st",
        Mt32PartParams.FineTune => MidiTools.FormatSigned(v - 50) + " ct",
        Mt32PartParams.BenderRange => $"{v} st",
        Mt32PartParams.AssignMode => $"POLY {v + 1}",
        _ => v.ToString(),
    };
}
