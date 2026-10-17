using System.Globalization;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace PowerMIDI.Core;

// Fields of an event in the edit panel of the Event Editor: its kind, its channel, up to three values (label and text)
// and a text (SysEx bytes, text of a meta event). NotEditable: why the event cannot be changed (null: it can).
public sealed record EventForm(string Kind, bool HasChannel, int Channel, IReadOnlyList<string> Labels, IReadOnlyList<string> Values,
                               string TextLabel, string Text, string NotEditable = null)
{
    public bool HasText => TextLabel != null;
}

// What an edit panel gives: an event, or a note (Note On and Note Off), or an error.
public sealed record EventFormResult(MidiEvent Event, (int Channel, int Note, int Velocity, long Length)? Note, string Error);

// Conversion between the events and the fields of the edit panel (Event Editor).
public static class MidiEventForm
{
    // Kinds of events that can be added.
    public static readonly IReadOnlyList<string> NewKinds =
        new[] { "Note", "Control Change", "Program Change", "Pitch Bend", "Channel Pressure", "SysEx", "Tempo", "Text", "Marker", "Lyric" };

    static readonly string[] None = Array.Empty<string>();
    static string Text(int v) => v.ToString(CultureInfo.InvariantCulture);

    // Fields of an event of the list.
    public static EventForm Of(MidiEventRow row)
    {

        int c = row.Channel;
        switch (row.Event)
        {
            case NoteOnEvent on when row.OffIndex >= 0:
                return new("Note", true, c, new[] { "Note", "Velocity", "Length" }, new[] { Text(on.NoteNumber), Text(on.Velocity), row.Length.ToString(CultureInfo.InvariantCulture) }, null, null);
            case NoteOnEvent on:
                return new("Note On", true, c, new[] { "Note", "Velocity" }, new[] { Text(on.NoteNumber), Text(on.Velocity) }, null, null);
            case NoteOffEvent off:
                return new("Note Off", true, c, new[] { "Note", "Velocity" }, new[] { Text(off.NoteNumber), Text(off.Velocity) }, null, null);
            case ControlChangeEvent cc:
                return new("Control Change", true, c, new[] { "Controller", "Value" }, new[] { Text(cc.ControlNumber), Text(cc.ControlValue) }, null, null);
            case ProgramChangeEvent pc:
                return new("Program Change", true, c, new[] { "Program" }, new[] { Text(pc.ProgramNumber + 1) }, null, null);
            case PitchBendEvent pb:
                return new("Pitch Bend", true, c, new[] { "Value" }, new[] { Text(pb.PitchValue - 8192) }, null, null);
            case ChannelAftertouchEvent at:
                return new("Channel Pressure", true, c, new[] { "Value" }, new[] { Text(at.AftertouchValue) }, null, null);
            case NoteAftertouchEvent pat:
                return new("Poly Pressure", true, c, new[] { "Note", "Value" }, new[] { Text(pat.NoteNumber), Text(pat.AftertouchValue) }, null, null);
            case SysExEvent sx:
                return new("SysEx", false, 0, None, None, "Bytes (hex)", MidiEventList.Hex(sx.Data, all: true));
            case SetTempoEvent tempo:
                return new("Tempo", false, 0, new[] { "BPM" }, new[] { (60_000_000.0 / tempo.MicrosecondsPerQuarterNote).ToString("0.###", CultureInfo.InvariantCulture) }, null, null);
            case BaseTextEvent text:
                return new(row.Type, false, 0, None, None, "Text", MidiTools.DecodeText(text.Text));
            default:
                return new(row.Type, false, 0, None, None, null, null, "This kind of event cannot be changed yet");
        }
    }

    // Fields of a new event (default values); ticksPerQuarter: length of a beat, for a note.
    public static EventForm New(string kind, int channel, int ticksPerQuarter) => kind switch
    {
        "Note" => new(kind, true, channel, new[] { "Note", "Velocity", "Length" }, new[] { "60", "100", Text(ticksPerQuarter) }, null, null),
        "Control Change" => new(kind, true, channel, new[] { "Controller", "Value" }, new[] { "7", "100" }, null, null),
        "Program Change" => new(kind, true, channel, new[] { "Program" }, new[] { "1" }, null, null),
        "Pitch Bend" => new(kind, true, channel, new[] { "Value" }, new[] { "0" }, null, null),
        "Channel Pressure" => new(kind, true, channel, new[] { "Value" }, new[] { "0" }, null, null),
        "SysEx" => new(kind, false, 0, None, None, "Bytes (hex)", "41 10 42 12 40 00 7F 00 41"),
        "Tempo" => new(kind, false, 0, new[] { "BPM" }, new[] { "120" }, null, null),
        _ => new(kind, false, 0, None, None, "Text", ""),
    };

    // Event (or note) from the fields of the panel; the kind and the meta type come from form.
    public static EventFormResult Read(EventForm form, int channel, IReadOnlyList<string> values, string text)
    {
        string error = null;
        int Int(int i, int min, int max)
        {
            var label = form.Labels[i];
            if (int.TryParse(values[i]?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= min && v <= max) return v;
            error ??= $"{label}: {min} to {max}";
            return min;
        }
        if (form.HasChannel && channel is < 0 or > 15) return new(null, null, "Channel: 1 to 16");
        MidiEvent e = null;
        switch (form.Kind)
        {
            case "Note":
                int note = Int(0, 0, 127), velocity = Int(1, 1, 127), length = Int(2, 1, int.MaxValue);
                return error != null ? new(null, null, error) : new(null, (channel, note, velocity, length), null);
            case "Note On": e = MidiTools.NoteOn(channel, Int(0, 0, 127), Int(1, 0, 127)); break;
            case "Note Off": e = MidiTools.NoteOff(channel, Int(0, 0, 127), Int(1, 0, 127)); break;
            case "Control Change": e = MidiTools.Cc(channel, Int(0, 0, 127), Int(1, 0, 127)); break;
            case "Program Change": e = MidiTools.Pc(channel, Int(0, 1, 128) - 1); break;
            case "Pitch Bend": e = new PitchBendEvent((ushort)(Int(0, -8192, 8191) + 8192)) { Channel = (FourBitNumber)(byte)channel }; break;
            case "Channel Pressure": e = new ChannelAftertouchEvent((SevenBitNumber)(byte)Int(0, 0, 127)) { Channel = (FourBitNumber)(byte)channel }; break;
            case "Poly Pressure":
                e = new NoteAftertouchEvent((SevenBitNumber)(byte)Int(0, 0, 127), (SevenBitNumber)(byte)Int(1, 0, 127))
                    { Channel = (FourBitNumber)(byte)channel };
                break;
            case "SysEx":
                var bytes = ParseHex(text, out error);
                if (error == null)
                {
                    // Roland DT1 (GS, MT-32, Sound Canvas): the checksum is worked out again.
                    if (bytes.Length >= 9 && bytes[0] == 0x41 && bytes[3] == 0x12) MidiTools.FixRolandChecksum(bytes);
                    e = new NormalSysExEvent(bytes);
                }
                break;
            case "Tempo":
                if (double.TryParse(values[0]?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm) && bpm is >= 4 and <= 1000)
                    e = new SetTempoEvent((long)Math.Round(60_000_000.0 / bpm));
                else error = "BPM: 4 to 1000";
                break;
            default:
                var raw = MidiTools.EncodeText(text ?? "");
                e = form.Kind switch
                {
                    "Marker" => new MarkerEvent(raw), "Lyric" => new LyricEvent(raw), "Cue Point" => new CuePointEvent(raw),
                    "Track Name" => new SequenceTrackNameEvent(raw), "Instrument Name" => new InstrumentNameEvent(raw),
                    "Copyright" => new CopyrightNoticeEvent(raw), "Program Name" => new ProgramNameEvent(raw),
                    "Device Name" => new DeviceNameEvent(raw), "Text" => new TextEvent(raw), _ => null,
                };
                if (e == null) error = "This kind of event cannot be changed yet";
                break;
        }
        return error != null ? new(null, null, error) : new(e, null, null);
    }

    // SysEx bytes typed in hexadecimal (F0 at the start optional; F7 added at the end if missing).
    static byte[] ParseHex(string text, out string error)
    {
        error = null;
        var parts = (text ?? "").Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && parts[0].Equals("F0", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        var bytes = new List<byte>();
        foreach (var p in parts)
        {
            if (!byte.TryParse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)) { error = $"Not a byte: {p}"; return null; }
            bytes.Add(b);
        }
        if (bytes.Count == 0 || (bytes.Count == 1 && bytes[0] == 0xF7)) { error = "Bytes: none"; return null; }
        if (bytes[^1] != 0xF7) bytes.Add(0xF7);
        if (bytes.Take(bytes.Count - 1).Any(b => b > 0x7F)) { error = "Bytes: 00 to 7F between F0 and F7"; return null; }
        return bytes.ToArray();
    }
}
