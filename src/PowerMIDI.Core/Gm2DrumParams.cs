using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// GM2 Key-Based Instrument Control (SC-8850 manual p. 232): settings of each note of the drum kit of a channel,
// F0 7F 7F 0A 01 0n kk nn vv … F7 (n = channel, kk = note, then controller / value pairs): 07 level (relative,
// 0..200 %, 40h = 100 %), 0A pan, 5B reverb send, 5D chorus send (absolute). Drum instruments only. Reset by a
// GM2 System On (and any other reset). Values fixed in the interface replace the file's; they are also sent again
// after a Program Change on the channel (a new kit).
public static class Gm2DrumParams
{
    public const int Count = 4;
    public static readonly string[] Names = { "Level", "Pan", "Reverb Send", "Chorus Send" };
    public static readonly int[] Controllers = { 0x07, 0x0A, 0x5B, 0x5D };
    // After a reset: level 100 %; -1 = the kit's (depends on the note).
    public static readonly int[] Defaults = { 64, -1, -1, -1 };
    public const int Level = 0, Pan = 1;

    static readonly int[,,] file = new int[16, 128, Count];
    static readonly bool[,,] isFixed = new bool[16, 128, Count];
    static readonly int[,,] fixedValue = new int[16, 128, Count];
    static int fixedCount;

    static Gm2DrumParams() { ResetFile(); }

    public static void ResetFile()
    {
        for (int c = 0; c < 16; c++)
            for (int n = 0; n < 128; n++)
                for (int p = 0; p < Count; p++) file[c, n, p] = Defaults[p];
    }

    public static int FileValue(int c, int n, int p) => file[c, n, p];
    public static bool IsFixed(int c, int n, int p) => isFixed[c, n, p];
    public static int FixedValue(int c, int n, int p) => fixedValue[c, n, p];
    public static int Value(int c, int n, int p) => isFixed[c, n, p] ? fixedValue[c, n, p] : file[c, n, p];
    public static bool HasFixed => fixedCount > 0;
    public static bool IsNoteFixed(int c, int n) => Enumerable.Range(0, Count).Any(p => isFixed[c, n, p]);
    public static bool IsChannelFixed(int c) => Enumerable.Range(0, 128).Any(n => IsNoteFixed(c, n));

    public static IEnumerable<(int Channel, int Note, int Param, int Value)> Fixed()
    {
        for (int c = 0; c < 16; c++)
            for (int n = 0; n < 128; n++)
                for (int p = 0; p < Count; p++)
                    if (isFixed[c, n, p]) yield return (c, n, p, fixedValue[c, n, p]);
    }

    public static void Set(IOutputDevice device, int c, int n, int p, int value)
    {
        if (!isFixed[c, n, p]) fixedCount++;
        isFixed[c, n, p] = true;
        fixedValue[c, n, p] = value;
        device?.SendEvent(ParamEvent(c, n, p, value));
    }

    // Back to the file for the fixed values of a channel, of a note or of one parameter (-1 = all): the file's values
    // are sent again. A value of the kit (never set by the file) cannot be: the module keeps the fixed one until the
    // next reset.
    public static void Clear(IOutputDevice device, int channel = -1, int note = -1, int param = -1)
    {
        for (int c = 0; c < 16; c++)
            for (int n = 0; n < 128; n++)
                for (int p = 0; p < Count; p++)
                {
                    if (!isFixed[c, n, p] || (channel >= 0 && c != channel) || (note >= 0 && n != note) || (param >= 0 && p != param)) continue;
                    isFixed[c, n, p] = false;
                    fixedCount--;
                    if (file[c, n, p] >= 0) device?.SendEvent(ParamEvent(c, n, p, file[c, n, p]));
                }
    }

    public static void Apply(IOutputDevice device)
    {
        foreach (var e in FixedEvents()) device.SendEvent(e);
    }

    public static IEnumerable<MidiEvent> FixedEvents() => Enumerable.Range(0, 16).SelectMany(FixedEvents);

    public static IEnumerable<MidiEvent> FixedEvents(int c)
    {
        for (int n = 0; n < 128; n++)
            for (int p = 0; p < Count; p++)
                if (isFixed[c, n, p]) yield return ParamEvent(c, n, p, fixedValue[c, n, p]);
    }

    public static NormalSysExEvent ParamEvent(int c, int n, int p, int value) =>
        new(new byte[] { 0x7F, 0x7F, 0x0A, 0x01, (byte)c, (byte)n, (byte)Controllers[p], (byte)value, 0xF7 });

    // ---------- Reading the file ----------

    // Values written by a Key-Based Instrument Control message: (position of the value, parameter); its channel and
    // note in channel / note (-1: not such a message).
    static List<(int Position, int Param)> Positions(byte[] d, out int channel, out int note)
    {
        var list = new List<(int, int)>();
        channel = note = -1;
        if (d.Length < 9 || d[0] != 0x7F || d[2] != 0x0A || d[3] != 0x01 || d[4] > 0x0F || d[5] > 0x7F) return list;
        channel = d[4];
        note = d[5];
        for (int pos = 6; pos + 1 < d.Length - 1; pos += 2)
        {
            int p = Array.IndexOf(Controllers, (int)d[pos]);
            if (p >= 0) list.Add((pos + 1, p));
        }
        return list;
    }

    // Copy of such a message with its fixed values (null if it writes none, or the same values).
    public static byte[] WithFixedValues(byte[] d)
    {
        byte[] data = null;
        foreach (var (pos, p) in Positions(d, out int c, out int n))
        {
            if (!isFixed[c, n, p] || d[pos] == fixedValue[c, n, p]) continue;
            data ??= (byte[])d.Clone();
            data[pos] = (byte)fixedValue[c, n, p];
        }
        return data;
    }

    // Message played from the file: stores its values; returns it with the fixed values (null = unchanged).
    public static MidiEvent Read(byte[] d)
    {
        var positions = Positions(d, out int c, out int n);
        if (positions.Count == 0) return null;
        foreach (var (pos, p) in positions) file[c, n, p] = d[pos];
        var data = WithFixedValues(d);
        return data != null ? new NormalSysExEvent(data) : null;
    }
}
