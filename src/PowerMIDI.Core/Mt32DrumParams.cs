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

    static readonly int[,] file = new int[Keys, Count];
    static readonly bool[,] isFixed = new bool[Keys, Count];
    static readonly int[,] fixedValue = new int[Keys, Count];
    static int fixedCount;

    static Mt32DrumParams() { ResetFile(); }

    public static void ResetFile()
    {
        for (int k = 0; k < Keys; k++)
            for (int p = 0; p < Count; p++) file[k, p] = -1;
    }

    // Values by note (24-108).
    public static int FileValue(int note, int p) => file[note - FirstKey, p];
    public static bool IsFixed(int note, int p) => isFixed[note - FirstKey, p];
    public static int FixedValue(int note, int p) => fixedValue[note - FirstKey, p];
    public static int Value(int note, int p) => IsFixed(note, p) ? FixedValue(note, p) : FileValue(note, p);
    public static bool HasFixed => fixedCount > 0;
    public static bool IsNoteFixed(int note) => Enumerable.Range(0, Count).Any(p => IsFixed(note, p));

    public static IEnumerable<(int Note, int Param, int Value)> Fixed()
    {
        for (int k = 0; k < Keys; k++)
            for (int p = 0; p < Count; p++)
                if (isFixed[k, p]) yield return (k + FirstKey, p, fixedValue[k, p]);
    }

    public static void Set(IOutputDevice device, int note, int p, int value)
    {
        int k = note - FirstKey;
        if (!isFixed[k, p]) fixedCount++;
        isFixed[k, p] = true;
        fixedValue[k, p] = value;
        device?.SendEvent(ParamEvent(note, p, value));
    }

    // Back to the file for one parameter, a note or everything (-1 = all): the file's values are sent again (a value of
    // the module's rhythm map, unknown here, cannot be: the module keeps the fixed one until its next reset).
    public static void Clear(IOutputDevice device, int note = -1, int param = -1)
    {
        for (int k = 0; k < Keys; k++)
            for (int p = 0; p < Count; p++)
            {
                if (!isFixed[k, p] || (note >= 0 && k != note - FirstKey) || (param >= 0 && p != param)) continue;
                isFixed[k, p] = false;
                fixedCount--;
                if (file[k, p] >= 0) device?.SendEvent(ParamEvent(k + FirstKey, p, file[k, p]));
            }
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
            if (!isFixed[k, p] || d[pos] == fixedValue[k, p]) continue;
            data ??= (byte[])d.Clone();
            data[pos] = (byte)fixedValue[k, p];
        }
        if (data != null) MidiTools.FixRolandChecksum(data);
        return data;
    }

    // MT-32 DT1 played from the file: stores its values; returns it with the fixed values (null = unchanged).
    public static byte[] Read(byte[] d)
    {
        foreach (var (pos, k, p) in Positions(d)) file[k, p] = Math.Min(d[pos], Max[p]);
        return WithFixedValues(d);
    }
}
