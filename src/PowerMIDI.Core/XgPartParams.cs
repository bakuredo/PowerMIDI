using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// XG part parameters of the Effects tab's Part page (those the Mixer does not show), for parts 1-16 (part pp taken as
// channel pp). Written by Multi Part SysEx F0 43 1n 4C 08 pp aa vv F7 (the high pass filter: 0A pp 20), and some also by:
// - NRPN on the channel (CC99 / CC98 / CC6): EQ 01 30 / 31 / 34 / 35, high pass filter 01 24;
// - RPN 00 00 (pitch bend range, 0..24 semitones = Bend Pitch Control 40..58);
// - CC126 / CC127 (mono / poly).
// Reset by an XG System On or a GM On. Values fixed in the interface replace the file's.
public static class XgPartParams
{
    public sealed record Param(string Group, string Name, int Block, int Address, int Default, int Min, int Max,
                               int Nrpn = -1, bool Mu100 = false, bool TwoNibbles = false);

    // Detune: 2 bytes of 4 bits (09: bits 7-4, 0A: bits 3-0), 80h = 0 Hz, ±12.8 Hz in 0.1 Hz steps.
    public static readonly Param[] Params =
    {
        new("EQ", "Bass Gain", 0x08, 0x72, 64, 52, 76, Nrpn: 0x30, Mu100: true),
        new("EQ", "Bass Frequency", 0x08, 0x76, 0x0C, 0x04, 0x28, Nrpn: 0x34, Mu100: true),
        new("EQ", "Treble Gain", 0x08, 0x73, 64, 52, 76, Nrpn: 0x31, Mu100: true),
        new("EQ", "Treble Frequency", 0x08, 0x77, 0x36, 0x1C, 0x3A, Nrpn: 0x35, Mu100: true),
        new("Tuning", "Note Shift", 0x08, 0x08, 64, 0x28, 0x58),
        new("Tuning", "Detune", 0x08, 0x09, 0x80, 0, 255, TwoNibbles: true),
        new("Tuning", "Bend Range", 0x08, 0x23, 0x42, 0x28, 0x58),
        new("Play", "Mono / Poly", 0x08, 0x05, 1, 0, 1),
        new("Play", "Velocity Sense Depth", 0x08, 0x0C, 64, 0, 127),
        new("Play", "Velocity Sense Offset", 0x08, 0x0D, 64, 0, 127),
        new("Play", "Dry Level", 0x08, 0x11, 127, 0, 127),
        new("Filter", "High Pass Cutoff", 0x0A, 0x20, 64, 0, 127, Nrpn: 0x24, Mu100: true),
        new("Pitch EG", "Initial Level", 0x08, 0x69, 64, 0, 127),
        new("Pitch EG", "Attack Time", 0x08, 0x6A, 64, 0, 127),
        new("Pitch EG", "Release Level", 0x08, 0x6B, 64, 0, 127),
        new("Pitch EG", "Release Time", 0x08, 0x6C, 64, 0, 127),
    };
    public static int Count => Params.Length;
    public const int BassGain = 0, TrebleGain = 2, NoteShift = 4, Detune = 5, BendRange = 6, MonoPoly = 7, HighPass = 11;
    public static bool IsEq(int p) => Params[p].Group == "EQ";

    static readonly int[,] file = new int[16, Params.Length];
    static readonly bool[,] isFixed = new bool[16, Params.Length];
    static readonly int[,] fixedValue = new int[16, Params.Length];

    static XgPartParams() { ResetFile(); }

    public static void ResetFile()
    {
        for (int c = 0; c < 16; c++)
            for (int p = 0; p < Count; p++) file[c, p] = Params[p].Default;
    }

    public static int FileValue(int c, int p) => file[c, p];
    public static bool IsFixed(int c, int p) => isFixed[c, p];
    public static int FixedValue(int c, int p) => fixedValue[c, p];
    public static int Value(int c, int p) => isFixed[c, p] ? fixedValue[c, p] : file[c, p];
    public static bool IsChannelFixed(int c) => Enumerable.Range(0, Count).Any(p => isFixed[c, p]);
    public static bool HasFixed => Enumerable.Range(0, 16).Any(IsChannelFixed);

    public static void Set(IOutputDevice device, int c, int p, int value)
    {
        isFixed[c, p] = true;
        fixedValue[c, p] = value;
        device?.SendEvent(ParamEvent(c, p, value));
    }

    // Back to the file for one parameter, a channel or everything (-1 = all): the file's values are sent again.
    public static void Clear(IOutputDevice device, int channel = -1, int param = -1)
    {
        for (int c = 0; c < 16; c++)
            for (int p = 0; p < Count; p++)
            {
                if (!isFixed[c, p] || (channel >= 0 && c != channel) || (param >= 0 && p != param)) continue;
                isFixed[c, p] = false;
                device?.SendEvent(ParamEvent(c, p, file[c, p]));
            }
    }

    // Sends the fixed values (start of track, seek, port change, after a reset).
    public static void Apply(IOutputDevice device)
    {
        foreach (var e in FixedEvents()) device.SendEvent(e);
    }

    public static IEnumerable<(int Channel, int Param, int Value)> Fixed()
    {
        for (int c = 0; c < 16; c++)
            for (int p = 0; p < Count; p++)
                if (isFixed[c, p]) yield return (c, p, fixedValue[c, p]);
    }

    public static IEnumerable<MidiEvent> FixedEvents() => Fixed().Select(x => (MidiEvent)ParamEvent(x.Channel, x.Param, x.Value));

    public static NormalSysExEvent ParamEvent(int c, int p, int value)
    {
        var d = Params[p];
        var data = d.TwoNibbles ? new byte[] { (byte)(value >> 4 & 0x0F), (byte)(value & 0x0F) } : new[] { (byte)value };
        return new(new byte[] { 0x43, 0x10, 0x4C, (byte)d.Block, (byte)c, (byte)d.Address }.Concat(data).Append((byte)0xF7).ToArray());
    }

    // ---------- Reading the file ----------

    // Values written by a part SysEx (blocks 08 and 0A): (position, parameter, shift) for each byte of a parameter
    // (shift: place of a detune nibble in the value); its channel in channel.
    static List<(int Position, int Param, int Shift)> Positions(byte[] d, out int channel)
    {
        var list = new List<(int, int, int)>();
        channel = d.Length >= 8 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C && d[3] is 0x08 or 0x0A && d[4] < 16 ? d[4] : -1;
        if (channel < 0) return list;
        for (int pos = 6; pos < d.Length - 1; pos++)
        {
            int address = d[5] + pos - 6;
            for (int p = 0; p < Count; p++)
            {
                var param = Params[p];
                if (param.Block != d[3]) continue;
                if (address == param.Address) list.Add((pos, p, param.TwoNibbles ? 4 : 0));
                else if (param.TwoNibbles && address == param.Address + 1) list.Add((pos, p, 0));
            }
        }
        return list;
    }

    static int ByteOf(int p, int value, int shift) => Params[p].TwoNibbles ? value >> shift & 0x0F : value;

    // Copy of a part SysEx with its fixed values (null if it writes none, or the same values).
    public static byte[] WithFixedValues(byte[] d)
    {
        byte[] data = null;
        foreach (var (pos, p, shift) in Positions(d, out int c))
        {
            if (!isFixed[c, p] || d[pos] == ByteOf(p, fixedValue[c, p], shift)) continue;
            data ??= (byte[])d.Clone();
            data[pos] = (byte)ByteOf(p, fixedValue[c, p], shift);
        }
        return data;
    }

    // Part SysEx played from the file: stores its values; returns the message with the fixed values (null = unchanged).
    public static byte[] Read(byte[] d)
    {
        foreach (var (pos, p, shift) in Positions(d, out int c))
            file[c, p] = Params[p].TwoNibbles ? file[c, p] & ~(0x0F << shift) | (d[pos] & 0x0F) << shift : d[pos];
        return WithFixedValues(d);
    }

    // Parameter written by a data entry: an NRPN 01 xx, or the RPN 00 00 (bend range); -1 if none.
    public static int ParamOf(DataEntry entry) =>
        entry.Fine ? -1 : entry.Rpn ? (entry.Msb == 0 && entry.Lsb == 0 ? BendRange : -1)
        : entry.Msb == 0x01 ? Array.FindIndex(Params, x => x.Nrpn == entry.Lsb) : -1;

    // Value of the parameter written by a data entry: the bend range in semitones (0..24) becomes 40h + range.
    static int FromDataEntry(int p, int value) => p == BendRange ? 0x40 + Math.Min(value, 24) : value;

    // Data entry with the fixed value (null = unchanged). A negative bend range has no RPN value: the data entry
    // writes 0 and the SysEx is returned in again (to send after it).
    public static MidiEvent FixDataEntry(ControlChangeEvent cc, DataEntry entry, out MidiEvent again)
    {
        again = null;
        int p = ParamOf(entry), c = cc.Channel;
        if (p < 0 || !isFixed[c, p]) return null;
        int value = fixedValue[c, p];
        if (p == BendRange)
        {
            if (value < 0x40) again = ParamEvent(c, p, value);
            value = Math.Max(0, value - 0x40);
        }
        return new ControlChangeEvent((SevenBitNumber)6, (SevenBitNumber)value) { Channel = cc.Channel };
    }

    // Data entry played from the file: stored, and replaced by the fixed value (null = unchanged).
    public static MidiEvent ReadDataEntry(ControlChangeEvent cc, DataEntry entry, out MidiEvent again)
    {
        again = null;
        int p = ParamOf(entry);
        if (p < 0) return null;
        file[cc.Channel, p] = FromDataEntry(p, cc.ControlValue);
        return FixDataEntry(cc, entry, out again);
    }

    // CC126 (mono) / CC127 (poly) with the fixed mode: the message to play instead (null = unchanged).
    public static MidiEvent FixMonoPoly(ControlChangeEvent cc)
    {
        int c = cc.Channel, mode = cc.ControlNumber == 127 ? 1 : 0;
        if (!isFixed[c, MonoPoly] || fixedValue[c, MonoPoly] == mode) return null;
        return new ControlChangeEvent((SevenBitNumber)(fixedValue[c, MonoPoly] == 1 ? 127 : 126), (SevenBitNumber)0) { Channel = cc.Channel };
    }

    // CC126 / CC127 played from the file: stored, and replaced by the fixed mode (null = unchanged).
    public static MidiEvent ReadMonoPoly(ControlChangeEvent cc)
    {
        file[cc.Channel, MonoPoly] = cc.ControlNumber == 127 ? 1 : 0;
        return FixMonoPoly(cc);
    }
}
