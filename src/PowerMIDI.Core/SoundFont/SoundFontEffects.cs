using System.Globalization;

namespace PowerMIDI.Core;

// Reverb and chorus of the SoundFont engine (FluidSynth), set in the Effects tab in SF2 mode. They are settings of the
// engine, not of a track: kept from one track to the next, remembered in PowerMIDI.ini ([SoundFont]). A value is the
// position of its slider (a whole number); FluidSynth's value is position × Scale.
// Defaults: those recommended for FluidSynth by the author of GeneralUser GS (its README, section 3.0.2): the reverb of
// FluidSynth 2.4 (room size 0.5, damping 0.3, width 0.8, level 0.7) and a softer chorus than FluidSynth's (4 voices
// instead of 3, level 0.55 instead of 0.6, speed 0.36 Hz instead of 0.2, depth 3.6 ms instead of 4.25).
// Global send (%): a send to the reverb / chorus added to every note of all the channels, whatever their CC91 / CC93 (a
// file that leaves them at 0 gets some); 0 by default (see SoundFontOutput.SetGlobalSends).
public static class SoundFontEffects
{
    // Block (Reverb / Chorus), label, INI key, slider range and default (positions), scale, decimals and unit of the
    // text; Choices: a list instead of a slider (its texts, by value).
    public sealed record Param(string Block, string Label, string Key, int Min, int Max, int Default, double Scale,
                               int Decimals = 0, string Unit = "", string[] Choices = null);

    public const int ReverbOn = 0, RoomSize = 1, Damping = 2, Width = 3, ReverbLevel = 4,
                     ChorusOn = 5, Voices = 6, ChorusLevel = 7, Speed = 8, Depth = 9, Wave = 10,
                     ReverbSend = 11, ChorusSend = 12;
    // The effect itself (and its presets): reverb ReverbOn .. ChorusFirst - 1, chorus ChorusFirst .. ChorusEnd - 1. Then
    // the global sends, one per page.
    public const int ChorusFirst = ChorusOn, ChorusEnd = Wave + 1;

    static readonly string[] OffOn = { "Off", "On" };

    public static readonly Param[] Params =
    {
        new("Reverb", "Reverb", "ReverbOn", 0, 1, 1, 1, Choices: OffOn),
        new("Reverb", "Room size", "ReverbRoomSize", 0, 100, 50, 0.01, 2),
        new("Reverb", "Damping", "ReverbDamping", 0, 100, 30, 0.01, 2),
        new("Reverb", "Width", "ReverbWidth", 0, 100, 8, 0.1, 1),
        new("Reverb", "Level", "ReverbLevel", 0, 100, 70, 0.01, 2),
        new("Chorus", "Chorus", "ChorusOn", 0, 1, 1, 1, Choices: OffOn),
        new("Chorus", "Voices", "ChorusVoices", 0, 99, 4, 1),
        new("Chorus", "Level", "ChorusLevel", 0, 200, 55, 0.01, 2),
        new("Chorus", "Speed", "ChorusSpeed", 10, 500, 36, 0.01, 2, " Hz"),
        new("Chorus", "Depth", "ChorusDepth", 0, 210, 36, 0.1, 1, " ms"),
        new("Chorus", "Wave", "ChorusWave", 0, 1, 0, 1, Choices: new[] { "Sine", "Triangle" }),
        new("Reverb", "Global send", "ReverbGlobalSend", 0, 100, 0, 1, 0, " %"),
        new("Chorus", "Global send", "ChorusGlobalSend", 0, 100, 0, 1, 0, " %"),
    };
    public static int Count => Params.Length;

    // ---------- Presets ----------
    // A preset of a page (Reverb: the parameters 0 .. ChorusFirst - 1; Chorus: ChorusFirst .. ChorusEnd - 1; not the
    // global sends, which are not the character of the effect): its name and the positions of those parameters, in
    // order. The first one: the defaults; the others: the GS macros of
    // the SC-88 / SC-8850, as FluidSynth can approach them (its reverb is of the Freeverb kind, its chorus a plain one),
    // set by ear as starting points. Not offered: the GS Delay and Panning Delay (FluidSynth's reverb has no echo),
    // Flanger and Short Delay (its chorus has no feedback).
    public sealed record Preset(string Name, int[] Values);

    static int[] Defaults(int first, int last) => Params[first..last].Select(p => p.Default).ToArray();

    // On, room size, damping, width, level.
    public static readonly Preset[] ReverbPresets =
    {
        new("Default", Defaults(0, ChorusFirst)),
        new("Room 1", new[] { 1, 30, 50, 6, 60 }),
        new("Room 2", new[] { 1, 40, 45, 7, 65 }),
        new("Room 3", new[] { 1, 55, 60, 8, 65 }),
        new("Hall 1", new[] { 1, 70, 35, 10, 70 }),
        new("Hall 2", new[] { 1, 85, 25, 10, 75 }),
        new("Plate", new[] { 1, 60, 10, 10, 65 }),
    };

    // On, voices, level, speed, depth, wave.
    public static readonly Preset[] ChorusPresets =
    {
        new("Default", Defaults(ChorusFirst, ChorusEnd)),
        new("Chorus 1", new[] { 1, 3, 50, 37, 20, 0 }),
        new("Chorus 2", new[] { 1, 3, 60, 110, 50, 0 }),
        new("Chorus 3", new[] { 1, 4, 70, 37, 60, 0 }),
        new("Chorus 4", new[] { 1, 5, 75, 110, 45, 0 }),
        new("Feedback Chorus", new[] { 1, 6, 90, 24, 80, 0 }),
    };

    // The presets of the page starting at parameter first (0 or ChorusFirst).
    public static Preset[] PresetsOf(int first) => first == ChorusFirst ? ChorusPresets : ReverbPresets;

    // The preset the values of a page are (its place in the list), or -1: values set by hand.
    public static int PresetIndex(int first) =>
        Array.FindIndex(PresetsOf(first), preset => preset.Values.Select((v, i) => values[first + i] == v).All(same => same));

    // Sets the values of a page to those of a preset (not given to the engine: see Apply).
    public static void SetPreset(int first, int index)
    {
        var preset = PresetsOf(first)[index];
        for (int i = 0; i < preset.Values.Length; i++) Set(first + i, preset.Values[i]);
    }

    static readonly int[] values = Params.Select(p => p.Default).ToArray();

    public static int Value(int p) => values[p];
    public static double Real(int p) => values[p] * Params[p].Scale;
    public static bool IsChanged(int p) => values[p] != Params[p].Default;

    public static void Set(int p, int value) => values[p] = Math.Clamp(value, Params[p].Min, Params[p].Max);
    public static void Clear(int p) => values[p] = Params[p].Default;
    public static void Reset() { for (int p = 0; p < Count; p++) Clear(p); }

    // Text of a value: its choice, or FluidSynth's value with its unit (e.g. "0.36 Hz").
    public static string Format(int p, int value)
    {
        var param = Params[p];
        if (param.Choices != null) return value >= 0 && value < param.Choices.Length ? param.Choices[value] : value.ToString();
        return (value * param.Scale).ToString("F" + param.Decimals, CultureInfo.InvariantCulture) + param.Unit;
    }

    // Given to the engine (null: none). Called after each change: a few calls, the engine applies them at once (the
    // global sends: to the notes played from then on).
    public static void Apply(SoundFontOutput engine)
    {
        engine?.SetReverb(values[ReverbOn] == 1, Real(RoomSize), Real(Damping), Real(Width), Real(ReverbLevel));
        engine?.SetChorus(values[ChorusOn] == 1, values[Voices], Real(ChorusLevel), Real(Speed), Real(Depth), values[Wave]);
        engine?.SetGlobalSends(values[ReverbSend], values[ChorusSend]);
    }

    // ---------- PowerMIDI.ini: FluidSynth's values (e.g. ChorusSpeed=0.36), only those changed ----------
    public static void Read(IniFile ini)
    {
        for (int p = 0; p < Count; p++)
        {
            var text = ini.Get("SoundFont", Params[p].Key);
            values[p] = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
                ? Math.Clamp((int)Math.Round(real / Params[p].Scale), Params[p].Min, Params[p].Max)
                : Params[p].Default;
        }
    }

    public static void Write(IniFile ini)
    {
        for (int p = 0; p < Count; p++)
            if (IsChanged(p)) ini.Set("SoundFont", Params[p].Key, Real(p).ToString("0.###", CultureInfo.InvariantCulture));
    }
}
