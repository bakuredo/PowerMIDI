using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// XG drum setups (MU100 and later: 4, an XG Level 1 module such as the MU50: 2): settings of each note of the drum
// kit played by the parts assigned to them (Multi Part PART MODE 08 pp 07 = 2..5: DRUMS1..4; after a reset, part 10
// uses Drum Setup 1). Written by SysEx F0 43 1n 4C 3s rr pp vv F7 (s = setup 0-3, rr = note 13-91, pp = parameter)
// or by NRPN on the channel of such a part (CC99 = 14..1F, CC98 = note, CC6 = value).
// The module resets a setup on an XG System On, a GM On, a Drum Setup Reset (00 00 7D s) and on a Program Change
// of a part assigned to it. Values fixed in the interface replace the file's and are sent again after these.
public static class XgDrumSetup
{
    public const int Setups = 4, Level1Setups = 2, FirstNote = 13, LastNote = 91;

    // Parameters shown (index p): pitch, filter, envelope, output.
    public const int Count = 12;
    public static readonly string[] Names = { "Pitch Coarse", "Pitch Fine", "Cutoff", "Resonance", "Attack Rate", "Decay 1 Rate",
                                              "Decay 2 Rate", "Level", "Pan", "Reverb Send", "Chorus Send", "Variation Send" };
    public static readonly int[] Addresses = { 0x00, 0x01, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x02, 0x04, 0x05, 0x06, 0x07 };
    // NRPN MSB of each parameter (17: both decay rates).
    static readonly int[] NrpnMsb = { 0x18, 0x19, 0x14, 0x15, 0x16, 0x17, 0x17, 0x1A, 0x1C, 0x1D, 0x1E, 0x1F };
    // After a reset: 64 = ±0 for the offsets, variation send 127; -1 = depends on the note (level, pan, sends).
    public static readonly int[] Defaults = { 64, 64, 64, 64, 64, 64, 64, -1, -1, -1, -1, 127 };
    public const int Pan = 8;
    public static bool IsOffset(int p) => p < 7;   // -64..0..+63

    static readonly int[,,] file = new int[Setups, 128, Count];
    static readonly bool[,,] isFixed = new bool[Setups, 128, Count];
    static readonly int[,,] fixedValue = new int[Setups, 128, Count];
    static int fixedCount;

    // PART MODE of each channel (part = channel): 0 normal, 1 drum, 2..5 drum setup 1..4. The file's, and the one
    // chosen in the interface (-1 = the file's), which replaces it.
    static readonly int[] filePartMode = new int[16];
    public static readonly int[] PartModeOverride = Enumerable.Repeat(-1, 16).ToArray();
    public const int Normal = 0, Drum = 1;
    public static int PartMode(int channel) => PartModeOverride[channel] >= 0 ? PartModeOverride[channel] : filePartMode[channel];
    public static int FilePartMode(int channel) => filePartMode[channel];
    public static bool HasPartModes => PartModeOverride.Any(m => m >= 0);

    // Incremented when the file changes a value (the interface refreshes its page).
    public static int Version;

    static XgDrumSetup() { ResetFile(); }

    // Reset of the module (XG System On, GM On): every setup back to its defaults, part 10 on Drum Setup 1.
    public static void ResetFile()
    {
        for (int s = 0; s < Setups; s++) ResetSetup(s);
        Array.Clear(filePartMode);
        filePartMode[9] = 2;
        ResetControllers();
    }

    static void ResetSetup(int s)
    {
        for (int n = 0; n < 128; n++)
            for (int p = 0; p < Count; p++) file[s, n, p] = Defaults[p];
        Version++;
    }

    // Setup (0-3) used by a channel, or -1.
    public static int SetupOf(int channel) => PartMode(channel) is >= 2 and <= 5 ? PartMode(channel) - 2 : -1;

    public static int FileValue(int s, int n, int p) => file[s, n, p];
    public static bool IsFixed(int s, int n, int p) => isFixed[s, n, p];
    public static int FixedValue(int s, int n, int p) => fixedValue[s, n, p];
    public static int Value(int s, int n, int p) => isFixed[s, n, p] ? fixedValue[s, n, p] : file[s, n, p];
    public static bool HasFixed => fixedCount > 0;
    public static bool IsNoteFixed(int s, int n) => Enumerable.Range(0, Count).Any(p => isFixed[s, n, p]);
    public static bool IsSetupFixed(int s) => Enumerable.Range(0, 128).Any(n => IsNoteFixed(s, n));

    // Fixed values, as (setup, note, parameter, value).
    public static IEnumerable<(int Setup, int Note, int Param, int Value)> Fixed()
    {
        for (int s = 0; s < Setups; s++)
            for (int n = 0; n < 128; n++)
                for (int p = 0; p < Count; p++)
                    if (isFixed[s, n, p]) yield return (s, n, p, fixedValue[s, n, p]);
    }

    public static void Set(IOutputDevice device, int s, int n, int p, int value)
    {
        if (!isFixed[s, n, p]) fixedCount++;
        isFixed[s, n, p] = true;
        fixedValue[s, n, p] = value;
        device?.SendEvent(ParamEvent(s, n, p, value));
    }

    // Back to the file for the fixed values of a setup, of a note or of one parameter (-1 = all): the file's values are
    // sent again. A value that depends on the note (never set by the file) cannot be sent: the setup is then reset,
    // and the file's values and the remaining fixed ones are written again.
    public static void Clear(IOutputDevice device, int setup = -1, int note = -1, int param = -1)
    {
        for (int s = 0; s < Setups; s++)
        {
            if (setup >= 0 && s != setup) continue;
            var cleared = new List<(int Note, int Param)>();
            for (int n = 0; n < 128; n++)
                for (int p = 0; p < Count; p++)
                    if (isFixed[s, n, p] && (note < 0 || n == note) && (param < 0 || p == param))
                    {
                        isFixed[s, n, p] = false;
                        fixedCount--;
                        cleared.Add((n, p));
                    }
            if (device == null || cleared.Count == 0) continue;
            if (cleared.All(x => file[s, x.Note, x.Param] >= 0))
            {
                foreach (var (n, p) in cleared) device.SendEvent(ParamEvent(s, n, p, file[s, n, p]));
                continue;
            }
            device.SendEvent(ResetEvent(s));
            for (int n = 0; n < 128; n++)
                for (int p = 0; p < Count; p++)
                    if (isFixed[s, n, p] || file[s, n, p] != Defaults[p]) device.SendEvent(ParamEvent(s, n, p, Value(s, n, p)));
        }
    }

    // Sends the fixed values (start of track, seek, port change, after a reset).
    public static void Apply(IOutputDevice device)
    {
        foreach (var e in PartModeEvents().Concat(FixedEvents())) device.SendEvent(e);
    }

    public static void Apply(IOutputDevice device, int s)
    {
        foreach (var e in FixedEvents(s)) device.SendEvent(e);
    }

    public static IEnumerable<MidiEvent> FixedEvents() => Enumerable.Range(0, Setups).SelectMany(FixedEvents);

    public static IEnumerable<MidiEvent> FixedEvents(int s)
    {
        for (int n = 0; n < 128; n++)
            for (int p = 0; p < Count; p++)
                if (isFixed[s, n, p]) yield return ParamEvent(s, n, p, fixedValue[s, n, p]);
    }

    // ---------- Messages ----------

    public static NormalSysExEvent ParamEvent(int s, int n, int p, int value) =>
        new(new byte[] { 0x43, 0x10, 0x4C, (byte)(0x30 + s), (byte)n, (byte)Addresses[p], (byte)value, 0xF7 });

    public static NormalSysExEvent ResetEvent(int s) => new(new byte[] { 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7D, (byte)s, 0xF7 });

    // Drum Setup Reset: the setup reset, or -1.
    public static int ResetSetupOf(byte[] d) =>
        d.Length >= 8 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C && d[3] == 0 && d[4] == 0 && d[5] == 0x7D && d[6] < Setups ? d[6] : -1;

    // Drum setup SysEx: the setup it writes, or -1.
    public static int SetupOf(byte[] d) =>
        d.Length >= 8 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C && d[3] is >= 0x30 and < 0x30 + Setups ? d[3] - 0x30 : -1;

    // Positions of the parameters written by a drum setup SysEx (one per data byte), as (position, parameter).
    public static List<(int Position, int Param)> Positions(byte[] d)
    {
        var list = new List<(int, int)>();
        if (SetupOf(d) < 0) return list;
        for (int pos = 6; pos < d.Length - 1; pos++)
        {
            int p = Array.IndexOf(Addresses, d[5] + pos - 6);
            if (p >= 0) list.Add((pos, p));
        }
        return list;
    }

    // Copy of a drum setup SysEx with its fixed values (null if it writes none, or the same values).
    public static byte[] WithFixedValues(byte[] d)
    {
        int s = SetupOf(d);
        if (s < 0) return null;
        byte[] data = null;
        foreach (var (pos, p) in Positions(d))
        {
            if (!isFixed[s, d[4], p] || d[pos] == fixedValue[s, d[4], p]) continue;
            data ??= (byte[])d.Clone();
            data[pos] = (byte)fixedValue[s, d[4], p];
        }
        return data;
    }

    // ---------- Reading the file during playback ----------

    // Drum setup SysEx or Drum Setup Reset played from the file: stores its values. Returns the message to play
    // with the fixed values (null = unchanged); reset: the setup whose fixed values must be sent again after it.
    public static MidiEvent Read(byte[] d, out int reset)
    {
        reset = ResetSetupOf(d);
        if (reset >= 0) { ResetSetup(reset); if (!IsSetupFixed(reset)) reset = -1; return null; }
        int s = SetupOf(d);
        if (s < 0) return null;
        foreach (var (pos, p) in Positions(d)) file[s, d[4], p] = d[pos];
        Version++;
        var data = WithFixedValues(d);
        return data != null ? new NormalSysExEvent(data) : null;
    }

    // Part mode written by a Multi Part SysEx (08 pp 07).
    public static void ReadPartMode(int channel, int mode) => filePartMode[channel] = mode;

    public static NormalSysExEvent PartModeEvent(int channel, int mode) =>
        new(new byte[] { 0x43, 0x10, 0x4C, 0x08, (byte)channel, 0x07, (byte)mode, 0xF7 });

    // Part mode chosen in the interface: sent, then the fixed values of its drum setup.
    public static void SetPartMode(IOutputDevice device, int channel, int mode)
    {
        PartModeOverride[channel] = mode;
        if (device == null) return;
        device.SendEvent(PartModeEvent(channel, mode));
        if (SetupOf(channel) is int s and >= 0) Apply(device, s);
    }

    // Back to the file's part mode (-1: every channel).
    public static void ClearPartMode(IOutputDevice device, int channel = -1)
    {
        for (int c = 0; c < 16; c++)
        {
            if ((channel >= 0 && c != channel) || PartModeOverride[c] < 0) continue;
            PartModeOverride[c] = -1;
            if (device == null) continue;
            device.SendEvent(PartModeEvent(c, filePartMode[c]));
            if (SetupOf(c) is int s and >= 0) Apply(device, s);
        }
    }

    public static IEnumerable<MidiEvent> PartModeEvents() =>
        Enumerable.Range(0, 16).Where(c => PartModeOverride[c] >= 0).Select(c => (MidiEvent)PartModeEvent(c, PartModeOverride[c]));

    // Program Change on a channel: the module resets the setup of its part. Returns that setup if it has fixed
    // values to send again, otherwise -1.
    public static int ProgramChanged(int channel)
    {
        int s = SetupOf(channel);
        if (s < 0) return -1;
        ResetSetup(s);
        return IsSetupFixed(s) ? s : -1;
    }

    // NRPN state of each channel: MSB, LSB (-1 = none, or an RPN is selected).
    public sealed class NrpnState
    {
        readonly int[] msb = new int[16], lsb = new int[16];
        public NrpnState() => Reset();
        public void Reset() { Array.Fill(msb, -1); Array.Fill(lsb, -1); }

        // CC99 / CC98 / CC101 / CC100 change the state; CC6 on a drum setup parameter (setup of the channel given by
        // setupOf): its setup, note and parameters (the decay NRPN writes both decay rates). Otherwise null.
        public (int Setup, int Note, List<int> Params)? Read(ControlChangeEvent cc, Func<int, int> setupOf)
        {
            int channel = cc.Channel, value = cc.ControlValue;
            switch ((int)cc.ControlNumber)
            {
                case 99: msb[channel] = value; return null;
                case 98: lsb[channel] = value; return null;
                case 101: case 100: msb[channel] = lsb[channel] = -1; return null;
                case 6: break;
                default: return null;
            }
            int s = setupOf(channel), n = lsb[channel];
            if (s < 0 || n < 0 || msb[channel] < 0) return null;
            var ps = Enumerable.Range(0, Count).Where(p => NrpnMsb[p] == msb[channel]).ToList();
            return ps.Count > 0 ? (s, n, ps) : null;
        }
    }

    static readonly NrpnState nrpn = new();

    public static void ResetControllers() => nrpn.Reset();

    // Data entry of a drum setup NRPN with the fixed values: the message to play instead (null = unchanged). The decay
    // NRPN writes both decay rates: when only one is fixed, or both with different values, the second one is
    // returned in again (to send after it), otherwise null.
    public static MidiEvent FixNrpn(int s, int n, List<int> ps, ControlChangeEvent cc, out MidiEvent again)
    {
        again = null;
        var fixedPs = ps.Where(p => isFixed[s, n, p]).ToList();
        if (fixedPs.Count == 0) return null;
        if (fixedPs.Count == 1 && ps.Count == 2)   // one decay fixed: the other keeps the file's value
            again = ParamEvent(s, n, ps.First(p => p != fixedPs[0]), cc.ControlValue);
        else if (fixedPs.Count == 2 && fixedValue[s, n, fixedPs[0]] != fixedValue[s, n, fixedPs[1]])
            again = ParamEvent(s, n, fixedPs[1], fixedValue[s, n, fixedPs[1]]);
        return new ControlChangeEvent((SevenBitNumber)6, (SevenBitNumber)fixedValue[s, n, fixedPs[0]]) { Channel = cc.Channel };
    }

    // NRPN controllers played from the file: a drum setup value is stored, and replaced by the fixed one
    // (see FixNrpn; null = unchanged).
    public static MidiEvent ReadController(ControlChangeEvent cc, out MidiEvent again)
    {
        again = null;
        if (nrpn.Read(cc, SetupOf) is not var (s, n, ps)) return null;
        foreach (int p in ps) file[s, n, p] = cc.ControlValue;
        Version++;
        return FixNrpn(s, n, ps, cc, out again);
    }
}
