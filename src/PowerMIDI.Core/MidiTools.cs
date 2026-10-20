using System.Text;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;

namespace PowerMIDI.Core;

// MIDI helpers shared by the player: resets, mode detection, output ports.
public static class MidiTools
{
    public static readonly string[] Extensions = { ".mid", ".midi", ".kar", ".rmi" };

    // Reset SysEx of each mode (DryWetMIDI: data without the leading F0, with the trailing F7).
    static readonly Dictionary<string, byte[]> Resets = new()
    {
        ["GS"] = new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 },   // GS Reset
        ["XG"] = new byte[] { 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7 },               // XG System On
        ["GM"] = new byte[] { 0x7E, 0x7F, 0x09, 0x01, 0xF7 },                                 // GM System On
        ["GM2"] = new byte[] { 0x7E, 0x7F, 0x09, 0x03, 0xF7 },                                // GM2 System On
        ["MT-32"] = new byte[] { 0x41, 0x10, 0x16, 0x12, 0x7F, 0x00, 0x00, 0x00, 0x01, 0xF7 }, // MT-32 All Parameters Reset
    };

    public static bool IsMidiFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static string[] GetOutputNames()
    {
        var devices = OutputDevice.GetAll().ToList();
        var names = devices.Select(d => d.Name).ToArray();
        foreach (var d in devices) d.Dispose();
        return names;
    }

    // Preferred port: the Sound Canvas if connected, otherwise the Windows GS synth.
    public static string GetPreferredOutput(IReadOnlyList<string> names)
    {
        foreach (var pattern in new[] { "SC-8850", "SC-88", "Sound Canvas", "Roland", "GS Wavetable" })
        {
            var match = names.FirstOrDefault(n => n.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return names.FirstOrDefault();
    }

    public static void SendSysEx(IOutputDevice device, byte[] data) => device.SendEvent(new NormalSysExEvent(data));

    public static ControlChangeEvent Cc(int channel, int number, int value) =>
        new((SevenBitNumber)(byte)number, (SevenBitNumber)(byte)value) { Channel = (FourBitNumber)(byte)channel };

    public static ProgramChangeEvent Pc(int channel, int program) =>
        new((SevenBitNumber)(byte)program) { Channel = (FourBitNumber)(byte)channel };

    // Plays a note on its own (Audition buttons): Note On now, its Note Off after a while.
    public static void PlayNote(IOutputDevice device, int channel, int note, int velocity, TimeSpan length)
    {
        if (device == null) return;
        device.SendEvent(NoteOn(channel, note, Math.Clamp(velocity, 1, 127)));
        Task.Delay(length).ContinueWith(_ =>
        {
            try { device.SendEvent(NoteOff(channel, note, 0)); }
            catch (Exception) { }   // port closed meanwhile
        });
    }

    public static NoteOnEvent NoteOn(int channel, int note, int velocity) =>
        new((SevenBitNumber)(byte)note, (SevenBitNumber)(byte)velocity) { Channel = (FourBitNumber)(byte)channel };

    public static NoteOffEvent NoteOff(int channel, int note, int velocity) =>
        new((SevenBitNumber)(byte)note, (SevenBitNumber)(byte)velocity) { Channel = (FourBitNumber)(byte)channel };

    // Recomputes the Roland checksum of a DT1 SysEx (GS, MT-32): address and data bytes.
    public static void FixRolandChecksum(byte[] data)
    {
        int sum = 0;
        for (int i = 4; i < data.Length - 2; i++) sum += data[i];
        data[data.Length - 2] = (byte)((128 - sum % 128) % 128);
    }

    public static void SendPanic(IOutputDevice device)
    {
        for (int ch = 0; ch < 16; ch++)
        {
            device.SendEvent(Cc(ch, 120, 0));   // All Sound Off
            device.SendEvent(Cc(ch, 121, 0));   // Reset All Controllers
            device.SendEvent(Cc(ch, 123, 0));   // All Notes Off
        }
    }

    // Silences every note at a pause, without changing the state of the channels. The playback library only releases
    // the notes it holds (one per key): a key started again while held (two Note On) keeps a voice, and the notes
    // released while a pedal is down keep ringing. All Sound Off (GS, XG, GM2) and All Notes Off (MT-32, which does
    // not know All Sound Off) are sent with the pedals up, then the pedals are put back as the file left them.
    public static void ReleaseNotes(IOutputDevice device, int[] sustain, int[] sostenuto)
    {
        for (int ch = 0; ch < 16; ch++)
        {
            if (sustain[ch] >= 64) device.SendEvent(Cc(ch, 64, 0));
            if (sostenuto[ch] >= 64) device.SendEvent(Cc(ch, 66, 0));
            device.SendEvent(Cc(ch, 120, 0));   // All Sound Off
            device.SendEvent(Cc(ch, 123, 0));   // All Notes Off
            if (sustain[ch] >= 64) device.SendEvent(Cc(ch, 64, sustain[ch]));
            if (sostenuto[ch] >= 64) device.SendEvent(Cc(ch, 66, sostenuto[ch]));
        }
    }

    public static void SendReset(IOutputDevice device, string standard)
    {
        if (Resets.TryGetValue(standard, out var reset))
        {
            SendSysEx(device, reset);
            // The module needs time after a reset; an MT-32 restarts completely (longer).
            Thread.Sleep(standard == "MT-32" ? 500 : 250);
        }
    }

    // Manufacturers of the SysEx specific to the other modes (ignored with a forced mode): 41 Roland (GS, MT-32), 43 Yamaha (XG).
    public static int[] GetOtherModeManufacturers(string mode) => mode switch
    {
        "GS" or "MT-32" => new[] { 0x43 },
        "XG" => new[] { 0x41 },
        "GM" or "GM2" => new[] { 0x41, 0x43 },
        _ => Array.Empty<int>(),
    };

    // Messages to write into a file to force a mode: the mode's reset.
    public static MidiEvent[] GetModeEvents(string mode)
    {
        return Resets.TryGetValue(mode, out var data) ? new MidiEvent[] { new NormalSysExEvent(data) } : Array.Empty<MidiEvent>();
    }

    // Standard of a file that says nothing about it, and the one shown when no track is loaded (mode Auto).
    public const string DefaultStandard = "GM";

    // Standard of a file based on its SysEx: XG > GS > MT-32 > GM2 > GM.
    // MT-32: Roland SysEx with model 16h (F0 41 dev 16 12 …), sent by files for the MT-32 / CM-32L.
    // The file's last reset takes priority (GS Reset, XG System On, GM / GM2 On): it is the mode it
    // leaves the module in (e.g. an XG file forced to GS keeps its XG effect SysEx, but starts with a GS Reset).
    public static string GetStandard(MidiFile file)
    {
        var found = new HashSet<string>();
        string lastReset = null;
        long lastResetTime = -1;
        foreach (var chunk in file.GetTrackChunks())
        {
            long time = 0;
            foreach (var e in chunk.Events)
            {
                time += e.DeltaTime;
                if (e is not SysExEvent sysEx) continue;
                var d = sysEx.Data;
                if (d.Length < 4) continue;
                string reset = null;
                if (IsGsReset(d)) reset = "GS";
                else if (d.Length >= 6 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C && d[3] == 0 && d[4] == 0 && d[5] == 0x7E) reset = "XG";
                else if (d[0] == 0x7E && d[2] == 0x09 && d[3] == 0x03) reset = "GM2";
                else if (d[0] == 0x7E && d[2] == 0x09 && d[3] == 0x01) reset = "GM";
                else if (d.Length >= 7 && d[0] == 0x41 && d[2] == 0x16 && d[3] == 0x12 && d[4] == 0x7F && d[5] == 0 && d[6] == 0) reset = "MT-32";
                if (reset != null && time >= lastResetTime) { lastReset = reset; lastResetTime = time; }

                if (d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C) found.Add("XG");
                else if (d[0] == 0x41 && d[2] == 0x42 && d[3] == 0x12) found.Add("GS");
                else if (d[0] == 0x41 && d[2] == 0x16 && d[3] == 0x12) found.Add("MT-32");
                else if (d[0] == 0x7E && d[2] == 0x09 && d[3] == 0x03) found.Add("GM2");
                else if (d[0] == 0x7E && d[2] == 0x09 && d[3] == 0x01) found.Add("GM");
            }
        }
        // A GM On followed by GS / XG SysEx (without a reset of that mode): the file still targets GS / XG.
        if (lastReset != null && !(lastReset == "GM" && (found.Contains("GS") || found.Contains("XG") || found.Contains("MT-32"))))
            return lastReset;
        foreach (var std in new[] { "XG", "GS", "MT-32", "GM2", "GM" })
            if (found.Contains(std)) return std;
        return DefaultStandard;
    }

    public static TimeSpan GetDuration(MidiFile file) => (TimeSpan)file.GetDuration<MetricTimeSpan>();

    // Loop of a video game track, in ticks: markers "loopStart" / "loopEnd" (any case, spaces or "_" allowed;
    // also as text or cue point events), EMIDI-style markers "[" / "]", or CC111 (RPG Maker: loop start, the end
    // of the file being the loop end). The end is the first one after the start; none: the end of the file.
    // Null if the file has no loop start, or an empty loop.
    public static (long Start, long End, string Source)? GetLoop(MidiFile file)
    {
        long? start = null, end = null;
        string source = null;
        var events = file.GetTimedEvents().ToList();
        if (events.Count == 0) return null;
        foreach (var t in events)
        {
            var point = LoopPointOf(t.Event);
            if (start == null && point == LoopPoint.Start)
            {
                start = t.Time;
                source = t.Event is BaseTextEvent text ? text.Text.Trim() : "CC111";
            }
            else if (start != null && end == null && point == LoopPoint.End) { end = t.Time; source += " / " + ((BaseTextEvent)t.Event).Text.Trim(); }
        }
        if (start == null) return null;
        end ??= events.Max(t => t.Time);
        return end > start ? (start.Value, end.Value, source) : null;
    }

    // GS Reset (DT1 40 00 7F), or System Mode Set of the SC-88 and later (DT1 00 00 7F: single / double module mode),
    // which initializes the module in the same way.
    public static bool IsGsReset(byte[] d) =>
        d.Length >= 7 && d[0] == 0x41 && d[2] == 0x42 && d[3] == 0x12 && d[5] == 0x00 && d[6] == 0x7F && d[4] is 0x40 or 0x00;

    public enum LoopPoint { None, Start, End }

    // What an event is for the loop (see GetLoop): a marker (or text, cue point) loopStart / "[" or a CC111 starts it,
    // a marker loopEnd / "]" ends it.
    public static LoopPoint LoopPointOf(MidiEvent e)
    {
        if (e is ControlChangeEvent cc) return cc.ControlNumber == 111 ? LoopPoint.Start : LoopPoint.None;
        if (e is not (MarkerEvent or TextEvent or CuePointEvent)) return LoopPoint.None;
        var text = ((BaseTextEvent)e).Text?.Trim().Replace(" ", "").Replace("_", "").ToLowerInvariant();
        return text is "loopstart" or "[" ? LoopPoint.Start : text is "loopend" or "]" ? LoopPoint.End : LoopPoint.None;
    }

    // Values of the controllers a file never sets (those of the modules after a reset), for GetLoopRestore.
    static readonly Dictionary<int, int> ControllerDefaults = new()
    {
        [1] = 0, [5] = 0, [7] = 100, [10] = 64, [11] = 127, [64] = 0, [65] = 0, [66] = 0, [67] = 0,
        [71] = 64, [72] = 64, [73] = 64, [74] = 64, [75] = 64, [76] = 64, [77] = 64, [78] = 64, [91] = 40, [93] = 0, [94] = 0,
    };

    // Events to send when a loop restarts: the state of the file at the loop start (controllers, instrument,
    // pitch bend and its range) where it differs from the state at the loop end, e.g. the volume after a fade out.
    // Not restored: SysEx, NRPN and the other RPN. The events at the loop start itself are played again by the loop.
    public static List<MidiEvent> GetLoopRestore(MidiFile file, long start, long end)
    {
        var cc = new int[16, 128];
        int[] program = new int[16], pitch = new int[16], bendRange = new int[16], rpn = new int[16];
        for (int ch = 0; ch < 16; ch++)
        {
            for (int n = 0; n < 128; n++) cc[ch, n] = -1;
            program[ch] = -1; pitch[ch] = 8192; bendRange[ch] = -1; rpn[ch] = -1;
        }
        int[,] startCc = null;
        int[] startProgram = null, startPitch = null, startBendRange = null;
        foreach (var t in file.GetTimedEvents())
        {
            if (t.Time >= end) break;
            if (startCc == null && t.Time >= start)
            {
                startCc = (int[,])cc.Clone();
                startProgram = (int[])program.Clone(); startPitch = (int[])pitch.Clone(); startBendRange = (int[])bendRange.Clone();
            }
            switch (t.Event)
            {
                case ControlChangeEvent c:
                    int number = c.ControlNumber, value = c.ControlValue;
                    cc[c.Channel, number] = value;
                    if (number is 98 or 99) rpn[c.Channel] = -1;
                    else if (number is 100 or 101) rpn[c.Channel] = cc[c.Channel, 101] == 0 && cc[c.Channel, 100] == 0 ? 0 : -1;
                    else if (number == 6 && rpn[c.Channel] == 0) bendRange[c.Channel] = value;
                    break;
                case ProgramChangeEvent p: program[p.Channel] = p.ProgramNumber; break;
                case PitchBendEvent b: pitch[b.Channel] = b.PitchValue; break;
            }
        }
        var events = new List<MidiEvent>();
        if (startCc == null) return events;
        for (int ch = 0; ch < 16; ch++)
        {
            // Instrument: bank then program, as a whole.
            if (startProgram[ch] >= 0 && (program[ch] != startProgram[ch] || cc[ch, 0] != startCc[ch, 0] || cc[ch, 32] != startCc[ch, 32]))
            {
                if (startCc[ch, 0] >= 0) events.Add(Cc(ch, 0, startCc[ch, 0]));
                if (startCc[ch, 32] >= 0) events.Add(Cc(ch, 32, startCc[ch, 32]));
                events.Add(Pc(ch, startProgram[ch]));
            }
            for (int n = 1; n < 120; n++)
            {
                if (n is 6 or 32 or 38 or (>= 96 and <= 101) || cc[ch, n] == startCc[ch, n]) continue;
                int value = startCc[ch, n];
                if (value < 0 && !ControllerDefaults.TryGetValue(n, out value)) continue;
                if (value != cc[ch, n]) events.Add(Cc(ch, n, value));
            }
            if (bendRange[ch] != startBendRange[ch])
            {
                events.Add(Cc(ch, 101, 0)); events.Add(Cc(ch, 100, 0));
                events.Add(Cc(ch, 6, startBendRange[ch] >= 0 ? startBendRange[ch] : 2));
                events.Add(Cc(ch, 101, 127)); events.Add(Cc(ch, 100, 127));
            }
            if (pitch[ch] != startPitch[ch]) events.Add(new PitchBendEvent((ushort)startPitch[ch]) { Channel = (FourBitNumber)(byte)ch });
        }
        return events;
    }

    // Notes to turn off when the loop (start-end, ticks) restarts, as the game engines these loops are written for do:
    // - those ending between from and the loop end: the playback library loops as soon as its clock passes the loop
    //   end, without playing the events of the last clock step; with a coarse clock (Windows lowers the timer resolution
    //   of a process without a visible window) or a short tick, the Note Off of a note ending right at the loop end can
    //   be skipped, and the note then hangs;
    // - those started in the loop and held past its end (a pad on the loop point): their Note Off, after the loop end,
    //   is never played while looping; the next pass plays them again from their start.
    // Not the notes started before the loop and held across it (a drone from the intro): the loop never plays them
    // again, they keep sounding (same channel and key).
    public static List<(int Channel, int Note)> GetLoopEndNotes(MidiFile file, long start, long from, long end)
    {
        var notes = file.GetNotes().Where(n => n.Time < end).ToList();
        var drones = notes.Where(n => n.Time < start && n.EndTime > end).Select(n => ((int)n.Channel, (int)n.NoteNumber)).ToHashSet();
        return notes.Where(n => n.EndTime >= from && n.EndTime <= end || n.Time >= start && n.EndTime > end)
                    .Select(n => ((int)n.Channel, (int)n.NoteNumber)).Where(k => !drones.Contains(k)).Distinct().ToList();
    }

    // ---------- Texts of a file (title, copyright, comments) ----------
    // A MIDI file does not say how its texts are encoded. They are read and written byte for byte (Latin-1: one
    // character per byte), so that a saved file keeps them as they were, and decoded for display only (DecodeText).
    static readonly Encoding ByteEncoding = Encoding.Latin1;

    public static MidiFile ReadFile(string path) => MidiFile.Read(path, Reading());
    public static MidiFile ReadFile(Stream stream) => MidiFile.Read(stream, Reading());

    // Damaged files are read as far as possible, as most players do (about 1 % of the VGMusic archive): a value out of
    // range is brought back into it (a controller at 128: 127), truncated data and a track whose size is not the one
    // declared are taken as they are. A file without MIDI header is still refused (not a MIDI file).
    static ReadingSettings Reading() => new()
    {
        TextEncoding = ByteEncoding,
        InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.SnapToLimits,
        InvalidMetaEventParameterValuePolicy = InvalidMetaEventParameterValuePolicy.SnapToLimits,
        InvalidSystemCommonEventParameterValuePolicy = InvalidSystemCommonEventParameterValuePolicy.SnapToLimits,
        NotEnoughBytesPolicy = NotEnoughBytesPolicy.Ignore,
        InvalidChunkSizePolicy = InvalidChunkSizePolicy.Ignore,
        UnexpectedTrackChunksCountPolicy = UnexpectedTrackChunksCountPolicy.Ignore,
        MissedEndOfTrackPolicy = MissedEndOfTrackPolicy.Ignore,
        ExtraTrackChunkPolicy = ExtraTrackChunkPolicy.Read,
    };

    public static void WriteFile(MidiFile file, string path) =>
        file.Write(path, true, settings: new WritingSettings { TextEncoding = ByteEncoding });

    static readonly Encoding ShiftJis = GetCodePage(932), Windows1252 = GetCodePage(1252);

    static Encoding GetCodePage(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    // Text of a file (read by ReadFile) as it was meant: UTF-8 if its bytes are valid UTF-8; Shift-JIS (Japanese
    // files) if they are valid Shift-JIS with a lead byte 81-9F, which Western text hardly has; otherwise
    // Windows-1252 (accented letters).
    // Text typed for a text event (Event Editor): written in UTF-8 when it has other characters than ASCII (DecodeText
    // reads it back), as the bytes of the file are kept one character per byte.
    public static string EncodeText(string text) =>
        text.All(c => c < 0x80) ? text : ByteEncoding.GetString(Encoding.UTF8.GetBytes(text));

    public static string DecodeText(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.All(c => c < 0x80) || raw.Any(c => c > 0xFF)) return raw;
        var bytes = ByteEncoding.GetBytes(raw);
        try { return new UTF8Encoding(false, true).GetString(bytes); } catch (DecoderFallbackException) { }
        if (bytes.Any(b => b >= 0x81 && b <= 0x9F))
            try { return ShiftJis.GetString(bytes); } catch (DecoderFallbackException) { }
        try { return Windows1252.GetString(bytes); } catch (DecoderFallbackException) { return raw; }
    }

    // Title and descriptive texts of a file (null / empty if it has none).
    // Title: name of the first track (Sequence / Track Name, the usual convention), otherwise the karaoke
    // "@T" text. Texts: the Text events placed before the first note (credits, comments), not the lyrics
    // that follow; karaoke "@" tags without their prefix. At most 10.
    public static (string Title, string Copyright, IReadOnlyList<string> Texts) GetInfo(MidiFile file)
    {
        var chunks = file.GetTrackChunks().ToList();
        string title = DecodeText(chunks.FirstOrDefault()?.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text)?.Trim();
        string copyright = null;
        var texts = new List<(long Time, string Text)>();
        long firstNote = long.MaxValue;
        foreach (var chunk in chunks)
        {
            long time = 0;
            foreach (var e in chunk.Events)
            {
                time += e.DeltaTime;
                switch (e)
                {
                    case NoteOnEvent on when on.Velocity > 0: firstNote = Math.Min(firstNote, time); break;
                    case CopyrightNoticeEvent c when copyright == null && !string.IsNullOrWhiteSpace(c.Text): copyright = DecodeText(c.Text).Trim(); break;
                    case TextEvent t when !string.IsNullOrWhiteSpace(t.Text): texts.Add((time, DecodeText(t.Text).Trim())); break;
                }
            }
        }
        if (string.IsNullOrEmpty(title))
            title = texts.Select(t => t.Text).FirstOrDefault(t => t.StartsWith("@T"))?[2..].Trim();
        var shown = texts.Where(t => t.Time <= firstNote).OrderBy(t => t.Time)
                         .Select(t => t.Text.StartsWith('@') && t.Text.Length > 2 ? t.Text[2..].Trim() : t.Text)
                         .Where(t => t.Length > 0 && t != title).Distinct().Take(10).ToList();
        return (string.IsNullOrEmpty(title) ? null : title, copyright, shown);
    }

    public static string FormatTime(TimeSpan t) => t.ToString(@"mm\:ss");

    // Signed number: "+5", "0", "-5".
    public static string FormatSigned(int value) => value > 0 ? $"+{value}" : value.ToString();
}
