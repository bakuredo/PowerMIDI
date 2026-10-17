using System.Globalization;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace PowerMIDI.Core;

// One row of the event list (Event Editor): an event of the file, a note being one row (Note On and its Note Off).
// Tick: position in the file; Track: track number (1-based); Channel: 0-15, -1 for an event without channel (SysEx,
// meta); Kind: group used by the filters; Type, Data and Description: the texts of the row; Event: the event (the Note On
// of a note); Index: its place in its track (for the changes, see MidiEventEdits); OffIndex / Length: the Note Off of a
// note (-1 / 0 otherwise); Entry: for a data entry (CC6, CC38), the RPN / NRPN it writes.
public sealed record MidiEventRow(long Tick, int Track, int Channel, EventKind Kind, string Type, string Data, string Description,
                                  MidiEvent Event = null, int Index = -1, int OffIndex = -1, long Length = 0, DataEntry? Entry = null);

// Setting: not an event of the file, a value fixed in the Mixer or Effects tab, sent at the start (see EventEditorViewModel).
public enum EventKind { Note, Controller, Program, PitchBend, Aftertouch, SysEx, Meta, Setting }

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
        // All the events of all tracks in the order they play (position, then track, then place in the track). A note:
        // its Note On with the place and position of its Note Off (the first Note Off of the same channel and key after it).
        var objects = new List<(long Tick, int Track, int Index, MidiEvent Event, int OffIndex, long OffTick)>();
        int track = 0;
        foreach (var chunk in file.GetTrackChunks())
        {
            track++;
            var held = new Dictionary<(int, int), Queue<int>>();   // Note On waiting for their Note Off, by channel and key
            long time = 0;
            for (int i = 0; i < chunk.Events.Count; i++)
            {
                var e = chunk.Events[i];
                time += e.DeltaTime;
                if (e is NoteOnEvent on && on.Velocity > 0)
                {
                    var key = ((int)on.Channel, (int)on.NoteNumber);
                    if (!held.TryGetValue(key, out var queue)) held[key] = queue = new();
                    queue.Enqueue(objects.Count);
                    objects.Add((time, track, i, e, -1, 0));
                }
                else if (e is NoteEvent off && held.TryGetValue(((int)off.Channel, (int)off.NoteNumber), out var waiting) && waiting.Count > 0)
                {
                    int o = waiting.Dequeue();
                    objects[o] = objects[o] with { OffIndex = i, OffTick = time };
                }
                else objects.Add((time, track, i, e, -1, 0));
            }
        }
        objects.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Track != b.Track ? a.Track.CompareTo(b.Track) : a.Index.CompareTo(b.Index));

        // State of the channels while reading: bank, program, GS rhythm part, RPN / NRPN selected.
        int[] msb = new int[16], lsb = new int[16], program = new int[16], rhythm = new int[16];
        rhythm[9] = 1;
        var nrpn = new NrpnState();   // as the player reads them (ChannelMixer)
        var names = new string[16];   // instrument of each channel, worked out again after a change
        string Instrument(int c) => names[c] ??= instrumentName?.Invoke(c, program[c], msb[c], lsb[c], rhythm[c]) ?? "";

        var rows = new List<MidiEventRow>(objects.Count);
        foreach (var (tick, t, index, e, offIndex, offTick) in objects)
        {
            MidiEventRow Row(int channel, EventKind kind, string type, string data, string description) =>
                new(tick, t, channel, kind, type, data, description, e, index);
            switch (e)
            {
                case NoteOnEvent on when offIndex >= 0:
                    rows.Add(Row(on.Channel, EventKind.Note, "Note", $"{NoteName(on.NoteNumber)} ({on.NoteNumber}) · vel {on.Velocity} · len {offTick - tick}",
                                 Instrument(on.Channel)) with { OffIndex = offIndex, Length = offTick - tick });
                    break;
                case NoteOnEvent on:   // a Note On without its Note Off (or the reverse): not paired into a note
                    rows.Add(Row(on.Channel, EventKind.Note, "Note On", $"{NoteName(on.NoteNumber)} ({on.NoteNumber}) · vel {on.Velocity}", Instrument(on.Channel)));
                    break;
                case NoteOffEvent off:
                    rows.Add(Row(off.Channel, EventKind.Note, "Note Off", $"{NoteName(off.NoteNumber)} ({off.NoteNumber})", Instrument(off.Channel)));
                    break;
                case ControlChangeEvent cc:
                {
                    int c = cc.Channel, n = cc.ControlNumber, v = cc.ControlValue;
                    string text = ControllerName(n);
                    if (n == 0) (msb[c], names[c]) = (v, null);
                    if (n == 32) (lsb[c], names[c]) = (v, null);
                    var entry = nrpn.Read(cc);
                    if (entry is { } p) text += $" · {ParameterName(p.Rpn, p.Msb, p.Lsb)}";
                    rows.Add(Row(c, EventKind.Controller, "Control Change", $"{n} = {v}", text) with { Entry = entry });
                    break;
                }
                case ProgramChangeEvent pc:
                    program[pc.Channel] = pc.ProgramNumber;
                    names[pc.Channel] = null;
                    rows.Add(Row(pc.Channel, EventKind.Program, "Program Change", $"{pc.ProgramNumber + 1} · bank {msb[pc.Channel]} / {lsb[pc.Channel]}",
                                 Instrument(pc.Channel)));
                    break;
                case PitchBendEvent pb:
                    rows.Add(Row(pb.Channel, EventKind.PitchBend, "Pitch Bend", MidiTools.FormatSigned(pb.PitchValue - 8192), ""));
                    break;
                case ChannelAftertouchEvent at:
                    rows.Add(Row(at.Channel, EventKind.Aftertouch, "Channel Pressure", $"{at.AftertouchValue}", ""));
                    break;
                case NoteAftertouchEvent pat:
                    rows.Add(Row(pat.Channel, EventKind.Aftertouch, "Poly Pressure", $"{NoteName(pat.NoteNumber)} ({pat.NoteNumber}) = {pat.AftertouchValue}", ""));
                    break;
                case SysExEvent sx:
                {
                    var d = sx.Data;
                    int c = ChannelMixer.RhythmPartChannel(d);
                    if (c >= 0) (rhythm[c], names[c]) = (d[7], null);
                    rows.Add(Row(-1, EventKind.SysEx, "SysEx", Hex(d), describeSysEx?.Invoke(d) ?? DescribeSysEx(d)));
                    break;
                }
                default:
                    rows.Add(Row(ChannelOf(e), EventKind.Meta, MetaType(e), MetaData(e), ""));
                    break;
            }
        }
        return rows;
    }

    // ---------- Values fixed in the interface ----------

    // What replaces an event of the file during playback (and in the saved file): a value fixed in the Mixer or Effects
    // tab (blue there), or the mode forced in the interface; null if the event plays as it is. Same cases as
    // ChannelMixer.Callback (the data entries: the RPN / NRPN selected before them, see Build). instrumentName: as for
    // Build, to name the instrument chosen in the Mixer tab.
    public static string FixedText(MidiEventRow row, Func<int, int, int, int, int, string> instrumentName = null)
    {
        int c = row.Channel;
        // The instrument chosen in the Mixer tab for a channel.
        (int, int, int)? Chosen(int ch) => ChannelMixer.Override[ch] ? (ChannelMixer.OverrideMsb[ch], ChannelMixer.OverrideLsb[ch], ChannelMixer.OverrideProgram[ch]) : null;
        string Instrument(int ch)
        {
            var (msb, lsb, program) = Chosen(ch).Value;
            var name = instrumentName?.Invoke(ch, program, msb, lsb, ChannelMixer.Rhythm(ch));
            return string.IsNullOrEmpty(name) ? $"Mixer: bank {msb} / {lsb}, PC {program + 1}" : $"Mixer: {name} (bank {msb} / {lsb}, PC {program + 1})";
        }
        switch (row.Event)
        {
            case ProgramChangeEvent when ChannelMixer.Override[c]:
            case ControlChangeEvent bank when (int)bank.ControlNumber is 0 or 32 && ChannelMixer.Override[c]:
                return Instrument(c);
            case ControlChangeEvent cc when ChannelMixer.ControllerIndex(cc.ControlNumber) is int k and >= 0 && ChannelMixer.IsFixed(k, c):
                return $"Fixed in Mixer tab: {ChannelMixer.FixedValue(k, c)}";
            case ControlChangeEvent cc when (int)cc.ControlNumber == ChannelMixer.PortamentoSwitch && ChannelMixer.IsFixed(ChannelMixer.PortamentoTime, c):
                return $"Portamento time fixed in Mixer tab: switch {ChannelMixer.PortamentoSwitchValue(c)}";
            case ControlChangeEvent cc when (int)cc.ControlNumber is 126 or 127:
                return (ChannelMixer.GsMode ? GsPartParams.FixMonoPoly(cc) : ChannelMixer.XgMode ? XgPartParams.FixMonoPoly(cc) : null) != null
                    ? "Fixed in Effects tab (Part)" : null;
            case ControlChangeEvent cc when row.Entry is { } entry:
                return IsDataEntryFixed(cc, entry) ? "Fixed in Effects tab (Drums / Part)" : null;
            case SysExEvent sx:
            {
                var d = sx.Data;
                if (ChannelMixer.ForceMode && (MidiFileEditor.IsReset(sx) || MidiFileEditor.IsOtherModeSysEx(d, ChannelMixer.DropManufacturers)))
                    return "Ignored: mode forced";
                if (ChannelMixer.IsVoiceMapSysEx(d) && ChannelMixer.VoiceMapOverride >= 0) return "Voice map chosen in Mixer tab";
                if (ChannelMixer.IsGsDefaultMapSysEx(d) && ChannelMixer.GsDefaultMapOverride >= 1) return "Default map chosen in Mixer tab";
                if (ChannelMixer.MultiPartChannel(d) is int mp and >= 0)
                {
                    if (Chosen(mp) is { } chosen && ChannelMixer.ReplaceMultiPart(d, chosen, k => -1) != null) return Instrument(mp);
                    if (ChannelMixer.ReplaceMultiPart(d, null, k => ChannelMixer.IsFixed(k, mp) ? ChannelMixer.FixedValue(k, mp) : -1, XgDrumSetup.PartModeOverride[mp]) != null)
                        return "Fixed in Mixer tab (Multi Part)";
                }
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

    // A data entry replaced by a value of the Part or Drums page, in the mode of the track (as ChannelMixer.Callback).
    static bool IsDataEntryFixed(ControlChangeEvent cc, DataEntry entry)
    {
        if (ChannelMixer.GsMode)
            return GsDrumMap.NrpnParam(cc.Channel, entry, GsDrumMap.MapOf) is { } map && GsDrumMap.IsFixed(map.Map, map.Note, map.Param) ||
                   GsPartParams.FixDataEntry(cc, entry) != null;
        if (ChannelMixer.Gm2Mode) return Gm2PartParams.FixDataEntry(cc, entry) != null;
        if (!ChannelMixer.XgMode) return false;
        return XgDrumSetup.NrpnParams(cc.Channel, entry, XgDrumSetup.SetupOf) is { } setup && setup.Params.Any(p => XgDrumSetup.IsFixed(setup.Setup, setup.Note, p)) ||
               XgPartParams.FixDataEntry(cc, entry, out _) != null;
    }

    // ---------- Texts ----------

    // Note name in the Yamaha notation (C3 = 60).
    static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    public static string NoteName(int n) => $"{NoteNames[n % 12]}{n / 12 - 2}";

    // SysEx bytes in hexadecimal, without F0 / F7 (cut after 48 bytes, unless all).
    public static string Hex(byte[] d, bool all = false)
    {
        var bytes = d.Length > 0 && d[^1] == 0xF7 ? d[..^1] : d;
        var text = string.Join(" ", (all ? bytes : bytes.Take(48)).Select(b => b.ToString("X2")));
        return !all && bytes.Length > 48 ? text + $" … ({bytes.Length} bytes)" : text;
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
