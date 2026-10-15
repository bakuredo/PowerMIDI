using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// Key of a value fixed in a settings module: up to three numbers (group or channel, note, parameter), unused ones 0.
public readonly record struct SettingKey(int A, int B = 0, int C = 0);

// One kind of settings fixed in the Effects tab (GsXgEffects, Mt32Effects, Gm2Effects, and the Part and Drums pages:
// XgPartParams, XgDrumSetup, GsDrumMap…), seen in the same way by the player, the Insert mode and the save: its fixed
// values, how to set, clear, send and write them.
public sealed class SettingsModule
{
    public string Name { get; init; }                     // short code, in the fingerprint of the settings
    public string Standard { get; init; }                 // mode of the track it belongs to: XG, GS, GM2, MT-32 (null: any)
    public Func<bool> IsActive { get; init; }             // the module plays in this mode (its values are sent)
    public Func<IEnumerable<(SettingKey Key, int Value)>> Fixed { get; init; }
    public Action<IOutputDevice, SettingKey, int> Set { get; init; }
    public Action<IOutputDevice, SettingKey> Clear { get; init; }
    public Action<IOutputDevice> ClearAll { get; init; }
    public Action ResetFile { get; init; }                // reset of the module: the file's values back to defaults
    public Action<IOutputDevice> Apply { get; init; }     // sends the fixed values
    public Func<SettingKey, int, IEnumerable<MidiEvent>> Events { get; init; }   // messages writing one value
    public Func<SettingKey, int> ChannelOf { get; init; } // channel whose track gets them in an insertion (-1: first track)
    public Func<MidiFile, string> Write { get; init; }    // writes the fixed values in a saved file; returns a report
    // Text of a value (Insert mode), set by the interface (EffectsViewModel), which knows the names and units.
    public Func<SettingKey, int, string> Describe { get; set; }

    public bool HasFixed => Fixed().Any();
    public Dictionary<SettingKey, int> FixedValues() => Fixed().ToDictionary(x => x.Key, x => x.Value);
}

// The settings modules, in the order they are handled: the effects first (as the module resets its parts after an
// effect type), in XG the part modes before the drum setups (a setup applies to the parts assigned to it).
public static class ModuleSettings
{
    static IEnumerable<MidiEvent> One(MidiEvent e) { yield return e; }
    static int FirstChannel(Func<int, bool> uses) => Enumerable.Range(0, 16).FirstOrDefault(uses, -1);
    static int Mt32Channel(int part) => part < 9 && Mt32Effects.PartChannel[part] <= 15 ? Mt32Effects.PartChannel[part] : -1;
    // Back to the file for every fixed value, in the order of Fixed (the effect types before their parameters).
    static void ClearEach(SettingsModule m, IOutputDevice device)
    {
        foreach (var (key, _) in m.Fixed().ToList()) m.Clear(device, key);
    }
    // Fixed values of an effects module (key: parameter in C), the types first: a type resets the parameters of its
    // block in the module.
    static IEnumerable<(SettingKey, int)> FixedEffects(int count, Func<int, bool> isFixed, Func<int, int> value, Func<int, bool> isType) =>
        Enumerable.Range(0, count).Where(isFixed).OrderBy(p => !isType(p)).Select(p => (new SettingKey(0, 0, p), value(p)));

    // GS and XG effects (Effects tab: Reverb, Chorus, Delay, EQ, EFX; Variation, Insertion, Multi EQ). Sent in every
    // mode, as before the modules existed; written in the saved file whatever its mode.
    public static readonly SettingsModule GsXgFx = new()
    {
        Name = "e", Standard = null, IsActive = () => true,
        Fixed = () => FixedEffects(GsXgEffects.Count, GsXgEffects.IsFixed, GsXgEffects.FixedValue, GsXgEffects.IsType),
        Set = (d, k, v) => GsXgEffects.Set(d, k.C, v),
        Clear = (d, k) => GsXgEffects.Clear(d, k.C),
        ClearAll = d => ClearEach(GsXgFx, d),
        ResetFile = GsXgEffects.ResetFile, Apply = GsXgEffects.Apply,
        Events = (k, v) => One(GsXgEffects.ParamEvent(k.C, v)),
        ChannelOf = k => -1,
        Write = f => MidiFileEditor.ApplyEffects(f, Enumerable.Range(0, GsXgEffects.Count).Select(GsXgEffects.IsFixed).ToArray(),
                                                 Enumerable.Range(0, GsXgEffects.Count).Select(GsXgEffects.FixedValue).ToArray()),
    };

    // MT-32 reverb (Effects tab, Reverb page); the reverb switch of a part goes in the track of its channel.
    public static readonly SettingsModule Mt32Reverb = new()
    {
        Name = "m", Standard = "MT-32", IsActive = () => ChannelMixer.Mt32Mode,
        Fixed = () => FixedEffects(Mt32Effects.Count, Mt32Effects.IsFixed, Mt32Effects.FixedValue, p => false),
        Set = (d, k, v) => Mt32Effects.Set(d, k.C, v),
        Clear = (d, k) => Mt32Effects.Clear(d, k.C),
        ClearAll = d => ClearEach(Mt32Reverb, d),
        ResetFile = Mt32Effects.ResetFile, Apply = Mt32Effects.Apply,
        Events = (k, v) => Mt32Effects.ParamEvents(k.C, v),
        ChannelOf = k => k.C >= Mt32Effects.PartFirst ? Mt32Channel(k.C - Mt32Effects.PartFirst) : -1,
        Write = MidiFileEditor.WriteMt32Reverb,
    };

    // GM2 reverb and chorus (Effects tab, Reverb and Chorus pages).
    public static readonly SettingsModule Gm2Fx = new()
    {
        Name = "g", Standard = "GM2", IsActive = () => ChannelMixer.Gm2Mode,
        Fixed = () => FixedEffects(Gm2Effects.Count, Gm2Effects.IsFixed, Gm2Effects.FixedValue, Gm2Effects.IsType),
        Set = (d, k, v) => Gm2Effects.Set(d, k.C, v),
        Clear = (d, k) => Gm2Effects.Clear(d, k.C),
        ClearAll = d => ClearEach(Gm2Fx, d),
        ResetFile = Gm2Effects.ResetFile, Apply = Gm2Effects.Apply,
        Events = (k, v) => One(Gm2Effects.ParamEvent(k.C, v)),
        ChannelOf = k => -1,
        Write = MidiFileEditor.WriteGm2Effects,
    };

    public static readonly SettingsModule XgPart = new()
    {
        Name = "pe", Standard = "XG", IsActive = () => ChannelMixer.XgMode,
        Fixed = () => XgPartParams.Fixed().Select(x => (new SettingKey(x.Channel, 0, x.Param), x.Value)),
        Set = (d, k, v) => XgPartParams.Set(d, k.A, k.C, v),
        Clear = (d, k) => XgPartParams.Clear(d, k.A, k.C),
        ClearAll = d => XgPartParams.Clear(d),
        ResetFile = XgPartParams.ResetFile, Apply = XgPartParams.Apply,
        Events = (k, v) => One(XgPartParams.ParamEvent(k.A, k.C, v)),
        ChannelOf = k => k.A,
        Write = MidiFileEditor.WriteXgPartParams,
    };

    public static readonly SettingsModule XgPartModes = new()
    {
        Name = "pm", Standard = "XG", IsActive = () => ChannelMixer.XgMode,
        Fixed = () => Enumerable.Range(0, 16).Where(c => XgDrumSetup.PartModeOverride[c] >= 0)
                                .Select(c => (new SettingKey(c), XgDrumSetup.PartModeOverride[c])),
        Set = (d, k, v) => XgDrumSetup.SetPartMode(d, k.A, v),
        Clear = (d, k) => XgDrumSetup.ClearPartMode(d, k.A),
        ClearAll = d => XgDrumSetup.ClearPartMode(d),
        ResetFile = () => { },   // with the drum setups (XgDrumSetup.ResetFile)
        Apply = d => { foreach (var e in XgDrumSetup.PartModeEvents()) d.SendEvent(e); },
        Events = (k, v) => One(XgDrumSetup.PartModeEvent(k.A, v)),
        ChannelOf = k => k.A,
        Write = MidiFileEditor.WriteXgDrumSetup,
    };

    public static readonly SettingsModule XgDrums = new()
    {
        Name = "d", Standard = "XG", IsActive = () => ChannelMixer.XgMode,
        Fixed = () => XgDrumSetup.Fixed().Select(x => (new SettingKey(x.Setup, x.Note, x.Param), x.Value)),
        Set = (d, k, v) => XgDrumSetup.Set(d, k.A, k.B, k.C, v),
        Clear = (d, k) => XgDrumSetup.Clear(d, k.A, k.B, k.C),
        ClearAll = d => XgDrumSetup.Clear(d),
        ResetFile = XgDrumSetup.ResetFile,
        Apply = d => { foreach (var e in XgDrumSetup.FixedEvents()) d.SendEvent(e); },
        Events = (k, v) => One(XgDrumSetup.ParamEvent(k.A, k.B, k.C, v)),
        ChannelOf = k => FirstChannel(c => XgDrumSetup.SetupOf(c) == k.A),
        Write = MidiFileEditor.WriteXgDrumSetup,
    };

    public static readonly SettingsModule GsPart = new()
    {
        Name = "gp", Standard = "GS", IsActive = () => ChannelMixer.GsMode,
        Fixed = () => GsPartParams.Fixed().Select(x => (new SettingKey(x.Channel, 0, x.Param), x.Value)),
        Set = (d, k, v) => GsPartParams.Set(d, k.A, k.C, v),
        Clear = (d, k) => GsPartParams.Clear(d, k.A, k.C),
        ClearAll = d => GsPartParams.Clear(d),
        ResetFile = GsPartParams.ResetFile, Apply = GsPartParams.Apply,
        Events = (k, v) => One(GsPartParams.ParamEvent(k.A, k.C, v)),
        ChannelOf = k => k.A,
        Write = MidiFileEditor.WriteGsPartParams,
    };

    // A value of the drum set (never set by the file) can only go back by selecting the drum set again.
    public static readonly SettingsModule GsDrums = new()
    {
        Name = "gd", Standard = "GS", IsActive = () => ChannelMixer.GsMode,
        Fixed = () => GsDrumMap.Fixed().Select(x => (new SettingKey(x.Map, x.Note, x.Param), x.Value)),
        Set = (d, k, v) => GsDrumMap.Set(d, k.A, k.B, k.C, v),
        Clear = (d, k) => { foreach (int m in GsDrumMap.Clear(d, k.A, k.B, k.C)) ChannelMixer.ResetGsDrumMap(d, m); },
        ClearAll = d => { foreach (int m in GsDrumMap.Clear(d)) ChannelMixer.ResetGsDrumMap(d, m); },
        ResetFile = GsDrumMap.ResetFile, Apply = GsDrumMap.Apply,
        Events = (k, v) => One(GsDrumMap.ParamEvent(k.A, k.B, k.C, v)),
        ChannelOf = k => FirstChannel(c => GsDrumMap.MapOf(c) == k.A),
        Write = MidiFileEditor.WriteGsDrumMap,
    };

    public static readonly SettingsModule Gm2Part = new()
    {
        Name = "rp", Standard = "GM2", IsActive = () => ChannelMixer.Gm2Mode,
        Fixed = () => Gm2PartParams.Fixed().Select(x => (new SettingKey(x.Channel, 0, x.Param), x.Value)),
        Set = (d, k, v) => Gm2PartParams.Set(d, k.A, k.C, v),
        Clear = (d, k) => Gm2PartParams.Clear(d, k.A, k.C),
        ClearAll = d => Gm2PartParams.Clear(d),
        ResetFile = Gm2PartParams.ResetFile, Apply = Gm2PartParams.Apply,
        Events = (k, v) => Gm2PartParams.ParamEvents(k.A, k.C, v),
        ChannelOf = k => k.A,
        Write = MidiFileEditor.WriteGm2PartParams,
    };

    public static readonly SettingsModule Gm2Drums = new()
    {
        Name = "kd", Standard = "GM2", IsActive = () => ChannelMixer.Gm2Mode,
        Fixed = () => Gm2DrumParams.Fixed().Select(x => (new SettingKey(x.Channel, x.Note, x.Param), x.Value)),
        Set = (d, k, v) => Gm2DrumParams.Set(d, k.A, k.B, k.C, v),
        Clear = (d, k) => Gm2DrumParams.Clear(d, k.A, k.B, k.C),
        ClearAll = d => Gm2DrumParams.Clear(d),
        ResetFile = Gm2DrumParams.ResetFile, Apply = Gm2DrumParams.Apply,
        Events = (k, v) => One(Gm2DrumParams.ParamEvent(k.A, k.B, k.C, v)),
        ChannelOf = k => k.A,
        Write = MidiFileEditor.WriteGm2DrumParams,
    };

    public static readonly SettingsModule Mt32Part = new()
    {
        Name = "mp", Standard = "MT-32", IsActive = () => ChannelMixer.Mt32Mode,
        Fixed = () => Mt32PartParams.Fixed().Select(x => (new SettingKey(x.Part, 0, x.Param), x.Value)),
        Set = (d, k, v) => Mt32PartParams.Set(d, k.A, k.C, v),
        Clear = (d, k) => Mt32PartParams.Clear(d, k.A, k.C),
        ClearAll = d => Mt32PartParams.Clear(d),
        ResetFile = Mt32PartParams.ResetFile, Apply = Mt32PartParams.Apply,
        Events = (k, v) => One(Mt32PartParams.ParamEvent(k.A, k.C, v)),
        ChannelOf = k => Mt32Channel(k.A),
        Write = MidiFileEditor.WriteMt32PartParams,
    };

    public static readonly SettingsModule Mt32Drums = new()
    {
        Name = "md", Standard = "MT-32", IsActive = () => ChannelMixer.Mt32Mode,
        Fixed = () => Mt32DrumParams.Fixed().Select(x => (new SettingKey(0, x.Note, x.Param), x.Value)),
        Set = (d, k, v) => Mt32DrumParams.Set(d, k.B, k.C, v),
        Clear = (d, k) => Mt32DrumParams.Clear(d, k.B, k.C),
        ClearAll = d => Mt32DrumParams.Clear(d),
        ResetFile = Mt32DrumParams.ResetFile, Apply = Mt32DrumParams.Apply,
        Events = (k, v) => One(Mt32DrumParams.ParamEvent(k.B, k.C, v)),
        ChannelOf = k => Mt32Channel(Mt32PartParams.RhythmPart),
        Write = MidiFileEditor.WriteMt32DrumParams,
    };

    public static readonly SettingsModule[] All =
        { GsXgFx, Mt32Reverb, Gm2Fx, XgPart, XgPartModes, XgDrums, GsPart, GsDrums, Gm2Part, Gm2Drums, Mt32Part, Mt32Drums };

    // Reset of the module (start of a track, reset in the file): every module's file values back to defaults.
    public static void ResetFiles()
    {
        foreach (var m in All) m.ResetFile();
    }

    // Fixed values of the modules of the current mode (start of a track, seek, port change, after a reset).
    public static void Apply(IOutputDevice device)
    {
        foreach (var m in All) if (m.IsActive()) m.Apply(device);
    }
}
