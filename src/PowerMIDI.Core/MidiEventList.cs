using System.Globalization;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace PowerMIDI.Core;

// One row of the event list (Event Editor): an event of the file, a note being one row (Note On and its Note Off).
// Tick: position in the file; Track: track number (1-based); Channel: 0-15, -1 for an event without channel (SysEx,
// meta); Kind: group used by the filters; Type, Data and Description: the texts of the row; Event: the event (null for a
// note).
public sealed record MidiEventRow(long Tick, int Track, int Channel, EventKind Kind, string Type, string Data, string Description,
                                  MidiEvent Event = null);

public enum EventKind { Note, Controller, Program, PitchBend, Aftertouch, SysEx, Meta }

// The events of a file as a list (Event Editor), sorted by position then track, with readable texts: note names,
// controller names, instruments of the Program Changes in the standard of the file, RPN / NRPN of the data entries,
// tempo, texts and SysEx (resets, GS rhythm part, module display, and whatever describeSysEx knows).
public static class MidiEventList
{
    // instrumentName(channel, program, bank MSB, bank LSB, GS rhythm part): name of the instrument the channel plays.
    // describeSysEx(data): description of a SysEx (null: the generic one).
    public static List<MidiEventRow> Build(MidiFile file, Func<int, int, int, int, int, string> instrumentName,
                                           Func<byte[], string> describeSysEx = null)
    {
        // All the events of all tracks in the order they play (position, then track, then order in the track).
        var objects = new List<(long Tick, int Track, int Order, ITimedObject Object)>();
        int track = 0;
        foreach (var chunk in file.GetTrackChunks())
        {
            track++;
            int order = 0;
            foreach (var o in chunk.GetObjects(ObjectType.TimedEvent | ObjectType.Note)) objects.Add((o.Time, track, order++, o));
        }
        objects.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Track != b.Track ? a.Track.CompareTo(b.Track) : a.Order.CompareTo(b.Order));

        // State of the channels while reading: bank, program, GS rhythm part, RPN / NRPN selected.
        int[] msb = new int[16], lsb = new int[16], program = new int[16], rhythm = new int[16];
        rhythm[9] = 1;
        var parameter = new (bool Rpn, int Msb, int Lsb)?[16];
        var names = new string[16];   // instrument of each channel, worked out again after a change
        string Instrument(int c) => names[c] ??= instrumentName?.Invoke(c, program[c], msb[c], lsb[c], rhythm[c]) ?? "";

        var rows = new List<MidiEventRow>(objects.Count);
        foreach (var (tick, t, _, o) in objects)
        {
            if (o is Note note)
            {
                int c = note.Channel;
                rows.Add(new(tick, t, c, EventKind.Note, "Note",
                             $"{NoteName(note.NoteNumber)} ({note.NoteNumber}) · vel {note.Velocity} · len {note.Length}", Instrument(c)));
                continue;
            }
            var e = ((TimedEvent)o).Event;
            switch (e)
            {
                case NoteOnEvent on:   // a Note On without its Note Off (or the reverse): not paired into a note
                    rows.Add(new(tick, t, on.Channel, EventKind.Note, "Note On", $"{NoteName(on.NoteNumber)} ({on.NoteNumber}) · vel {on.Velocity}", Instrument(on.Channel), e));
                    break;
                case NoteOffEvent off:
                    rows.Add(new(tick, t, off.Channel, EventKind.Note, "Note Off", $"{NoteName(off.NoteNumber)} ({off.NoteNumber})", Instrument(off.Channel), e));
                    break;
                case ControlChangeEvent cc:
                {
                    int c = cc.Channel, n = cc.ControlNumber, v = cc.ControlValue;
                    string text = ControllerName(n);
                    switch (n)
                    {
                        case 0: msb[c] = v; names[c] = null; break;
                        case 32: lsb[c] = v; names[c] = null; break;
                        case 99: parameter[c] = (false, v, parameter[c] is { Rpn: false } p99 ? p99.Lsb : 127); break;
                        case 98: parameter[c] = (false, parameter[c] is { Rpn: false } p98 ? p98.Msb : 127, v); break;
                        case 101: parameter[c] = (true, v, parameter[c] is { Rpn: true } p101 ? p101.Lsb : 127); break;
                        case 100: parameter[c] = (true, parameter[c] is { Rpn: true } p100 ? p100.Msb : 127, v); break;
                        case 6 or 38 when parameter[c] is { } p:
                            text += $" · {ParameterName(p.Rpn, p.Msb, p.Lsb)}";
                            break;
                    }
                    rows.Add(new(tick, t, c, EventKind.Controller, "Control Change", $"{n} = {v}", text, e));
                    break;
                }
                case ProgramChangeEvent pc:
                    program[pc.Channel] = pc.ProgramNumber;
                    names[pc.Channel] = null;
                    rows.Add(new(tick, t, pc.Channel, EventKind.Program, "Program Change",
                                 $"{pc.ProgramNumber + 1} · bank {msb[pc.Channel]} / {lsb[pc.Channel]}", Instrument(pc.Channel), e));
                    break;
                case PitchBendEvent pb:
                    rows.Add(new(tick, t, pb.Channel, EventKind.PitchBend, "Pitch Bend", MidiTools.FormatSigned(pb.PitchValue - 8192), "", e));
                    break;
                case ChannelAftertouchEvent at:
                    rows.Add(new(tick, t, at.Channel, EventKind.Aftertouch, "Channel Pressure", $"{at.AftertouchValue}", "", e));
                    break;
                case NoteAftertouchEvent pat:
                    rows.Add(new(tick, t, pat.Channel, EventKind.Aftertouch, "Poly Pressure",
                                 $"{NoteName(pat.NoteNumber)} ({pat.NoteNumber}) = {pat.AftertouchValue}", "", e));
                    break;
                case SysExEvent sx:
                {
                    var d = sx.Data;
                    int c = ChannelMixer.RhythmPartChannel(d);
                    if (c >= 0) (rhythm[c], names[c]) = (d[7], null);
                    rows.Add(new(tick, t, -1, EventKind.SysEx, "SysEx", Hex(d), describeSysEx?.Invoke(d) ?? DescribeSysEx(d), e));
                    break;
                }
                default:
                    rows.Add(new(tick, t, ChannelOf(e), EventKind.Meta, MetaType(e), MetaData(e), "", e));
                    break;
            }
        }
        return rows;
    }

    // ---------- Values fixed in the interface ----------

    // What replaces an event of the file during playback (and in the saved file): a value fixed in the Mixer or Effects
    // tab (blue there); null if the event plays as it is. Not the data entries of the NRPN / RPN of the Part and Drums
    // pages (the selection of the parameter is in earlier events).
    public static string FixedText(MidiEventRow row)
    {
        int c = row.Channel;
        switch (row.Event)
        {
            case ProgramChangeEvent when ChannelMixer.Override[c]:
            case ControlChangeEvent bank when (int)bank.ControlNumber is 0 or 32 && ChannelMixer.Override[c]:
                return "Instrument chosen in Mixer tab";
            case ControlChangeEvent cc when ChannelMixer.ControllerIndex(cc.ControlNumber) is int k and >= 0 && ChannelMixer.IsFixed(k, c):
                return $"Fixed in Mixer tab: {ChannelMixer.FixedValue(k, c)}";
            case SysExEvent sx:
            {
                var d = sx.Data;
                if (ChannelMixer.RhythmPartChannel(d) is int part and >= 0 && ChannelMixer.RhythmOverride[part] >= 0) return "Drum part chosen in Mixer tab";
                if (GsXgEffects.ValuesOf(d).Any(x => GsXgEffects.IsFixed(x.Param))) return "Fixed in Effects tab";
                var positions = new List<(int Position, int Param)>();
                if (Gm2Effects.ApplyBytes(d, new int[Gm2Effects.Count], positions) && positions.Any(x => Gm2Effects.IsFixed(x.Param))) return "Fixed in Effects tab";
                positions.Clear();
                if (Mt32Effects.ApplyBytes(d, new int[Mt32Effects.Count], new int[9], positions) != Mt32Effects.Changes.None && positions.Any(x => Mt32Effects.IsFixed(x.Param)))
                    return "Fixed in Effects tab";
                if (XgDrumSetup.WithFixedValues(d) != null || XgPartParams.WithFixedValues(d) != null || GsDrumMap.WithFixedValues(d) != null ||
                    GsPartParams.WithFixedValues(d) != null || Gm2DrumParams.WithFixedValues(d) != null || Mt32PartParams.WithFixedValues(d) != null ||
                    Mt32DrumParams.WithFixedValues(d) != null)
                    return "Fixed in Effects tab (Drums / Part)";
                return null;
            }
            default:
                return null;
        }
    }

    // ---------- Texts ----------

    // Note name in the Yamaha notation (C3 = 60).
    static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    public static string NoteName(int n) => $"{NoteNames[n % 12]}{n / 12 - 2}";

    // SysEx bytes in hexadecimal, without F0 / F7 (cut after 48 bytes).
    public static string Hex(byte[] d)
    {
        var bytes = d.Length > 0 && d[^1] == 0xF7 ? d[..^1] : d;
        var text = string.Join(" ", bytes.Take(48).Select(b => b.ToString("X2")));
        return bytes.Length > 48 ? text + $" … ({bytes.Length} bytes)" : text;
    }

    static readonly Dictionary<int, string> Controllers = new()
    {
        [0] = "Bank Select", [1] = "Modulation", [2] = "Breath", [4] = "Foot", [5] = "Portamento Time", [6] = "Data Entry",
        [7] = "Volume", [8] = "Balance", [10] = "Pan", [11] = "Expression", [32] = "Bank Select LSB", [38] = "Data Entry LSB",
        [64] = "Hold", [65] = "Portamento", [66] = "Sostenuto", [67] = "Soft", [71] = "Resonance", [72] = "Release Time",
        [73] = "Attack Time", [74] = "Cutoff", [75] = "Decay Time", [76] = "Vibrato Rate", [77] = "Vibrato Depth",
        [78] = "Vibrato Delay", [84] = "Portamento Control", [91] = "Reverb", [93] = "Chorus", [94] = "Delay / Variation",
        [96] = "Data Increment", [97] = "Data Decrement", [98] = "NRPN LSB", [99] = "NRPN MSB", [100] = "RPN LSB",
        [101] = "RPN MSB", [111] = "Loop start (CC111)", [120] = "All Sound Off", [121] = "Reset All Controllers",
        [122] = "Local Control", [123] = "All Notes Off", [124] = "Omni Off", [125] = "Omni On", [126] = "Mono On", [127] = "Poly On",
    };

    public static string ControllerName(int n) => Controllers.TryGetValue(n, out var name) ? name : $"Controller {n}";

    // RPN / NRPN selected for a data entry.
    static string ParameterName(bool rpn, int msb, int lsb)
    {
        if (!rpn) return $"NRPN {msb:X2} {lsb:X2}";
        return (msb, lsb) switch
        {
            (0, 0) => "Pitch Bend Sensitivity",
            (0, 1) => "Fine Tuning",
            (0, 2) => "Coarse Tuning",
            (0, 5) => "Modulation Depth Range",
            (127, 127) => "RPN null",
            _ => $"RPN {msb:X2} {lsb:X2}",
        };
    }

    // SysEx: resets, GS rhythm part, module display; otherwise its maker and address.
    public static string DescribeSysEx(byte[] d)
    {
        if (d.Length >= 7 && d[0] == 0x41 && d[2] == 0x42 && d[3] == 0x12 && d[4] == 0x40 && d[5] == 0x00 && d[6] == 0x7F) return "GS Reset";
        if (d.Length >= 6 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C && d[3] == 0 && d[4] == 0 && d[5] == 0x7E) return "XG System On";
        if (d.Length >= 4 && d[0] == 0x7E && d[2] == 0x09) return d[3] switch { 1 => "GM System On", 2 => "GM System Off", 3 => "GM2 System On", _ => "GM message" };
        if (d.Length >= 7 && d[0] == 0x41 && d[2] == 0x16 && d[3] == 0x12 && d[4] == 0x7F && d[5] == 0 && d[6] == 0) return "MT-32 Reset";
        if (d.Length >= 6 && d[0] == 0x7F && d[2] == 0x04 && d[3] == 0x01) return $"Master Volume {d[5]}";
        int c = ChannelMixer.RhythmPartChannel(d);
        if (c >= 0) return $"Use for rhythm part ch {c + 1}: {(d[7] == 0 ? "off" : $"drum {d[7]}")}";
        if (DisplayText(d) is { } text) return $"Display: \"{text}\"";
        if (d.Length >= 6 && ((d[0] == 0x41 && d[2] == 0x45 && d[4] == 0x10 && d[5] == 0x01) || (d[0] == 0x43 && d[2] == 0x4C && d[3] == 0x07)))
            return "Display: dot picture";
        if (d.Length >= 7 && d[0] == 0x41 && d[3] == 0x12)
            return $"{d[2] switch { 0x42 => "GS", 0x16 => "MT-32", 0x45 => "Sound Canvas", _ => $"Roland model {d[2]:X2}" }} DT1 {d[4]:X2} {d[5]:X2} {d[6]:X2}";
        if (d.Length >= 6 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C) return $"XG parameter {d[3]:X2} {d[4]:X2} {d[5]:X2}";
        if (d.Length >= 1) return d[0] switch { 0x7E => "Universal non-real time", 0x7F => "Universal real time", 0x41 => "Roland", 0x43 => "Yamaha", _ => $"Maker {d[0]:X2}" };
        return "";
    }

    // Text written on the module display by a SysEx (MT-32, Sound Canvas, XG), or null.
    static string DisplayText(byte[] d)
    {
        int first, last = d.Length - 2;
        if (d.Length >= 10 && d[0] == 0x41 && d[2] == 0x16 && d[3] == 0x12 && d[4] == 0x20 && d[5] == 0x00) first = 7;
        else if (d.Length >= 10 && d[0] == 0x41 && d[2] == 0x45 && d[3] == 0x12 && d[4] == 0x10 && d[5] == 0x00) first = 7;
        else if (d.Length >= 8 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C && d[3] == 0x06 && d[4] == 0x00) (first, last) = (6, d.Length - 1);
        else return null;
        var chars = d.Skip(first).Take(Math.Max(0, last - first)).TakeWhile(b => b >= 0x20).Select(b => b < 0x7F ? (char)b : ' ');
        return new string(chars.ToArray()).Trim();
    }

    static int ChannelOf(MidiEvent e) => e is ChannelPrefixEvent prefix ? prefix.Channel : -1;

    static string MetaType(MidiEvent e) => e switch
    {
        SetTempoEvent => "Tempo",
        TimeSignatureEvent => "Time Signature",
        KeySignatureEvent => "Key Signature",
        SequenceTrackNameEvent => "Track Name",
        InstrumentNameEvent => "Instrument Name",
        CopyrightNoticeEvent => "Copyright",
        MarkerEvent => "Marker",
        CuePointEvent => "Cue Point",
        LyricEvent => "Lyric",
        TextEvent => "Text",
        ProgramNameEvent => "Program Name",
        DeviceNameEvent => "Device Name",
        PortPrefixEvent => "Port",
        ChannelPrefixEvent => "Channel Prefix",
        SmpteOffsetEvent => "SMPTE Offset",
        SequenceNumberEvent => "Sequence Number",
        SequencerSpecificEvent => "Sequencer Specific",
        EndOfTrackEvent => "End of Track",
        _ => e.EventType.ToString(),
    };

    static string MetaData(MidiEvent e) => e switch
    {
        SetTempoEvent tempo => string.Format(CultureInfo.InvariantCulture, "{0:0.##} bpm", 60_000_000.0 / tempo.MicrosecondsPerQuarterNote),
        TimeSignatureEvent ts => $"{ts.Numerator}/{ts.Denominator}",
        KeySignatureEvent ks => $"{KeyName(ks.Key, ks.Scale)}",
        BaseTextEvent text => MidiTools.DecodeText(text.Text),
        PortPrefixEvent port => $"{port.Port + 1}",
        ChannelPrefixEvent prefix => $"{prefix.Channel + 1}",
        SequenceNumberEvent number => $"{number.Number}",
        SequencerSpecificEvent specific => string.Join(" ", specific.Data.Take(48).Select(b => b.ToString("X2"))),
        SmpteOffsetEvent smpte => $"{smpte.Hours:00}:{smpte.Minutes:00}:{smpte.Seconds:00}:{smpte.Frames:00}",
        _ => "",
    };

    static readonly string[] MajorKeys = { "Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#" };
    static readonly string[] MinorKeys = { "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#", "G#", "D#", "A#" };
    static string KeyName(int sharps, int scale) =>
        sharps is >= -7 and <= 7 ? (scale == 0 ? $"{MajorKeys[sharps + 7]} major" : $"{MinorKeys[sharps + 7]} minor") : $"{sharps}";
}
