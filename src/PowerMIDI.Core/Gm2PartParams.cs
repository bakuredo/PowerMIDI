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
        if (device != null) foreach (var e in ParamEvents(c, p, value)) device.SendEvent(e);
    }

    // Back to the file for one parameter, a channel or everything (-1 = all): the file's values are sent again.
    public static void Clear(IOutputDevice device, int channel = -1, int param = -1)
    {
        foreach (var (c, _, p) in store.Unfix(channel, -1, param))
            if (device != null) foreach (var e in ParamEvents(c, p, FileValue(c, p))) device.SendEvent(e);
    }

    public static void Apply(IOutputDevice device)
    {
        foreach (var e in FixedEvents()) device.SendEvent(e);
    }

    public static IEnumerable<(int Channel, int Param, int Value)> Fixed() => store.Fixed().Select(x => (x.G, x.P, x.Value));

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
        if (p < 0 || !IsFixed(c, p)) return null;
        int v = FixedValue(c, p);
        return Cc(c, cc.ControlNumber, !HasLsb(p) ? v : entry.Fine ? v & 0x7F : v >> 7);
    }

    // Data entry played from the file: stored, and replaced by the fixed value (null = unchanged). For the modulation
    // depth range, the fixed LSB is returned in again after a fixed MSB (the file may send no LSB).
    public static MidiEvent ReadDataEntry(ControlChangeEvent cc, DataEntry entry, out MidiEvent again)
    {
        again = null;
        int p = ParamOf(entry), c = cc.Channel;
        if (p < 0) return null;
        store.SetFile(c, 0, p, Entered(p, FileValue(c, p), entry, cc.ControlValue));
        if (IsFixed(c, p) && HasLsb(p) && !entry.Fine) again = Cc(c, 38, FixedValue(c, p) & 0x7F);
        return FixDataEntry(cc, entry);
    }
}
