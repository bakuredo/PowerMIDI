using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// GS part parameters of the Effects tab's Part page (those the Mixer does not show), for parts 1-16 (part taken as the
// channel of the same number; SC-8850 manual p. 237-239). Written by DT1 40 1x aa / 40 2x aa (x: part block, as for
// 40 1x: 1..9 = parts 1..9, 0 = part 10, A..F = parts 11..16), and some also by:
// - RPN 00 00 (pitch bend range, 0..24 semitones = Bend Pitch Control 40..58), RPN 00 01 (fine tuning: its MSB);
// - CC126 / CC127 (mono / poly).
// Reset by a GS Reset or a GM On. Values fixed in the interface replace the file's. The EQ of a part (40 4x 20) stays
// in GsXgEffects; the Part page shows it.
public static class GsPartParams
{
    public sealed record Param(string Group, string Name, int Block, int Address, int Default, int Min, int Max, bool TwoNibbles = false);

    // Pitch Offset Fine: 2 bytes of 4 bits (17: bits 7-4, 18: bits 3-0), 80h = 0 Hz, ±12.0 Hz in 0.1 Hz steps.
    // Pitch Fine Tune: its MSB only (2A; the LSB, 2B, is written 0): 40h = 0, ±100 cents.
    // Assign Mode: -1 = depends on the part (single for a drum part, full multi otherwise).
    public static readonly Param[] Params =
    {
        new("Tuning", "Key Shift", 0x10, 0x16, 64, 0x28, 0x58),
        new("Tuning", "Offset Fine", 0x10, 0x17, 0x80, 0x08, 0xF8, TwoNibbles: true),
        new("Tuning", "Fine Tune", 0x10, 0x2A, 64, 0, 127),
        new("Tuning", "Bend Range", 0x20, 0x10, 0x42, 0x40, 0x58),
        new("Play", "Mono / Poly", 0x10, 0x13, 1, 0, 1),
        new("Play", "Assign Mode", 0x10, 0x14, -1, 0, 2),
        new("Play", "Velocity Sense Depth", 0x10, 0x1A, 64, 0, 127),
        new("Play", "Velocity Sense Offset", 0x10, 0x1B, 64, 0, 127),
        new("Play", "Key Range Low", 0x10, 0x1D, 0, 0, 127),
        new("Play", "Key Range High", 0x10, 0x1E, 127, 0, 127),
    };
    public static int Count => Params.Length;
    public const int KeyShift = 0, OffsetFine = 1, FineTune = 2, BendRange = 3, MonoPoly = 4, AssignMode = 5, KeyRangeLow = 8, KeyRangeHigh = 9;

    // Values by channel (group) and parameter (one note).
    static readonly ValueStore store = new(16, 1, Params.Length, (c, n, p) => Params[p].Default);

    public static void ResetFile() => store.ResetFile();

    public static int FileValue(int c, int p) => store.File(c, 0, p);
    public static bool IsFixed(int c, int p) => store.IsFixed(c, 0, p);
    public static int FixedValue(int c, int p) => store.FixedValue(c, 0, p);
    public static int Value(int c, int p) => store.Value(c, 0, p);
    public static bool IsChannelFixed(int c) => store.IsGroupFixed(c);
    public static bool HasFixed => store.HasFixed;

    public static void Set(IOutputDevice device, int c, int p, int value)
    {
        store.Fix(c, 0, p, value);
        device?.SendEvent(ParamEvent(c, p, value));
    }

    // Back to the file for one parameter, a channel or everything (-1 = all): the file's values are sent again (an
    // assign mode never set by the file cannot be: the module keeps the fixed one until the next reset).
    public static void Clear(IOutputDevice device, int channel = -1, int param = -1)
    {
        foreach (var (c, _, p) in store.Unfix(channel, -1, param))
            if (FileValue(c, p) >= 0) device?.SendEvent(ParamEvent(c, p, FileValue(c, p)));
    }

    public static void Apply(IOutputDevice device)
    {
        foreach (var e in FixedEvents()) device.SendEvent(e);
    }

    public static IEnumerable<(int Channel, int Param, int Value)> Fixed() => store.Fixed().Select(x => (x.G, x.P, x.Value));

    public static IEnumerable<MidiEvent> FixedEvents() => Fixed().Select(x => (MidiEvent)ParamEvent(x.Channel, x.Param, x.Value));

    // Part block of a channel in the GS addresses (40 1x, 40 2x).
    static int BlockOf(int channel) => channel == 9 ? 0 : channel < 9 ? channel + 1 : channel;
    static int ChannelOf(int block) => block == 0 ? 9 : block <= 9 ? block - 1 : block;

    public static NormalSysExEvent ParamEvent(int c, int p, int value)
    {
        var d = Params[p];
        var data = d.TwoNibbles ? new byte[] { (byte)(value >> 4 & 0x0F), (byte)(value & 0x0F) }
                 : p == FineTune ? new byte[] { (byte)value, 0 }
                 : new[] { (byte)value };
        var bytes = new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, (byte)(d.Block | BlockOf(c)), (byte)d.Address }
            .Concat(data).Concat(new byte[] { 0, 0xF7 }).ToArray();
        MidiTools.FixRolandChecksum(bytes);
        return new NormalSysExEvent(bytes);
    }

    // ---------- Reading the file ----------

    // Values written by a part DT1 (40 1x / 40 2x): (position, parameter, shift) for each byte of a parameter (shift: place
    // of a nibble of the offset fine in the value; the fine tune LSB is not listed); its channel in channel.
    static List<(int Position, int Param, int Shift)> Positions(byte[] d, out int channel)
    {
        var list = new List<(int, int, int)>();
        channel = -1;
        if (d.Length < 10 || d[0] != 0x41 || d[2] != 0x42 || d[3] != 0x12 || d[4] != 0x40 || (d[5] & 0xF0) is not (0x10 or 0x20)) return list;
        channel = ChannelOf(d[5] & 0x0F);
        for (int pos = 7; pos < d.Length - 2; pos++)
        {
            int address = d[6] + pos - 7;
            for (int p = 0; p < Count; p++)
            {
                var param = Params[p];
                if (param.Block != (d[5] & 0xF0)) continue;
                if (address == param.Address) list.Add((pos, p, param.TwoNibbles ? 4 : 0));
                else if (param.TwoNibbles && address == param.Address + 1) list.Add((pos, p, 0));
            }
        }
        return list;
    }

    static int ByteOf(int p, int value, int shift) => Params[p].TwoNibbles ? value >> shift & 0x0F : value;

    // Copy of a part DT1 with its fixed values (checksum recomputed; null if it writes none, or the same values).
    public static byte[] WithFixedValues(byte[] d)
    {
        byte[] data = null;
        foreach (var (pos, p, shift) in Positions(d, out int c))
        {
            if (!IsFixed(c, p) || d[pos] == ByteOf(p, FixedValue(c, p), shift)) continue;
            data ??= (byte[])d.Clone();
            data[pos] = (byte)ByteOf(p, FixedValue(c, p), shift);
        }
        if (data != null) MidiTools.FixRolandChecksum(data);
        return data;
    }

    // Part DT1 played from the file: stores its values; returns the message with the fixed values (null = unchanged).
    public static byte[] Read(byte[] d)
    {
        foreach (var (pos, p, shift) in Positions(d, out int c))
            store.SetFile(c, 0, p, Params[p].TwoNibbles ? Math.Max(FileValue(c, p), 0) & ~(0x0F << shift) | (d[pos] & 0x0F) << shift : d[pos]);
        return WithFixedValues(d);
    }

    // Parameter written by a data entry: RPN 00 00 (bend range) or 00 01 (fine tuning); -1 if none.
    public static int ParamOf(DataEntry entry) =>
        !entry.Rpn || entry.Fine || entry.Msb != 0 ? -1 : entry.Lsb == 0 ? BendRange : entry.Lsb == 1 ? FineTune : -1;

    static int FromDataEntry(int p, int value) => p == BendRange ? 0x40 + Math.Min(value, 24) : value;

    // Data entry with the fixed value (null = unchanged).
    public static MidiEvent FixDataEntry(ControlChangeEvent cc, DataEntry entry)
    {
        int p = ParamOf(entry), c = cc.Channel;
        if (p < 0 || !IsFixed(c, p)) return null;
        int value = p == BendRange ? FixedValue(c, p) - 0x40 : FixedValue(c, p);
        return new ControlChangeEvent((SevenBitNumber)6, (SevenBitNumber)value) { Channel = cc.Channel };
    }

    // Data entry played from the file: stored, and replaced by the fixed value (null = unchanged).
    public static MidiEvent ReadDataEntry(ControlChangeEvent cc, DataEntry entry)
    {
        int p = ParamOf(entry);
        if (p < 0) return null;
        store.SetFile(cc.Channel, 0, p, FromDataEntry(p, cc.ControlValue));
        return FixDataEntry(cc, entry);
    }

    // CC126 (mono) / CC127 (poly) with the fixed mode (null = unchanged).
    public static MidiEvent FixMonoPoly(ControlChangeEvent cc)
    {
        int c = cc.Channel, mode = cc.ControlNumber == 127 ? 1 : 0;
        if (!IsFixed(c, MonoPoly) || FixedValue(c, MonoPoly) == mode) return null;
        return new ControlChangeEvent((SevenBitNumber)(FixedValue(c, MonoPoly) == 1 ? 127 : 126), (SevenBitNumber)0) { Channel = cc.Channel };
    }

    public static MidiEvent ReadMonoPoly(ControlChangeEvent cc)
    {
        store.SetFile(cc.Channel, 0, MonoPoly, cc.ControlNumber == 127 ? 1 : 0);
        return FixMonoPoly(cc);
    }
}
