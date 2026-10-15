using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// GS drum maps (SC-88 and later; SC-8850 manual p. 240): settings of each note of the drum set played by drum part 1
// (map 1) and drum part 2 (map 2), the drum parts of the channels being chosen by Use for Rhythm Part (40 1x 15).
// Written by DT1 41 mp rr vv (m = map 0 / 1, p = parameter, rr = note) or by NRPN on the channel of such a part
// (CC99 = 18 / 1A / 1C / 1D / 1E / 1F, CC98 = note, CC6 = value). The module initializes a map when its drum set
// changes (Program Change on a channel of the drum part) and on a GS Reset / GM On; the values fixed in the
// interface replace the file's and are sent again after these.
public static class GsDrumMap
{
    public const int Maps = 2, FirstNote = 0, LastNote = 127;

    // Parameters shown (index p). Pitch Coarse is kept as an offset (64 = the note's own pitch): the SysEx writes the
    // note played (PLAY NOTE NUMBER), the NRPN a relative change.
    public const int Count = 9;
    public static readonly string[] Names = { "Pitch Coarse", "Level", "Pan", "Reverb Send", "Chorus Send", "Delay Send",
                                              "Assign Group", "Rx Note Off", "Rx Note On" };
    public static readonly int[] Addresses = { 1, 2, 4, 5, 6, 9, 3, 7, 8 };
    static readonly int[] NrpnMsb = { 0x18, 0x1A, 0x1C, 0x1D, 0x1E, 0x1F, -1, -1, -1 };
    // After a reset: pitch ±0, notes received; -1 = depends on the note.
    public static readonly int[] Defaults = { 64, -1, -1, -1, -1, -1, -1, -1, 1 };
    public const int PitchCoarse = 0, Pan = 2, AssignGroup = 6, RxNoteOff = 7, RxNoteOn = 8;
    public static bool IsSwitch(int p) => p >= RxNoteOff;

    static readonly int[,,] file = new int[Maps, 128, Count];
    static readonly bool[,,] isFixed = new bool[Maps, 128, Count];
    static readonly int[,,] fixedValue = new int[Maps, 128, Count];
    static int fixedCount;

    static GsDrumMap() { ResetFile(); }

    // GS Reset, GM On: both maps back to the drum sets' values.
    public static void ResetFile()
    {
        for (int m = 0; m < Maps; m++) ResetMap(m);
    }

    static void ResetMap(int m)
    {
        for (int n = 0; n < 128; n++)
            for (int p = 0; p < Count; p++) file[m, n, p] = Defaults[p];
    }

    // Map (0 / 1) of the drum part of a channel, or -1 (normal part).
    public static int MapOf(int channel) => ChannelMixer.Rhythm(channel) is 1 or 2 ? ChannelMixer.Rhythm(channel) - 1 : -1;

    public static int FileValue(int m, int n, int p) => file[m, n, p];
    public static bool IsFixed(int m, int n, int p) => isFixed[m, n, p];
    public static int FixedValue(int m, int n, int p) => fixedValue[m, n, p];
    public static int Value(int m, int n, int p) => isFixed[m, n, p] ? fixedValue[m, n, p] : file[m, n, p];
    public static bool HasFixed => fixedCount > 0;
    public static bool IsNoteFixed(int m, int n) => Enumerable.Range(0, Count).Any(p => isFixed[m, n, p]);
    public static bool IsMapFixed(int m) => Enumerable.Range(0, 128).Any(n => IsNoteFixed(m, n));

    public static IEnumerable<(int Map, int Note, int Param, int Value)> Fixed()
    {
        for (int m = 0; m < Maps; m++)
            for (int n = 0; n < 128; n++)
                for (int p = 0; p < Count; p++)
                    if (isFixed[m, n, p]) yield return (m, n, p, fixedValue[m, n, p]);
    }

    public static void Set(IOutputDevice device, int m, int n, int p, int value)
    {
        if (!isFixed[m, n, p]) fixedCount++;
        isFixed[m, n, p] = true;
        fixedValue[m, n, p] = value;
        device?.SendEvent(ParamEvent(m, n, p, value));
    }

    // Back to the file for the fixed values of a map, of a note or of one parameter (-1 = all). Returns the maps
    // whose drum set must be selected again (a value that depends on the note, never set by the file, cannot be
    // sent): see ChannelMixer.ResetGsDrumMap, which then writes the file's values and the remaining fixed ones.
    public static List<int> Clear(IOutputDevice device, int map = -1, int note = -1, int param = -1)
    {
        var reset = new List<int>();
        for (int m = 0; m < Maps; m++)
        {
            if (map >= 0 && m != map) continue;
            var cleared = new List<(int Note, int Param)>();
            for (int n = 0; n < 128; n++)
                for (int p = 0; p < Count; p++)
                    if (isFixed[m, n, p] && (note < 0 || n == note) && (param < 0 || p == param))
                    {
                        isFixed[m, n, p] = false;
                        fixedCount--;
                        cleared.Add((n, p));
                    }
            if (device == null || cleared.Count == 0) continue;
            if (cleared.All(x => file[m, x.Note, x.Param] >= 0))
                foreach (var (n, p) in cleared) device.SendEvent(ParamEvent(m, n, p, file[m, n, p]));
            else reset.Add(m);
        }
        return reset;
    }

    // After the drum set of a map was selected again: the values written by the file and the fixed ones.
    public static IEnumerable<MidiEvent> KnownEvents(int m)
    {
        for (int n = 0; n < 128; n++)
            for (int p = 0; p < Count; p++)
                if (isFixed[m, n, p] || file[m, n, p] != Defaults[p]) yield return ParamEvent(m, n, p, Value(m, n, p));
    }

    public static void Apply(IOutputDevice device)
    {
        foreach (var e in FixedEvents()) device.SendEvent(e);
    }

    public static IEnumerable<MidiEvent> FixedEvents() => Enumerable.Range(0, Maps).SelectMany(FixedEvents);

    public static IEnumerable<MidiEvent> FixedEvents(int m)
    {
        for (int n = 0; n < 128; n++)
            for (int p = 0; p < Count; p++)
                if (isFixed[m, n, p]) yield return ParamEvent(m, n, p, fixedValue[m, n, p]);
    }

    // ---------- Messages ----------

    // Byte written by the SysEx for a value: the note played for the pitch (offset from the note itself).
    static int ToByte(int n, int p, int value) => p == PitchCoarse ? Math.Clamp(n + value - 64, 0, 127) : value;
    static int FromByte(int n, int p, int b) => p == PitchCoarse ? Math.Clamp(b - n + 64, 0, 127) : b;

    public static NormalSysExEvent ParamEvent(int m, int n, int p, int value)
    {
        var d = new byte[] { 0x41, 0x10, 0x42, 0x12, 0x41, (byte)(m << 4 | Addresses[p]), (byte)n, (byte)ToByte(n, p, value), 0, 0xF7 };
        MidiTools.FixRolandChecksum(d);
        return new NormalSysExEvent(d);
    }

    // Values written by a drum map SysEx (one note per data byte, from rr on), as (position, note, parameter); its map
    // in map (-1: not a drum map SysEx, or a parameter not shown: the map name).
    static List<(int Position, int Note, int Param)> Positions(byte[] d, out int map)
    {
        var list = new List<(int, int, int)>();
        map = -1;
        if (d.Length < 10 || d[0] != 0x41 || d[2] != 0x42 || d[3] != 0x12 || d[4] != 0x41 || d[5] >> 4 >= Maps) return list;
        int p = Array.IndexOf(Addresses, d[5] & 0x0F);
        if (p < 0) return list;
        map = d[5] >> 4;
        for (int pos = 7; pos < d.Length - 2 && d[6] + pos - 7 < 128; pos++) list.Add((pos, d[6] + pos - 7, p));
        return list;
    }

    // Copy of a drum map SysEx with its fixed values (checksum recomputed; null if it writes none, or the same values).
    public static byte[] WithFixedValues(byte[] d)
    {
        byte[] data = null;
        foreach (var (pos, n, p) in Positions(d, out int m))
        {
            if (!isFixed[m, n, p] || d[pos] == ToByte(n, p, fixedValue[m, n, p])) continue;
            data ??= (byte[])d.Clone();
            data[pos] = (byte)ToByte(n, p, fixedValue[m, n, p]);
        }
        if (data != null) MidiTools.FixRolandChecksum(data);
        return data;
    }

    // Drum map SysEx played from the file: stores its values; returns the message with the fixed values (null = unchanged).
    public static MidiEvent Read(byte[] d)
    {
        var positions = Positions(d, out int m);
        if (positions.Count == 0) return null;
        foreach (var (pos, n, p) in positions) file[m, n, p] = FromByte(n, p, d[pos]);
        var data = WithFixedValues(d);
        return data != null ? new NormalSysExEvent(data) : null;
    }

    // Program Change on a channel: the drum set of its drum part changes, and the module initializes the map. Returns
    // that map if it has fixed values to send again, otherwise -1.
    public static int ProgramChanged(int channel)
    {
        int m = MapOf(channel);
        if (m < 0) return -1;
        ResetMap(m);
        return IsMapFixed(m) ? m : -1;
    }

    // Drum map parameter written by an NRPN data entry (CC99 = parameter, CC98 = note) on a channel of a drum part:
    // its map, note and parameter, or null.
    public static (int Map, int Note, int Param)? NrpnParam(int channel, DataEntry entry, Func<int, int> mapOf)
    {
        int m = mapOf(channel), p = Array.IndexOf(NrpnMsb, entry.Msb);
        return m < 0 || entry.Rpn || p < 0 ? null : (m, entry.Lsb, p);
    }

    // Data entry with the fixed value (null = unchanged).
    public static MidiEvent FixNrpn(int m, int n, int p, ControlChangeEvent cc) =>
        isFixed[m, n, p] ? new ControlChangeEvent((SevenBitNumber)6, (SevenBitNumber)fixedValue[m, n, p]) { Channel = cc.Channel } : null;

    // NRPN data entry played from the file: a drum map value is stored, and replaced by the fixed one (null = unchanged,
    // also when it is not a drum map NRPN).
    public static MidiEvent ReadNrpn(ControlChangeEvent cc, DataEntry entry)
    {
        if (NrpnParam(cc.Channel, entry, MapOf) is not var (m, n, p)) return null;
        file[m, n, p] = cc.ControlValue;
        return FixNrpn(m, n, p, cc);
    }
}
