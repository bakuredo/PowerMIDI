using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// GM2 part parameters of the Effects tab's Part page: RPN of each channel (SC-8850 manual p. 228), written by the file
// with CC101 / CC100 (RPN number), CC6 (data entry MSB) and CC38 (LSB):
// - 00 00 pitch bend range (semitones, 2 after a reset);
// - 00 01 fine tuning (MSB only here: 40h = 0, ±100 cents);
// - 00 02 coarse tuning (28h..58h = -24..+24 semitones);
// - 00 05 modulation depth range: semitones (MSB) and 1/128 semitone (LSB), kept as MSB × 128 + LSB (default 50 cents).
// Reset by a GM2 System On (and any other reset). Values fixed in the interface replace the file's.
public static class Gm2PartParams
{
    public sealed record Param(string Group, string Name, int Rpn, int Default, int Min, int Max);

    public static readonly Param[] Params =
    {
        new("Tuning", "Bend Range", 0, 2, 0, 24),
        new("Tuning", "Fine Tune", 1, 64, 0, 127),
        new("Tuning", "Coarse Tune", 2, 64, 0x28, 0x58),
        new("Modulation", "Mod Depth Range", 5, 64, 0, 4 * 128),
    };
    public static int Count => Params.Length;
    public const int BendRange = 0, FineTune = 1, CoarseTune = 2, ModDepthRange = 3;
    static bool HasLsb(int p) => p == ModDepthRange;

    static readonly int[,] file = new int[16, Params.Length];
    static readonly bool[,] isFixed = new bool[16, Params.Length];
    static readonly int[,] fixedValue = new int[16, Params.Length];

    static Gm2PartParams() { ResetFile(); }

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
        if (device != null) foreach (var e in ParamEvents(c, p, value)) device.SendEvent(e);
    }

    // Back to the file for one parameter, a channel or everything (-1 = all): the file's values are sent again.
    public static void Clear(IOutputDevice device, int channel = -1, int param = -1)
    {
        for (int c = 0; c < 16; c++)
            for (int p = 0; p < Count; p++)
            {
                if (!isFixed[c, p] || (channel >= 0 && c != channel) || (param >= 0 && p != param)) continue;
                isFixed[c, p] = false;
                if (device != null) foreach (var e in ParamEvents(c, p, file[c, p])) device.SendEvent(e);
            }
    }

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

    public static IEnumerable<MidiEvent> FixedEvents() => Fixed().SelectMany(x => ParamEvents(x.Channel, x.Param, x.Value));

    static ControlChangeEvent Cc(int channel, int number, int value) =>
        new((SevenBitNumber)number, (SevenBitNumber)value) { Channel = (FourBitNumber)channel };

    // RPN number, data entry (MSB, and LSB for the modulation depth range), then RPN null.
    public static IEnumerable<MidiEvent> ParamEvents(int c, int p, int value)
    {
        yield return Cc(c, 101, 0);
        yield return Cc(c, 100, Params[p].Rpn);
        yield return Cc(c, 6, HasLsb(p) ? value >> 7 : value);
        if (HasLsb(p)) yield return Cc(c, 38, value & 0x7F);
        yield return Cc(c, 101, 0x7F);
        yield return Cc(c, 100, 0x7F);
    }

    // ---------- Reading the file ----------

    // Parameter written by a data entry (an RPN 00 0x), or -1; the LSB (CC38) only counts for the modulation depth range.
    public static int ParamOf(DataEntry entry)
    {
        if (!entry.Rpn || entry.Msb != 0) return -1;
        int p = Array.FindIndex(Params, x => x.Rpn == entry.Lsb);
        return p >= 0 && (!entry.Fine || HasLsb(p)) ? p : -1;
    }

    // Value after a data entry: the MSB or the LSB of the modulation depth range replaced.
    static int Entered(int p, int old, DataEntry entry, int value) =>
        !HasLsb(p) ? value : entry.Fine ? (old & ~0x7F) | value : value << 7 | (old & 0x7F);

    // Data entry with the fixed value (null = unchanged).
    public static MidiEvent FixDataEntry(ControlChangeEvent cc, DataEntry entry)
    {
        int p = ParamOf(entry), c = cc.Channel;
        if (p < 0 || !isFixed[c, p]) return null;
        int v = fixedValue[c, p];
        return Cc(c, cc.ControlNumber, !HasLsb(p) ? v : entry.Fine ? v & 0x7F : v >> 7);
    }

    // Data entry played from the file: stored, and replaced by the fixed value (null = unchanged). For the modulation
    // depth range, the fixed LSB is returned in again after a fixed MSB (the file may send no LSB).
    public static MidiEvent ReadDataEntry(ControlChangeEvent cc, DataEntry entry, out MidiEvent again)
    {
        again = null;
        int p = ParamOf(entry), c = cc.Channel;
        if (p < 0) return null;
        file[c, p] = Entered(p, file[c, p], entry, cc.ControlValue);
        if (isFixed[c, p] && HasLsb(p) && !entry.Fine) again = Cc(c, 38, fixedValue[c, p] & 0x7F);
        return FixDataEntry(cc, entry);
    }
}
