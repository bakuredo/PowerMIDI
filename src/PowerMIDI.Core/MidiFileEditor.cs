using System.Collections.Generic;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace PowerMIDI.Core;

// Writes the settings chosen in the interface into a MIDI file:
// - instrument of a channel: all its bank selects (CC0, CC32) and Program Changes are removed;
// - fixed controller of a channel (volume, pan, sends…): all its events of that controller are removed;
// - drum part of a channel (GS): its Use for Rhythm Part messages are removed;
// - XG Multi Part SysEx that write one of these values: corrected in place (they may write other values too);
// then the chosen value is inserted once, right after the reset at the start (ApplyChanges);
// - forced mode: the resets of the file are replaced by the reset of the mode (ForceReset);
// - fixed effects: the effect SysEx of the file are removed or corrected, and the fixed values inserted after the
//   reset (ApplyEffects for GS / XG, WriteMt32Reverb, WriteGm2Effects, WriteXgDrumSetup, WriteXgPartParams, WriteGsDrumMap, WriteGsPartParams).
public static class MidiFileEditor
{
    // GS Reset, XG System On, GM / GM2 On or MT-32 Reset.
    public static bool IsReset(MidiEvent e)
    {
        var sysEx = e as SysExEvent;
        if (sysEx == null) return false;
        var d = sysEx.Data;
        if (d.Length >= 7 && d[0] == 0x41 && d[2] == 0x42 && d[3] == 0x12 && d[4] == 0x40 && d[5] == 0x00 && d[6] == 0x7F) return true;  // GS Reset
        if (d.Length >= 6 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10 && d[2] == 0x4C && d[3] == 0 && d[4] == 0 && d[5] == 0x7E) return true; // XG System On
        if (d.Length >= 4 && d[0] == 0x7E && d[2] == 0x09 && (d[3] == 0x01 || d[3] == 0x03)) return true;                                  // GM / GM2 On
        if (d.Length >= 7 && d[0] == 0x41 && d[2] == 0x16 && d[3] == 0x12 && d[4] == 0x7F && d[5] == 0 && d[6] == 0) return true;         // MT-32 Reset
        return false;
    }

    static bool IsInstrumentEvent(MidiEvent e, bool[] apply)
    {
        var pc = e as ProgramChangeEvent;
        if (pc != null) return apply[pc.Channel];
        var cc = e as ControlChangeEvent;
        return cc != null && apply[cc.Channel] && (cc.ControlNumber == 0 || cc.ControlNumber == 32);
    }

    // Controller index (see ChannelMixer.ControllerNumbers) if event must be replaced, otherwise -1.
    static int ControllerToReplace(MidiEvent e, bool[][] apply)
    {
        var cc = e as ControlChangeEvent;
        if (cc == null) return -1;
        // The portamento switch goes with a fixed Portamento Time (on, off at 0: see ChannelMixer.PortamentoSwitchValue).
        if (cc.ControlNumber == ChannelMixer.PortamentoSwitch && apply[ChannelMixer.PortamentoTime][cc.Channel]) return ChannelMixer.PortamentoTime;
        int k = ChannelMixer.ControllerIndex(cc.ControlNumber);
        return k >= 0 && apply[k][cc.Channel] ? k : -1;
    }

    class Item
    {
        public TimedEvent Event;
        public int Priority;   // at equal time: 0 = reset, 1 = inserted instrument, 2 = everything else
        public int Index;      // original order
    }

    // applyCc[k][channel] / ccValues[k][channel]: fixed values for each mixer controller k.
    // rhythm[channel]: drum part chosen in the interface (GS Use for Rhythm Part: 0 = normal, 1 / 2 = drum part),
    // -1 = the file's. The file's Use for Rhythm Part messages of such a channel are replaced too.
    public static string ApplyChanges(MidiFile file, bool[] apply, int[] msb, int[] lsb, int[] program,
                                      bool[][] applyCc, int[][] ccValues, int[] rhythm)
    {
        int controllers = ChannelMixer.ControllerNumbers.Length;
        int tpqn = 480;
        var division = file.TimeDivision as TicksPerQuarterNoteTimeDivision;
        if (division != null) tpqn = division.TicksPerQuarterNote;

        var chunkIndexes = new List<int>();
        for (int i = 0; i < file.Chunks.Count; i++)
            if (file.Chunks[i] is TrackChunk) chunkIndexes.Add(i);

        // 1. Scan: last reset, first note and home track of each channel.
        long resetTime = -1;
        var firstNote = new long[16];
        var host = new int[16];
        var timedByChunk = new List<List<TimedEvent>>();
        for (int c = 0; c < 16; c++) { firstNote[c] = -1; host[c] = -1; }
        for (int k = 0; k < chunkIndexes.Count; k++)
        {
            var list = new List<TimedEvent>(((TrackChunk)file.Chunks[chunkIndexes[k]]).GetTimedEvents());
            timedByChunk.Add(list);
            foreach (var t in list)
            {
                if (IsReset(t.Event) && t.Time > resetTime) resetTime = t.Time;
                var ce = t.Event as ChannelEvent;
                if (ce == null) continue;
                int ch = ce.Channel;
                if (host[ch] < 0) host[ch] = k;
                var on = t.Event as NoteOnEvent;
                if (on != null && on.Velocity > 0 && (firstNote[ch] < 0 || t.Time < firstNote[ch])) firstNote[ch] = t.Time;
            }
        }
        if (chunkIndexes.Count == 0) return "No track in file.";

        // 2. Rebuild each track.
        var removed = new int[16];
        var removedRhythm = new int[16];
        var removedCc = new int[controllers][];
        for (int c = 0; c < controllers; c++) removedCc[c] = new int[16];
        for (int k = 0; k < chunkIndexes.Count; k++)
        {
            var items = new List<Item>();
            foreach (var t in timedByChunk[k])
            {
                if (IsInstrumentEvent(t.Event, apply))
                {
                    removed[((ChannelEvent)t.Event).Channel]++;
                    continue;
                }
                int cck = ControllerToReplace(t.Event, applyCc);
                if (cck >= 0)
                {
                    removedCc[cck][((ChannelEvent)t.Event).Channel]++;
                    continue;
                }
                var sysEx = t.Event as SysExEvent;
                int rhythmChannel = sysEx != null ? ChannelMixer.RhythmPartChannel(sysEx.Data) : -1;
                if (rhythmChannel >= 0 && rhythm[rhythmChannel] >= 0)
                {
                    removedRhythm[rhythmChannel]++;
                    continue;
                }
                // XG Multi Part SysEx: the values it writes for a fixed instrument or controller are corrected.
                int part = sysEx != null ? ChannelMixer.MultiPartChannel(sysEx.Data) : -1;
                if (part >= 0)
                {
                    var d = sysEx.Data;
                    var data = ChannelMixer.ReplaceMultiPart(d, apply[part] ? (msb[part], lsb[part], program[part]) : null,
                                                             c => applyCc[c][part] ? ccValues[c][part] : -1);
                    if (data != null)
                    {
                        for (int pos = 6; pos < d.Length - 1; pos++)
                        {
                            if (data[pos] == d[pos]) continue;
                            int cc = ChannelMixer.MultiPartController(d[5] + pos - 6);
                            if (cc >= 0) removedCc[cc][part]++; else removed[part]++;
                        }
                        items.Add(new Item { Event = new TimedEvent(new NormalSysExEvent(data), t.Time), Priority = 2, Index = items.Count });
                        continue;
                    }
                }
                items.Add(new Item { Event = t, Priority = IsReset(t.Event) ? 0 : 2, Index = items.Count });
            }

            for (int ch = 0; ch < 16; ch++)
            {
                bool anyCc = false;
                for (int c = 0; c < controllers; c++) anyCc |= applyCc[c][ch];
                if ((!apply[ch] && !anyCc && rhythm[ch] < 0) || Math.Max(host[ch], 0) != k) continue;
                // Right after reset (1/8 of a quarter note later), but never after first note.
                long time = resetTime < 0 ? 0 : resetTime + tpqn / 8;
                if (firstNote[ch] >= 0 && time > firstNote[ch]) time = Math.Max(firstNote[ch], Math.Max(resetTime, 0));
                var events = new List<MidiEvent>();
                // Drum part first: the module then reads the program as a drum kit (or as a sound).
                if (rhythm[ch] >= 0) events.Add(ChannelMixer.RhythmPartEvent(ch, rhythm[ch]));
                if (apply[ch])
                {
                    events.Add(new ControlChangeEvent((SevenBitNumber)(byte)0, (SevenBitNumber)(byte)msb[ch]) { Channel = (FourBitNumber)(byte)ch });
                    events.Add(new ControlChangeEvent((SevenBitNumber)(byte)32, (SevenBitNumber)(byte)lsb[ch]) { Channel = (FourBitNumber)(byte)ch });
                    events.Add(new ProgramChangeEvent((SevenBitNumber)(byte)program[ch]) { Channel = (FourBitNumber)(byte)ch });
                }
                for (int c = 0; c < controllers; c++)
                    if (applyCc[c][ch])
                        events.Add(new ControlChangeEvent((SevenBitNumber)(byte)ChannelMixer.ControllerNumbers[c],
                                                          (SevenBitNumber)(byte)ccValues[c][ch]) { Channel = (FourBitNumber)(byte)ch });
                if (applyCc[ChannelMixer.PortamentoTime][ch])
                    events.Add(new ControlChangeEvent((SevenBitNumber)ChannelMixer.PortamentoSwitch, (SevenBitNumber)ChannelMixer.PortamentoSwitchFor(ccValues[ChannelMixer.PortamentoTime][ch])) { Channel = (FourBitNumber)(byte)ch });
                foreach (var e in events)
                    items.Add(new Item { Event = new TimedEvent(e, time), Priority = 1, Index = items.Count });
            }

            // Stable sort: time, then priority, then original order.
            items.Sort((a, b) =>
            {
                int r = a.Event.Time.CompareTo(b.Event.Time);
                if (r != 0) return r;
                r = a.Priority.CompareTo(b.Priority);
                return r != 0 ? r : a.Index.CompareTo(b.Index);
            });

            // Rebuild delta times by hand (guaranteed order, even at equal time).
            var chunk = new TrackChunk();
            long previous = 0;
            foreach (var item in items)
            {
                var e = item.Event.Event;
                e.DeltaTime = item.Event.Time - previous;
                previous = item.Event.Time;
                chunk.Events.Add(e);
            }
            file.Chunks[chunkIndexes[k]] = chunk;
        }

        var report = new List<string>();
        for (int ch = 0; ch < 16; ch++)
        {
            var parts = new List<string>();
            if (rhythm[ch] >= 0)
                parts.Add(string.Format("{0} ({1} Use for Rhythm Part replaced)",
                                        rhythm[ch] == 0 ? "normal part" : "drum part " + rhythm[ch], removedRhythm[ch]));
            if (apply[ch]) parts.Add(string.Format("instrument ({0} bank/program event(s) or SysEx value(s) replaced)", removed[ch]));
            for (int c = 0; c < controllers; c++)
                if (applyCc[c][ch])
                    parts.Add(string.Format("{0} {1} ({2} CC{3} or SysEx value(s) replaced)", ChannelMixer.ControllerNames[c],
                                            ccValues[c][ch], removedCc[c][ch], ChannelMixer.ControllerNumbers[c]));
            if (parts.Count > 0) report.Add(string.Format("channel {0}: {1}", ch + 1, string.Join(", ", parts.ToArray())));
        }
        return string.Join("\n", report.ToArray());
    }

    // Forced mode: removes all file's resets (GS Reset, XG System On, GM / GM2 On, MT-32 Reset) and inserts
    // resetEvents instead (chosen mode's reset).
    // Events are placed at time of first original reset; if file had none, whole
    // track is shifted by one quarter note (except meta events at very start: tempo, time signature, name)
    // to give module time to process reset.
    // SysEx of another mode to remove: manufacturer in list (41 Roland, 43 Yamaha), except SC-55
    // screen display messages (41 dev 45 …), which do not set any mode.
    public static bool IsOtherModeSysEx(byte[] d, int[] manufacturers)
    {
        if (d == null || d.Length < 3 || manufacturers == null || Array.IndexOf(manufacturers, (int)d[0]) < 0) return false;
        return !(d[0] == 0x41 && d[2] == 0x45);
    }

    // dropManufacturers: SysEx of other modes to remove as well ("Clean" option), see IsOtherModeSysEx.
    public static string ForceReset(MidiFile file, MidiEvent[] resetEvents, int[] dropManufacturers)
    {
        int tpqn = 480;
        var division = file.TimeDivision as TicksPerQuarterNoteTimeDivision;
        if (division != null) tpqn = division.TicksPerQuarterNote;

        var chunkIndexes = new List<int>();
        for (int i = 0; i < file.Chunks.Count; i++)
            if (file.Chunks[i] is TrackChunk) chunkIndexes.Add(i);
        if (chunkIndexes.Count == 0) return "";

        long firstReset = -1; int resetChunk = 0;
        var timedByChunk = new List<List<TimedEvent>>();
        for (int k = 0; k < chunkIndexes.Count; k++)
        {
            var list = new List<TimedEvent>(((TrackChunk)file.Chunks[chunkIndexes[k]]).GetTimedEvents());
            timedByChunk.Add(list);
            foreach (var t in list)
                if (IsReset(t.Event) && (firstReset < 0 || t.Time < firstReset)) { firstReset = t.Time; resetChunk = k; }
        }
        long insertTime = firstReset < 0 ? 0 : firstReset;
        long shift = firstReset < 0 ? tpqn : 0;

        int removed = 0, cleaned = 0;
        for (int k = 0; k < chunkIndexes.Count; k++)
        {
            var items = new List<Item>();
            foreach (var t in timedByChunk[k])
            {
                if (IsReset(t.Event)) { removed++; continue; }
                var sx = t.Event as SysExEvent;
                if (sx != null && IsOtherModeSysEx(sx.Data, dropManufacturers)) { cleaned++; continue; }
                long time = t.Time;
                if (shift > 0 && !(time == 0 && t.Event is MetaEvent)) time += shift;
                items.Add(new Item { Event = new TimedEvent(t.Event, time), Priority = 2, Index = items.Count });
            }
            if (k == resetChunk)
            {
                foreach (var e in resetEvents)
                    items.Add(new Item { Event = new TimedEvent(e, insertTime), Priority = 0, Index = items.Count });
            }
            items.Sort((a, b) =>
            {
                int r = a.Event.Time.CompareTo(b.Event.Time);
                if (r != 0) return r;
                r = a.Priority.CompareTo(b.Priority);
                return r != 0 ? r : a.Index.CompareTo(b.Index);
            });
            var chunk = new TrackChunk();
            long previous = 0;
            foreach (var item in items)
            {
                var ev = item.Event.Event;
                ev.DeltaTime = item.Event.Time - previous;
                previous = item.Event.Time;
                chunk.Events.Add(ev);
            }
            file.Chunks[chunkIndexes[k]] = chunk;
        }
        return string.Format("forced mode: {0} reset(s) removed from file{1}{2}", removed,
                             cleaned > 0 ? string.Format(", {0} SysEx of another mode removed", cleaned) : "",
                             shift > 0 ? ", track shifted by one quarter note to make room for reset" : "");
    }

    // Fixed GS / XG effects (index = GsXgEffects.Addresses): file's SysEx that write these
    // parameters are removed (or corrected if they also write other parameters), then each fixed
    // value is inserted once after reset, each block's type before its parameters.
    public static string ApplyEffects(MidiFile file, bool[] apply, int[] values)
    {
        // Fixed type of the XG insertion effect of a parameter (-1: the type is not fixed, or not an insertion parameter).
        int InsertionType(int e)
        {
            if (!GsXgEffects.IsInsertion(e)) return -1;
            int type = GsXgEffects.InsertionType(e);
            return apply[type] ? values[type] : -1;
        }
        int tpqn = 480;
        var division = file.TimeDivision as TicksPerQuarterNoteTimeDivision;
        if (division != null) tpqn = division.TicksPerQuarterNote;

        var chunkIndexes = new List<int>();
        for (int i = 0; i < file.Chunks.Count; i++)
            if (file.Chunks[i] is TrackChunk) chunkIndexes.Add(i);
        if (chunkIndexes.Count == 0) return "";

        // Track and time of last reset (otherwise: start of first track).
        long resetTime = -1; int resetChunk = 0;
        var timedByChunk = new List<List<TimedEvent>>();
        for (int k = 0; k < chunkIndexes.Count; k++)
        {
            var list = new List<TimedEvent>(((TrackChunk)file.Chunks[chunkIndexes[k]]).GetTimedEvents());
            timedByChunk.Add(list);
            foreach (var t in list)
                if (IsReset(t.Event) && t.Time > resetTime) { resetTime = t.Time; resetChunk = k; }
        }

        int removed = 0, corrected = 0, reinserted = 0;
        for (int k = 0; k < chunkIndexes.Count; k++)
        {
            var items = new List<Item>();
            foreach (var original in timedByChunk[k])
            {
                var t = original;
                var sysEx = t.Event as SysExEvent;
                var d = sysEx != null ? sysEx.Data : null;
                var macroBlocks = new List<int>();
                bool gs = false; int count = 0;
                var bytes = d != null ? GsXgEffects.ValueBytes(d, out gs, out count) : null;
                if (bytes != null)
                {
                    int fixedCount = 0;
                    var data = (byte[])d.Clone();
                    foreach (var b in bytes)
                    {
                        int e = b[1];
                        if (apply[e]) { data[b[0]] = GsXgEffects.ValueByte(e, b[2], values[e]); fixedCount++; }
                        if (GsXgEffects.IsType(e) && !macroBlocks.Contains(GsXgEffects.Block[e])) macroBlocks.Add(GsXgEffects.Block[e]);
                    }
                    if (fixedCount == count) { removed++; continue; }   // only writes fixed parameters
                    if (fixedCount > 0)
                    {
                        if (gs) MidiTools.FixRolandChecksum(data);
                        t = new TimedEvent(new NormalSysExEvent(data), t.Time);
                        corrected++;
                    }
                }
                items.Add(new Item { Event = t, Priority = IsReset(t.Event) ? 0 : 2, Index = items.Count });
                // This message changes a block's type: module will overwrite that block's parameters,
                // so fixed ones are reinserted right after it.
                foreach (int block in macroBlocks)
                    for (int e = 0; e < apply.Length; e++)
                        if (apply[e] && GsXgEffects.Block[e] == block && !GsXgEffects.IsType(e))
                        {
                            items.Add(new Item { Event = new TimedEvent(GsXgEffects.ParamEvent(e, values[e], InsertionType(e)), t.Time),
                                                 Priority = 2, Index = items.Count });
                            reinserted++;
                        }
            }

            if (k == resetChunk)
            {
                long time = resetTime < 0 ? 0 : resetTime + tpqn / 8;
                for (int block = 0; block < GsXgEffects.Blocks; block++)
                    for (int pass = 0; pass < 2; pass++)          // type first, then parameters
                        for (int e = 0; e < apply.Length; e++)
                            if (apply[e] && GsXgEffects.Block[e] == block && GsXgEffects.IsType(e) == (pass == 0))
                                items.Add(new Item { Event = new TimedEvent(GsXgEffects.ParamEvent(e, values[e], InsertionType(e)), time),
                                                     Priority = 1, Index = items.Count });
            }

            items.Sort((a, b) =>
            {
                int r = a.Event.Time.CompareTo(b.Event.Time);
                if (r != 0) return r;
                r = a.Priority.CompareTo(b.Priority);
                return r != 0 ? r : a.Index.CompareTo(b.Index);
            });
            var chunk = new TrackChunk();
            long previous = 0;
            foreach (var item in items)
            {
                var ev = item.Event.Event;
                ev.DeltaTime = item.Event.Time - previous;
                previous = item.Event.Time;
                chunk.Events.Add(ev);
            }
            file.Chunks[chunkIndexes[k]] = chunk;
        }

        int inserted = 0;
        foreach (bool a in apply) if (a) inserted++;
        string text = string.Format("effects: {0} parameter(s) written, {1} effect SysEx removed from file, {2} corrected",
                                    inserted, removed, corrected);
        if (reinserted > 0) text += string.Format(", {0} reinserted after a type change", reinserted);
        return text;
    }

    // MT-32 mode: writes the MT-32 reverb values fixed in the interface (Mt32Effects), as MT-32 SysEx:
    // - right after the reset: one message per fixed value, so that it applies even if the file sets nothing;
    // - the file's own MT-32 SysEx that write a fixed parameter are corrected (checksum recomputed).
    public static string WriteMt32Reverb(MidiFile file)
    {
        var values = new int[Mt32Effects.Count];
        var channels = new int[9];
        var (written, corrected) = WriteFixedSysEx(file,
            (d, positions) => Mt32Effects.ApplyBytes(d, values, channels, positions) != Mt32Effects.Changes.None,
            Mt32Effects.WithFixedValues, Mt32Effects.FixedEvents);
        return string.Format("MT-32 reverb: {0} SysEx written after the reset, {1} SysEx of the file corrected", written, corrected);
    }

    // GM2 mode: same for the GM2 reverb / chorus values fixed in the interface (Gm2Effects), as GM2 SysEx.
    public static string WriteGm2Effects(MidiFile file)
    {
        var values = (int[])Gm2Effects.Defaults.Clone();
        // A type set by the file resets its block: the fixed parameters of that block are written again after it.
        IEnumerable<MidiEvent> AfterType(List<(int Position, int Param)> positions) =>
            positions.Where(x => Gm2Effects.IsType(x.Param)).Select(x => Gm2Effects.Block(x.Param)).Distinct()
                .SelectMany(block => Enumerable.Range(0, Gm2Effects.Count)
                    .Where(p => Gm2Effects.Block(p) == block && !Gm2Effects.IsType(p) && Gm2Effects.IsFixed(p))
                    .Select(p => (MidiEvent)Gm2Effects.ParamEvent(p, Gm2Effects.FixedValue(p))));
        var (written, corrected) = WriteFixedSysEx(file, (d, positions) => Gm2Effects.ApplyBytes(d, values, positions),
                                                   Gm2Effects.WithFixedValues, Gm2Effects.FixedEvents, AfterType);
        return string.Format("GM2 effects: {0} SysEx written after the reset, {1} SysEx of the file corrected", written, corrected);
    }

    // XG mode: writes the drum setup values fixed in the interface (XgDrumSetup):
    // - right after the reset, and again after each event that resets a setup with fixed values (Program Change
    //   on a part assigned to it, Drum Setup Reset);
    // - the file's drum setup SysEx and NRPN that write a fixed value are corrected;
    // - the part modes chosen in the interface: written after the reset, and the file's ones corrected.
    // The setup of each part is its last PART MODE in the file (part 10: Drum Setup 1 by default), or the one chosen.
    public static string WriteXgDrumSetup(MidiFile file)
    {
        var modes = new int[16];
        modes[9] = 2;
        foreach (var e in file.GetTrackChunks().SelectMany(c => c.Events).OfType<SysExEvent>())
        {
            var d = e.Data;
            int channel = ChannelMixer.MultiPartChannel(d);
            if (channel < 0) continue;
            for (int pos = 6; pos < d.Length - 1; pos++)
                if (d[5] + pos - 6 == 0x07) modes[channel] = d[pos];
        }
        for (int c = 0; c < 16; c++) if (XgDrumSetup.PartModeOverride[c] >= 0) modes[c] = XgDrumSetup.PartModeOverride[c];
        int SetupOf(int channel) => modes[channel] is >= 2 and <= 5 ? modes[channel] - 2 : -1;
        IEnumerable<MidiEvent> Again(int s) => s >= 0 && XgDrumSetup.IsSetupFixed(s) ? XgDrumSetup.FixedEvents(s) : Enumerable.Empty<MidiEvent>();

        var nrpn = new NrpnState();
        var (written, corrected) = WriteFixed(file, e =>
        {
            switch (e)
            {
                case SysExEvent sysEx:
                    int part = ChannelMixer.MultiPartChannel(sysEx.Data);
                    var data = part >= 0 ? ChannelMixer.ReplaceMultiPart(sysEx.Data, null, _ => -1, XgDrumSetup.PartModeOverride[part])
                                         : XgDrumSetup.WithFixedValues(sysEx.Data);
                    return (data != null ? new NormalSysExEvent(data) : null, Again(XgDrumSetup.ResetSetupOf(sysEx.Data)));
                case ProgramChangeEvent pc:
                    return (null, Again(SetupOf(pc.Channel)));
                case ControlChangeEvent cc when nrpn.Read(cc) is { } entry && XgDrumSetup.NrpnParams(cc.Channel, entry, SetupOf) is var (s, n, ps):
                    var fixedCc = XgDrumSetup.FixNrpn(s, n, ps, cc, out var again);
                    return (fixedCc, again != null ? new[] { again } : Enumerable.Empty<MidiEvent>());
            }
            return (null, Enumerable.Empty<MidiEvent>());
        }, () => XgDrumSetup.PartModeEvents().Concat(XgDrumSetup.FixedEvents()));
        return string.Format("XG drum setup: {0} SysEx written, {1} message(s) of the file corrected", written, corrected);
    }

    // GS mode: writes the drum map values fixed in the interface (GsDrumMap): right after the reset, and again after
    // each Program Change on a channel of a drum part with fixed values (the module initializes its map); the file's
    // drum map SysEx and NRPN that write a fixed value are corrected. The drum part of each channel is its last Use for
    // Rhythm Part in the file (channel 10: drum part 1 by default), or the one chosen in the Mixer.
    public static string WriteGsDrumMap(MidiFile file)
    {
        var rhythm = new int[16];
        rhythm[9] = 1;
        foreach (var e in file.GetTrackChunks().SelectMany(c => c.Events).OfType<SysExEvent>())
            if (ChannelMixer.RhythmPartChannel(e.Data) is int channel and >= 0) rhythm[channel] = e.Data[7];
        for (int c = 0; c < 16; c++) if (ChannelMixer.RhythmOverride[c] >= 0) rhythm[c] = ChannelMixer.RhythmOverride[c];
        int MapOf(int channel) => rhythm[channel] is 1 or 2 ? rhythm[channel] - 1 : -1;

        var nrpn = new NrpnState();
        var none = Enumerable.Empty<MidiEvent>();
        var (written, corrected) = WriteFixed(file, e =>
        {
            switch (e)
            {
                case SysExEvent sysEx when GsDrumMap.WithFixedValues(sysEx.Data) is { } data:
                    return (new NormalSysExEvent(data), none);
                case ProgramChangeEvent pc when MapOf(pc.Channel) is int m and >= 0 && GsDrumMap.IsMapFixed(m):
                    return (null, GsDrumMap.FixedEvents(m));
                case ControlChangeEvent cc when nrpn.Read(cc) is { } entry && GsDrumMap.NrpnParam(cc.Channel, entry, MapOf) is var (m, n, p):
                    return (GsDrumMap.FixNrpn(m, n, p, cc), none);
            }
            return (null, none);
        }, GsDrumMap.FixedEvents);
        return string.Format("GS drum map: {0} SysEx written, {1} message(s) of the file corrected", written, corrected);
    }

    // GS mode: writes the part values fixed in the interface (GsPartParams, Effects tab, Part page) as DT1 right after
    // the reset; the file's part DT1, pitch bend range / fine tuning RPN and mono / poly messages that write a fixed
    // value are corrected.
    public static string WriteGsPartParams(MidiFile file)
    {
        var nrpn = new NrpnState();
        var none = Enumerable.Empty<MidiEvent>();
        var (written, corrected) = WriteFixed(file, e => e switch
        {
            SysExEvent sysEx when GsPartParams.WithFixedValues(sysEx.Data) is { } data => (new NormalSysExEvent(data), none),
            ControlChangeEvent cc when (int)cc.ControlNumber is 126 or 127 => (GsPartParams.FixMonoPoly(cc), none),
            ControlChangeEvent cc when nrpn.Read(cc) is { } entry => (GsPartParams.FixDataEntry(cc, entry), none),
            _ => (null, none),
        }, GsPartParams.FixedEvents);
        return string.Format("GS part settings: {0} SysEx written after the reset, {1} message(s) of the file corrected", written, corrected);
    }

    // XG mode: writes the part values fixed in the interface (XgPartParams, Effects tab, Part page) as SysEx right after
    // the reset; the file's part SysEx, NRPN, pitch bend range RPN and mono / poly messages that write a fixed value are
    // corrected.
    public static string WriteXgPartParams(MidiFile file)
    {
        var nrpn = new NrpnState();
        var none = Enumerable.Empty<MidiEvent>();
        var (written, corrected) = WriteFixed(file, e =>
        {
            switch (e)
            {
                case SysExEvent sysEx when XgPartParams.WithFixedValues(sysEx.Data) is { } data:
                    return (new NormalSysExEvent(data), none);
                case ControlChangeEvent cc when (int)cc.ControlNumber is 126 or 127:
                    return (XgPartParams.FixMonoPoly(cc), none);
                case ControlChangeEvent cc when nrpn.Read(cc) is { } entry:
                    var fixedCc = XgPartParams.FixDataEntry(cc, entry, out var again);
                    return (fixedCc, again != null ? new[] { again } : none);
            }
            return (null, none);
        }, XgPartParams.FixedEvents);
        return string.Format("XG part settings: {0} SysEx written, {1} message(s) of the file corrected", written, corrected);
    }

    // Writes SysEx effect values fixed in the interface: the messages of fixedEvents right after the last reset,
    // and the file's own SysEx recognized by read (which lists the positions of their values) corrected by
    // withFixedValues (null = unchanged), followed by the messages of after (optional) if it returns any.
    // Returns the number of messages written and corrected.
    static (int Written, int Corrected) WriteFixedSysEx(MidiFile file, Func<byte[], List<(int Position, int Param)>, bool> read,
        Func<byte[], List<(int Position, int Param)>, byte[]> withFixedValues, Func<IEnumerable<MidiEvent>> fixedEvents,
        Func<List<(int Position, int Param)>, IEnumerable<MidiEvent>> after = null) =>
        WriteFixed(file, e =>
        {
            var positions = new List<(int Position, int Param)>();
            if (e is not SysExEvent sysEx || !read(sysEx.Data, positions)) return (null, Enumerable.Empty<MidiEvent>());
            var data = withFixedValues(sysEx.Data, positions);
            return (data != null ? new NormalSysExEvent(data) : null,
                    after != null && positions.Count > 0 ? after(positions) : Enumerable.Empty<MidiEvent>());
        }, fixedEvents);

    // Writes values fixed in the interface: the messages of fixedEvents right after the last reset; each event of the
    // file goes through process, which returns the message to write instead (null = unchanged) and the messages to
    // write right after it. Returns the number of messages written and corrected.
    static (int Written, int Corrected) WriteFixed(MidiFile file, Func<MidiEvent, (MidiEvent Replacement, IEnumerable<MidiEvent> After)> process,
        Func<IEnumerable<MidiEvent>> fixedEvents)
    {
        int tpqn = 480;
        var division = file.TimeDivision as TicksPerQuarterNoteTimeDivision;
        if (division != null) tpqn = division.TicksPerQuarterNote;

        var chunkIndexes = new List<int>();
        for (int i = 0; i < file.Chunks.Count; i++)
            if (file.Chunks[i] is TrackChunk) chunkIndexes.Add(i);
        if (chunkIndexes.Count == 0) return (0, 0);

        // Track and time of the last reset (otherwise: start of the first track).
        long resetTime = -1; int resetChunk = 0;
        var timedByChunk = new List<List<TimedEvent>>();
        for (int k = 0; k < chunkIndexes.Count; k++)
        {
            var list = new List<TimedEvent>(((TrackChunk)file.Chunks[chunkIndexes[k]]).GetTimedEvents());
            timedByChunk.Add(list);
            foreach (var t in list)
                if (IsReset(t.Event) && t.Time > resetTime) { resetTime = t.Time; resetChunk = k; }
        }

        int corrected = 0, written = 0;
        for (int k = 0; k < chunkIndexes.Count; k++)
        {
            var items = new List<Item>();
            foreach (var original in timedByChunk[k])
            {
                var t = original;
                var (replacement, after) = process(t.Event);
                if (replacement != null) { t = new TimedEvent(replacement, t.Time); corrected++; }
                items.Add(new Item { Event = t, Priority = IsReset(t.Event) ? 0 : 2, Index = items.Count });
                foreach (var e in after)
                {
                    items.Add(new Item { Event = new TimedEvent(e, t.Time), Priority = 2, Index = items.Count });
                    written++;
                }
            }
            if (k == resetChunk)
            {
                long time = resetTime < 0 ? 0 : resetTime + tpqn / 8;
                foreach (var e in fixedEvents())
                {
                    items.Add(new Item { Event = new TimedEvent(e, time), Priority = 1, Index = items.Count });
                    written++;
                }
            }
            items.Sort((a, b) =>
            {
                int r = a.Event.Time.CompareTo(b.Event.Time);
                if (r != 0) return r;
                r = a.Priority.CompareTo(b.Priority);
                return r != 0 ? r : a.Index.CompareTo(b.Index);
            });
            var chunk = new TrackChunk();
            long previous = 0;
            foreach (var item in items)
            {
                var ev = item.Event.Event;
                ev.DeltaTime = item.Event.Time - previous;
                previous = item.Event.Time;
                chunk.Events.Add(ev);
            }
            file.Chunks[chunkIndexes[k]] = chunk;
        }
        return (written, corrected);
    }
}
