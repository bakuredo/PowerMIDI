using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// GM2 reverb and chorus, set by the universal real time SysEx "Global Parameter Control":
//   F0 7F dev 04 05 01 01 01 | 01 ss | pp vv [pp vv …] F7
//   (slot path, parameter and value widths = 1; slot 01 01 = reverb, 01 02 = chorus).
// Parameters (index p):
//   0 = Reverb Type (0 Small Room, 1 Medium Room, 2 Large Room, 3 Medium Hall, 4 Large Hall, 8 Plate)   reverb pp 00
//   1 = Reverb Time (0-127: exp((v - 40) × 0.025) s)                                                    reverb pp 01
//   2 = Chorus Type (0 Chorus 1, 1 Chorus 2, 2 Chorus 3, 3 Chorus 4, 4 FB Chorus, 5 Flanger)            chorus pp 00
//   3 = Chorus Mod Rate (v × 0.122 Hz), 4 = Mod Depth ((v + 1) / 3.2 ms), 5 = Feedback (v × 0.763 %),
//   6 = Send To Reverb (v × 0.787 %)                                                                     chorus pp 01-04
// A type sets the other parameters of its block: they are then -1 ("auto", the type's default) until set.
// Values fixed in the interface are sent as GM2 SysEx and replace the file's.
public static class Gm2Effects
{
    public const int Count = 7;
    public const int ReverbType = 0, ChorusType = 2;
    // State after a GM2 System On: Large Hall, Chorus 3, the other parameters set by the types.
    public static readonly int[] Defaults = { 4, -1, 2, -1, -1, -1, -1 };

    static readonly int[] file = new int[Count];
    static readonly bool[] isFixed = new bool[Count];
    static readonly int[] fixedValue = new int[Count];

    static Gm2Effects() { ResetFile(); }

    // GM2 System On (start of a track, reset in the file): the file's values go back to the defaults.
    public static void ResetFile() => Array.Copy(Defaults, file, Count);

    public static int FileValue(int p) => file[p];
    public static bool IsFixed(int p) => isFixed[p];
    public static int FixedValue(int p) => fixedValue[p];
    public static int Value(int p) => isFixed[p] ? fixedValue[p] : file[p];
    public static bool HasFixed => isFixed.Any(f => f);

    public static bool IsType(int p) => p is ReverbType or ChorusType;
    // Block of a parameter: 0 = reverb, 1 = chorus.
    public static int Block(int p) => p < ChorusType ? 0 : 1;

    public static void Set(IOutputDevice device, int p, int value)
    {
        isFixed[p] = true;
        fixedValue[p] = value;
        if (IsType(p)) TypeChanged(file, p);
        if (device == null) return;
        device.SendEvent(ParamEvent(p, value));
        if (IsType(p)) SendFixedParams(device, Block(p));   // the type has just reset them in the module
    }

    // Back to the file: its value is sent again (if it is known).
    public static void Clear(IOutputDevice device, int p)
    {
        isFixed[p] = false;
        if (device == null || file[p] < 0) return;
        device.SendEvent(ParamEvent(p, file[p]));
        if (IsType(p)) SendFixedParams(device, Block(p));
    }

    // Sends the fixed values, types first (start of track, seek, port change, after a reset).
    public static void Apply(IOutputDevice device)
    {
        foreach (var e in FixedEvents()) device.SendEvent(e);
    }

    static void SendFixedParams(IOutputDevice device, int block)
    {
        foreach (var e in FixedParamEvents(block)) device.SendEvent(e);
    }

    // Fixed parameters of a block (not its type).
    static MidiEvent[] FixedParamEvents(int block) =>
        Enumerable.Range(0, Count).Where(p => Block(p) == block && !IsType(p) && isFixed[p]).Select(p => (MidiEvent)ParamEvent(p, fixedValue[p])).ToArray();

    // A type has just changed: the other parameters of its block go back to the type's defaults.
    static void TypeChanged(int[] values, int type)
    {
        for (int p = 0; p < Count; p++)
            if (Block(p) == Block(type) && !IsType(p)) values[p] = -1;
    }

    // ---------- GM2 SysEx ----------

    // Slot (01 = reverb, 02 = chorus) and parameter number of an index.
    static (byte Slot, byte Param) Address(int p) => p < ChorusType ? ((byte)1, (byte)p) : ((byte)2, (byte)(p - ChorusType));

    public static NormalSysExEvent ParamEvent(int p, int value)
    {
        var (slot, param) = Address(p);
        return new NormalSysExEvent(new byte[] { 0x7F, 0x7F, 0x04, 0x05, 0x01, 0x01, 0x01, 0x01, slot, param, (byte)value, 0xF7 });
    }

    public static IEnumerable<MidiEvent> FixedEvents()
    {
        foreach (bool types in new[] { true, false })
            for (int p = 0; p < Count; p++)
                if (isFixed[p] && IsType(p) == types) yield return ParamEvent(p, fixedValue[p]);
    }

    // ---------- Reading GM2 SysEx ----------

    // Applies a GM2 reverb / chorus SysEx to the given values. positions (optional) receives, for each value
    // byte, its position in the message and the parameter. Returns false if the message is not one.
    public static bool ApplyBytes(byte[] d, int[] values, List<(int Position, int Param)> positions = null)
    {
        // 7F dev 04 05 sw pw vw [slot path] (pp vv)… F7, with widths 1 and slot path 01 01 / 01 02.
        if (d == null || d.Length < 12 || d[0] != 0x7F || d[2] != 0x04 || d[3] != 0x05) return false;
        if (d[4] != 1 || d[5] != 1 || d[6] != 1 || d[7] != 1 || d[8] is not (1 or 2)) return false;
        int first = d[8] == 1 ? 0 : ChorusType, last = d[8] == 1 ? 1 : 6;
        bool any = false;
        for (int i = 9; i + 1 < d.Length - 1; i += 2)   // pairs pp vv, then F7
        {
            int p = first + d[i];
            if (p > last) continue;
            values[p] = d[i + 1];
            if (IsType(p)) TypeChanged(values, p);
            positions?.Add((i + 1, p));
            any = true;
        }
        return any;
    }

    // Copy of a GM2 SysEx with its bytes of fixed parameters replaced, or null if it writes no fixed parameter.
    public static byte[] WithFixedValues(byte[] d, List<(int Position, int Param)> positions)
    {
        byte[] data = null;
        foreach (var (position, param) in positions)
        {
            if (!isFixed[param] || d[position] == fixedValue[param]) continue;
            data ??= (byte[])d.Clone();
            data[position] = (byte)fixedValue[param];
        }
        return data;
    }

    // GM2 SysEx played from the file: stores its values; returns the message to play with the fixed values
    // instead of the file's (null = unchanged). A type set by the file resets its block in the module: the
    // fixed parameters of that block are sent again right after.
    public static MidiEvent Read(byte[] d)
    {
        // Port read first: the first use of ChannelMixer resets the effect values (static initialization).
        var device = ChannelMixer.Device;
        var positions = new List<(int Position, int Param)>();
        if (!ApplyBytes(d, file, positions)) return null;
        foreach (int block in positions.Where(x => IsType(x.Param)).Select(x => Block(x.Param)).Distinct())
            if (device != null) ChannelMixer.SendLater(FixedParamEvents(block));
        var data = WithFixedValues(d, positions);
        return data != null ? new NormalSysExEvent(data) : null;
    }
}
