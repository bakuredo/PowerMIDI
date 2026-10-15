using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// MT-32 rhythm setup (memory map of the Munt emulator, Structures.h): settings of each note of the rhythm part, keys 24 to
// 108, in the rhythm setup temporary area 03 01 10 + 4 × (key - 24): timbre (+0: 0-63 = memory timbres M1-M64, 64-93 =
// rhythm timbres R1-R30, 94 = off), output level (+1, 0..100), pan (+2, 0..14 = right..left) and reverb switch (+3).
// Set by the module's reset (its own rhythm map: "auto" here) and by the file's SysEx. Values fixed in the interface
// replace the file's. The reverb switch of all the keys is also in the Reverb page (Mt32Effects, rhythm part).
public static class Mt32DrumParams
{
    public const int FirstKey = 24, LastKey = 108, Keys = LastKey - FirstKey + 1;
    public const int Count = 4;
    public static readonly string[] Names = { "Timbre", "Level", "Pan", "Reverb" };
    public static readonly int[] Max = { 94, 100, 14, 1 };
    public const int Timbre = 0, Level = 1, Pan = 2, Reverb = 3;
    public const int TimbreOff = 94;

    // The 30 timbres of the MT-32 rhythm bank (R1-R30).
    public static readonly string[] RhythmTimbreNames =
    {
        "Acou BD", "Acou SD", "Acou Hi Tom", "Acou Mid Tom", "Acou Low Tom", "Elec SD", "Closed HH", "Open HH 1",
        "Crash Cym", "Ride Cym", "Rim Shot", "Hand Clap", "Cowbell", "Mt High Conga", "High Conga", "Low Conga",
        "High Timbale", "Low Timbale", "High Bongo", "Low Bongo", "High Agogo", "Low Agogo", "Tambourine", "Claves",
        "Maracas", "Smba Whis L", "Smba Whis S", "Cabasa", "Quijada", "Open HH 2",
    };

    const int RhythmTemp = (0x03 << 14) | (0x01 << 7) | 0x10;

    // Values by note (one group; note index = note - 24) and parameter; after a reset, those of the module's rhythm map
    // (unknown: -1).
    static readonly ValueStore store = new(1, Keys, Count, (g, k, p) => -1);

    public static void ResetFile() => store.ResetFile();

    // Values by note (24-108).
    public static int FileValue(int note, int p) => store.File(0, note - FirstKey, p);
    public static bool IsFixed(int note, int p) => store.IsFixed(0, note - FirstKey, p);
    public static int FixedValue(int note, int p) => store.FixedValue(0, note - FirstKey, p);
    public static int Value(int note, int p) => store.Value(0, note - FirstKey, p);
    public static bool HasFixed => store.HasFixed;
    public static bool IsNoteFixed(int note) => store.IsNoteFixed(0, note - FirstKey);

    public static IEnumerable<(int Note, int Param, int Value)> Fixed() => store.Fixed().Select(x => (x.N + FirstKey, x.P, x.Value));

    public static void Set(IOutputDevice device, int note, int p, int value)
    {
        store.Fix(0, note - FirstKey, p, value);
        device?.SendEvent(ParamEvent(note, p, value));
    }

    // Back to the file for one parameter, a note or everything (-1 = all): the file's values are sent again (a value of
    // the module's rhythm map, unknown here, cannot be: the module keeps the fixed one until its next reset).
    public static void Clear(IOutputDevice device, int note = -1, int param = -1)
    {
        foreach (var (_, k, p) in store.Unfix(0, note >= 0 ? note - FirstKey : -1, param))
            if (FileValue(k + FirstKey, p) >= 0) device?.SendEvent(ParamEvent(k + FirstKey, p, FileValue(k + FirstKey, p)));
    }

    public static void Apply(IOutputDevice device)
    {
        foreach (var e in FixedEvents()) device.SendEvent(e);
    }

    public static IEnumerable<MidiEvent> FixedEvents() => Fixed().Select(x => (MidiEvent)ParamEvent(x.Note, x.Param, x.Value));

    public static NormalSysExEvent ParamEvent(int note, int p, int value) =>
        Mt32Effects.SysEx(RhythmTemp + 4 * (note - FirstKey) + p, (byte)value);

    // ---------- Reading the file ----------

    // Values written by an MT-32 DT1 in the rhythm setup: (position, key index, parameter).
    static List<(int Position, int Key, int Param)> Positions(byte[] d)
    {
        var list = new List<(int, int, int)>();
        if (d.Length < 10 || d[0] != 0x41 || d[2] != 0x16 || d[3] != 0x12) return list;
        int address = d[4] << 14 | d[5] << 7 | d[6];
        for (int i = 7; i < d.Length - 2; i++)
        {
            int a = address + i - 7 - RhythmTemp;
            if (a >= 0 && a < 4 * Keys) list.Add((i, a / 4, a % 4));
        }
        return list;
    }

    // Copy of an MT-32 DT1 with its fixed values (checksum recomputed; null if it writes none, or the same values).
    public static byte[] WithFixedValues(byte[] d)
    {
        byte[] data = null;
        foreach (var (pos, k, p) in Positions(d))
        {
            if (!store.IsFixed(0, k, p) || d[pos] == store.FixedValue(0, k, p)) continue;
            data ??= (byte[])d.Clone();
            data[pos] = (byte)store.FixedValue(0, k, p);
        }
        if (data != null) MidiTools.FixRolandChecksum(data);
        return data;
    }

    // MT-32 DT1 played from the file: stores its values; returns it with the fixed values (null = unchanged).
    public static byte[] Read(byte[] d)
    {
        foreach (var (pos, k, p) in Positions(d)) store.SetFile(0, k, p, Math.Min(d[pos], Max[p]));
        return WithFixedValues(d);
    }
}
