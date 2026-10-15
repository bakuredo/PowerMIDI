using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// MT-32 / CM-32L reverb, the module's only effect, set by MT-32 SysEx (Roland DT1, model 16h, device 10h).
// Parameters (index p):
//   0 = Reverb Mode (0 Room, 1 Hall, 2 Plate, 3 Tap Delay)      system area 10 00 01
//   1 = Reverb Time (0-7)                                       10 00 02
//   2 = Reverb Level (0-7)                                      10 00 03
//   3..10 = reverb switch of parts 1-8 (0 off / 1 on)           patch temporary area 03 00 06 + 16 × part
//   11 = reverb switch of the rhythm part (last key written)    rhythm setup temporary area 03 01 10 + 4 × key + 3
// The MIDI channels of the parts come from 10 00 0D-15 (parts 1-8, rhythm; 16 = off); default: channels 2-9 and 10.
// Values fixed in the interface are sent as MT-32 SysEx and replace the file's.
public static class Mt32Effects
{
    public const int Count = 12;
    public const int PartFirst = 3;      // parts 1-8: 3..10
    public const int RhythmParam = 11;
    const int RhythmKeys = 85;           // rhythm setup: keys 24-108, 4 bytes each
    public static readonly string[] ModeNames = { "Room", "Hall", "Plate", "Tap Delay" };
    // State after an MT-32 reset: Room, time 5, level 3, reverb on every part.
    public static readonly int[] Defaults = { 0, 5, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1 };

    // Values by parameter (one group, one note).
    static readonly ValueStore store = new(1, 1, Count, (g, n, p) => Defaults[p]);
    // MIDI channel (0-15, 16 = off) of parts 1-8 (index 0-7) and of the rhythm part (index 8).
    public static readonly int[] PartChannel = new int[9];

    static Mt32Effects() { DefaultChannels(PartChannel); }

    // MT-32 reset (start of a track): the file's values go back to the defaults.
    public static void ResetFile()
    {
        store.ResetFile();
        DefaultChannels(PartChannel);
    }

    public static void DefaultChannels(int[] channels)
    {
        for (int p = 0; p < 8; p++) channels[p] = p + 1;
        channels[8] = 9;
    }

    public static int FileValue(int p) => store.File(0, 0, p);
    public static bool IsFixed(int p) => store.IsFixed(0, 0, p);
    public static int FixedValue(int p) => store.FixedValue(0, 0, p);
    public static int Value(int p) => store.Value(0, 0, p);
    public static bool HasFixed => store.HasFixed;

    public static void Set(IOutputDevice device, int p, int value)
    {
        store.Fix(0, 0, p, value);
        if (device != null) foreach (var e in ParamEvents(p, value)) device.SendEvent(e);
    }

    // Back to the file: its value is sent again.
    public static void Clear(IOutputDevice device, int p)
    {
        store.Unfix(0, 0, p);
        if (device != null) foreach (var e in ParamEvents(p, FileValue(p))) device.SendEvent(e);
    }

    // Sends the fixed values (start of track, seek, port change, after a reset).
    public static void Apply(IOutputDevice device)
    {
        foreach (var e in FixedEvents()) device.SendEvent(e);
    }

    // ---------- MT-32 SysEx ----------

    const int SystemArea = 0x10 << 14, PatchTemp = 0x03 << 14, RhythmTemp = (0x03 << 14) | (0x01 << 7) | 0x10;

    // DT1 message writing data at a 21-bit MT-32 address, with its Roland checksum.
    public static NormalSysExEvent SysEx(int address, params byte[] data)
    {
        var d = new List<byte> { 0x41, 0x10, 0x16, 0x12, (byte)(address >> 14 & 0x7F), (byte)(address >> 7 & 0x7F), (byte)(address & 0x7F) };
        d.AddRange(data);
        d.Add(0);
        d.Add(0xF7);
        var bytes = d.ToArray();
        MidiTools.FixRolandChecksum(bytes);   // same checksum (address + data) as GS
        return new NormalSysExEvent(bytes);
    }

    // Messages writing one parameter. The rhythm part's switch is written on every key.
    public static IEnumerable<MidiEvent> ParamEvents(int p, int value)
    {
        if (p < PartFirst) yield return SysEx(SystemArea + 1 + p, (byte)value);
        else if (p < RhythmParam) yield return SysEx(PatchTemp + 16 * (p - PartFirst) + 6, (byte)value);
        else for (int key = 0; key < RhythmKeys; key++) yield return SysEx(RhythmTemp + 4 * key + 3, (byte)value);
    }

    public static IEnumerable<MidiEvent> FixedEvents()
    {
        foreach (var (_, _, p, value) in store.Fixed())
            foreach (var e in ParamEvents(p, value)) yield return e;
    }

    // ---------- Reading MT-32 SysEx ----------

    [Flags]
    public enum Changes { None = 0, Reverb = 1, Parts = 2 }

    // Applies an MT-32 DT1 SysEx to the given values and part channels. positions (optional) receives,
    // for each byte written to a parameter, its position in the message and the parameter.
    public static Changes ApplyBytes(byte[] d, int[] values, int[] channels, List<(int Position, int Param)> positions = null)
    {
        if (d == null || d.Length < 10 || d[0] != 0x41 || d[2] != 0x16 || d[3] != 0x12) return Changes.None;
        int address = d[4] << 14 | d[5] << 7 | d[6];
        var changes = Changes.None;
        for (int i = 7; i < d.Length - 2; i++)   // … data, checksum, F7
        {
            int a = address + i - 7, v = d[i];
            int param = -1;
            if (a >= SystemArea + 1 && a <= SystemArea + 3) { param = a - SystemArea - 1; v = param == 0 ? Math.Min(v, 3) : Math.Min(v, 7); }
            else if (a >= SystemArea + 0x0D && a <= SystemArea + 0x15) { channels[a - SystemArea - 0x0D] = Math.Min(v, 16); changes |= Changes.Parts; }
            else if (a >= PatchTemp && a < PatchTemp + 128 && (a - PatchTemp) % 16 == 6) param = PartFirst + (a - PatchTemp) / 16;
            else if (a >= RhythmTemp && a < RhythmTemp + RhythmKeys * 4 && (a - RhythmTemp) % 4 == 3) param = RhythmParam;
            if (param < 0) continue;
            if (param >= PartFirst) v = v == 0 ? 0 : 1;
            values[param] = v;
            positions?.Add((i, param));
            changes |= param < PartFirst ? Changes.Reverb : Changes.Parts;
        }
        return changes;
    }

    // Copy of an MT-32 SysEx with its bytes of fixed parameters replaced (checksum recomputed), or null if
    // it writes no fixed parameter.
    public static byte[] WithFixedValues(byte[] d, List<(int Position, int Param)> positions)
    {
        byte[] data = null;
        foreach (var (position, param) in positions)
        {
            if (!IsFixed(param) || d[position] == FixedValue(param)) continue;
            data ??= (byte[])d.Clone();
            data[position] = (byte)FixedValue(param);
        }
        if (data != null) MidiTools.FixRolandChecksum(data);
        return data;
    }

    // MT-32 SysEx played from the file: stores its values; returns the message to play with the fixed values
    // instead of the file's (null = unchanged).
    public static MidiEvent Read(byte[] d)
    {
        var positions = new List<(int Position, int Param)>();
        var values = store.Files(0, 0);
        if (ApplyBytes(d, values, PartChannel, positions) == Changes.None) return null;
        store.SetFiles(0, 0, values);
        var data = WithFixedValues(d, positions);
        return data != null ? new NormalSysExEvent(data) : null;
    }
}
