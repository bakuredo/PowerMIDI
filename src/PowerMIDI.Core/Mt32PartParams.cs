using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// MT-32 part parameters of the Effects tab's Part page (memory map of the Munt emulator, Structures.h):
// - patch temporary area 03 00 00 + 16 × part (parts 1-8): key shift (+2, 0..48 = -24..+24 semitones), fine tune
//   (+3, 0..100 = -50..+50 cents), bender range (+4, 0..24 semitones), assign mode (+5, POLY 1..4);
// - system area 10 00 04..0C: partial reserve of parts 1-8 and of the rhythm part (0..32 partials, 32 in all).
// A Program Change loads the patch temporary area of the part from its patch: its values are then the patch's
// (unknown here: "auto") and the fixed ones are sent again. Values fixed in the interface replace the file's.
public static class Mt32PartParams
{
    public const int Parts = 9, RhythmPart = 8;   // index 8: the rhythm part (partial reserve only)
    public const int Count = 5;
    public static readonly string[] Names = { "Key Shift", "Fine Tune", "Bender Range", "Assign Mode", "Partial Reserve" };
    public const int KeyShift = 0, FineTune = 1, BenderRange = 2, AssignMode = 3, PartialReserve = 4;
    public static readonly int[] Max = { 48, 100, 24, 3, 32 };
    // After an MT-32 reset: the patch's values ("auto"), the factory partial reserve (3, 10, 6, 4, 3, 0, 0, 0, rhythm 6).
    static readonly int[] DefaultReserve = { 3, 10, 6, 4, 3, 0, 0, 0, 6 };
    public static bool IsPatchParam(int p) => p < PartialReserve;

    const int PatchTemp = 0x03 << 14, SystemArea = 0x10 << 14;

    static readonly int[,] file = new int[Parts, Count];
    static readonly bool[,] isFixed = new bool[Parts, Count];
    static readonly int[,] fixedValue = new int[Parts, Count];

    static Mt32PartParams() { ResetFile(); }

    public static void ResetFile()
    {
        for (int part = 0; part < Parts; part++)
        {
            for (int p = 0; p < PartialReserve; p++) file[part, p] = -1;
            file[part, PartialReserve] = DefaultReserve[part];
        }
    }

    public static int FileValue(int part, int p) => file[part, p];
    public static bool IsFixed(int part, int p) => isFixed[part, p];
    public static int FixedValue(int part, int p) => fixedValue[part, p];
    public static int Value(int part, int p) => isFixed[part, p] ? fixedValue[part, p] : file[part, p];
    public static bool IsPartFixed(int part) => Enumerable.Range(0, Count).Any(p => isFixed[part, p]);
    public static bool HasFixed => Enumerable.Range(0, Parts).Any(IsPartFixed);
    // Partials reserved by all the parts (the MT-32 has 32).
    public static int ReserveTotal => Enumerable.Range(0, Parts).Sum(part => Math.Max(0, Value(part, PartialReserve)));

    public static void Set(IOutputDevice device, int part, int p, int value)
    {
        isFixed[part, p] = true;
        fixedValue[part, p] = value;
        device?.SendEvent(ParamEvent(part, p, value));
    }

    // Back to the file for one parameter, a part or everything (-1 = all): the file's values are sent again (a value of
    // the patch, unknown here, cannot be: the module keeps the fixed one until the part's next Program Change).
    public static void Clear(IOutputDevice device, int part = -1, int param = -1)
    {
        for (int q = 0; q < Parts; q++)
            for (int p = 0; p < Count; p++)
            {
                if (!isFixed[q, p] || (part >= 0 && q != part) || (param >= 0 && p != param)) continue;
                isFixed[q, p] = false;
                if (file[q, p] >= 0) device?.SendEvent(ParamEvent(q, p, file[q, p]));
            }
    }

    public static void Apply(IOutputDevice device)
    {
        foreach (var e in FixedEvents()) device.SendEvent(e);
    }

    public static IEnumerable<(int Part, int Param, int Value)> Fixed()
    {
        for (int part = 0; part < Parts; part++)
            for (int p = 0; p < Count; p++)
                if (isFixed[part, p]) yield return (part, p, fixedValue[part, p]);
    }

    public static IEnumerable<MidiEvent> FixedEvents() => Fixed().Select(x => (MidiEvent)ParamEvent(x.Part, x.Param, x.Value));

    // The fixed patch values of a part (sent again after its Program Change).
    public static IEnumerable<MidiEvent> FixedPatchEvents(int part) =>
        Enumerable.Range(0, PartialReserve).Where(p => isFixed[part, p]).Select(p => (MidiEvent)ParamEvent(part, p, fixedValue[part, p]));

    static int AddressOf(int part, int p) => p == PartialReserve ? SystemArea + 4 + part : PatchTemp + 16 * part + 2 + p;

    public static NormalSysExEvent ParamEvent(int part, int p, int value) => Mt32Effects.SysEx(AddressOf(part, p), (byte)value);

    // Part (0-7) whose patch a channel plays, or -1.
    public static int PartOf(int channel)
    {
        for (int part = 0; part < 8; part++) if (Mt32Effects.PartChannel[part] == channel) return part;
        return -1;
    }

    // ---------- Reading the file ----------

    // Values written by an MT-32 DT1: (position, part, parameter) for each byte of a parameter shown.
    static List<(int Position, int Part, int Param)> Positions(byte[] d)
    {
        var list = new List<(int, int, int)>();
        if (d.Length < 10 || d[0] != 0x41 || d[2] != 0x16 || d[3] != 0x12) return list;
        int address = d[4] << 14 | d[5] << 7 | d[6];
        for (int i = 7; i < d.Length - 2; i++)
        {
            int a = address + i - 7;
            if (a >= SystemArea + 4 && a < SystemArea + 4 + Parts) list.Add((i, a - SystemArea - 4, PartialReserve));
            else if (a >= PatchTemp && a < PatchTemp + 16 * 8 && (a - PatchTemp) % 16 is >= 2 and <= 5)
                list.Add((i, (a - PatchTemp) / 16, (a - PatchTemp) % 16 - 2));
        }
        return list;
    }

    // Copy of an MT-32 DT1 with its fixed values (checksum recomputed; null if it writes none, or the same values).
    public static byte[] WithFixedValues(byte[] d)
    {
        byte[] data = null;
        foreach (var (pos, part, p) in Positions(d))
        {
            if (!isFixed[part, p] || d[pos] == fixedValue[part, p]) continue;
            data ??= (byte[])d.Clone();
            data[pos] = (byte)fixedValue[part, p];
        }
        if (data != null) MidiTools.FixRolandChecksum(data);
        return data;
    }

    // MT-32 DT1 played from the file: stores its values; returns it with the fixed values (null = unchanged).
    public static byte[] Read(byte[] d)
    {
        foreach (var (pos, part, p) in Positions(d)) file[part, p] = Math.Min(d[pos], Max[p]);
        return WithFixedValues(d);
    }

    // Program Change on a channel: the patch values of its part are now the patch's. Returns that part if it has fixed
    // patch values to send again, otherwise -1.
    public static int ProgramChanged(int channel)
    {
        int part = PartOf(channel);
        if (part < 0) return -1;
        for (int p = 0; p < PartialReserve; p++) file[part, p] = -1;
        return Enumerable.Range(0, PartialReserve).Any(p => isFixed[part, p]) ? part : -1;
    }
}
