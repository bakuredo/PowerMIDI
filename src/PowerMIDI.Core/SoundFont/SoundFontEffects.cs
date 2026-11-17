using System.Globalization;

namespace PowerMIDI.Core;

// Reverb and chorus of the SoundFont engine (FluidSynth), set in the Effects tab in SF2 mode. They are settings of the
// engine, not of a track: kept from one track to the next, remembered in PowerMIDI.ini ([SoundFont]). A value is the
// position of its slider (a whole number); FluidSynth's value is position × Scale.
// Defaults: those recommended for FluidSynth by the author of GeneralUser GS (its README, section 3.0.2): the reverb of
// FluidSynth 2.4 (room size 0.5, damping 0.3, width 0.8, level 0.7) and a softer chorus than FluidSynth's (4 voices
// instead of 3, level 0.55 instead of 0.6, speed 0.36 Hz instead of 0.2, depth 3.6 ms instead of 4.25).
public static class SoundFontEffects
{
    // Block (Reverb / Chorus), label, INI key, slider range and default (positions), scale, decimals and unit of the
    // text; Choices: a list instead of a slider (its texts, by value).
    public sealed record Param(string Block, string Label, string Key, int Min, int Max, int Default, double Scale,
                               int Decimals = 0, string Unit = "", string[] Choices = null);

    public const int ReverbOn = 0, RoomSize = 1, Damping = 2, Width = 3, ReverbLevel = 4,
                     ChorusOn = 5, Voices = 6, ChorusLevel = 7, Speed = 8, Depth = 9, Wave = 10;
    public const int ChorusFirst = ChorusOn;   // the chorus page: ChorusFirst .. Count - 1

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
    };
    public static int Count => Params.Length;

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

    // Given to the engine (null: none). Called after each change: a few calls, the engine applies them at once.
    public static void Apply(SoundFontOutput engine)
    {
        engine?.SetReverb(values[ReverbOn] == 1, Real(RoomSize), Real(Damping), Real(Width), Real(ReverbLevel));
        engine?.SetChorus(values[ChorusOn] == 1, values[Voices], Real(ChorusLevel), Real(Speed), Real(Depth), values[Wave]);
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
