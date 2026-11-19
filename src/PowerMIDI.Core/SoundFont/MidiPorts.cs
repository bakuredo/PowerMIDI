using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace PowerMIDI.Core;

// An event of a track on another MIDI port than the first (port 1: the channels 17-32 of the SoundFont engine): a channel
// event, or a GS SysEx of a part of that port (see MidiPorts.GsPart).
public readonly record struct PortEvent(long Tick, int Port, MidiEvent Event);

// Files with more than 16 channels: tracks assigned to MIDI ports, by the MIDI Port meta event (FF 21; e.g. a module
// tracker song of 32 channels, converted to MIDI on two ports) or by the Device Name meta event (FF 09: the output a
// sequencer gave each track). On a 16-channel output they all land on the same 16 channels and collide (programs,
// volumes, pitch bends of two tracks on one channel). With the SoundFont engine, the tracks of port 1 and more are
// played on its channels above 16 (OtherPortsPlayer).
public static class MidiPorts
{
    // MIDI Port of a track: its first MIDI Port event; 0 without one. The port of a track in its file: PortsOf.
    public static int TrackPort(TrackChunk chunk) => chunk.Events.OfType<PortPrefixEvent>().FirstOrDefault()?.Port ?? 0;

    // Port of each track of a file, as a function of its track chunks (another chunk: 0). The MIDI Port events when the
    // file has any; otherwise the Device Name events (see DevicePorts).
    public static Func<TrackChunk, int> PortsOf(MidiFile midi)
    {
        var chunks = midi.GetTrackChunks().ToList();
        var ports = chunks.Any(c => c.Events.OfType<PortPrefixEvent>().Any()) ? chunks.Select(TrackPort).ToArray() : DevicePorts(chunks);
        var portOf = new Dictionary<TrackChunk, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < chunks.Count; i++) portOf[chunks[i]] = ports[i];
        return chunk => chunk != null && portOf.TryGetValue(chunk, out int port) ? port : 0;
    }

    // The tracks of a port of a file (read now: the chunks the file has now).
    public static Func<TrackChunk, bool> TracksOf(MidiFile midi, int port)
    {
        var portOf = PortsOf(midi);
        return chunk => portOf(chunk) == port;
    }

    public static bool HasOtherPorts(MidiFile midi)
    {
        var portOf = PortsOf(midi);
        return midi.GetTrackChunks().Any(c => portOf(c) > 0);
    }

    // Ports from the Device Name events (e.g. "Roland SC-8850 PART A" and "Roland SC-8850 PART B"), in a file without
    // MIDI Port events: the first one of each track (a track without one: the first port). Only when the names keep apart
    // channels that would collide (a channel used by tracks of two names), and there are at most 4 of them (the ports the
    // engine has); otherwise all the tracks on the first port, as before: a file naming the device of its tracks without
    // needing two ports, or using the meta event for something else.
    // The port of a name: its last word, when each name ends with a different one of A-D or 1-4 ("PART B", "(A)",
    // "Out 2"…); otherwise the order the names come in (after the tracks without one).
    static int[] DevicePorts(List<TrackChunk> chunks)
    {
        var ports = new int[chunks.Count];
        var names = chunks.Select(c => c.Events.OfType<DeviceNameEvent>().FirstOrDefault()?.Text?.Trim() ?? "").ToArray();
        // The names of the tracks with channel events, in their order, and the channels of each.
        var groups = new List<string>();
        var channelsOf = new Dictionary<string, HashSet<int>>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var used = chunks[i].Events.OfType<ChannelEvent>().Select(e => (int)e.Channel).ToHashSet();
            if (used.Count == 0) continue;
            if (!channelsOf.TryGetValue(names[i], out var channels)) { channelsOf[names[i]] = channels = new(); groups.Add(names[i]); }
            channels.UnionWith(used);
        }
        bool collide = groups.SelectMany(g => channelsOf[g]).GroupBy(c => c).Any(g => g.Count() > 1);
        if (groups.Count < 2 || groups.Count > OtherPortsPlayer.Ports || !collide) return ports;
        var named = groups.Where(g => g != "").ToList();
        var tokens = named.Select(PortToken).ToList();
        var portOf = new Dictionary<string, int>();
        if (tokens.All(t => t >= 0) && tokens.Distinct().Count() == tokens.Count)
            for (int i = 0; i < named.Count; i++) portOf[named[i]] = tokens[i];
        else
            for (int i = 0; i < named.Count; i++) portOf[named[i]] = i + (groups.Contains("") ? 1 : 0);
        for (int i = 0; i < chunks.Count; i++) ports[i] = portOf.GetValueOrDefault(names[i]);
        return ports;
    }

    // Port named by the last word of a device name: A-D or 1-4 (in brackets or not); -1 otherwise.
    static int PortToken(string name)
    {
        var last = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim('(', ')', '[', ']') ?? "";
        if (last.Length != 1) return -1;
        char c = char.ToUpperInvariant(last[0]);
        return c is >= 'A' and <= 'D' ? c - 'A' : c is >= '1' and <= '4' ? c - '1' : -1;
    }

    // A GS SysEx of the parameters of a part (Roland DT1 at address 40 1x yy or 40 2x yy: Use for Rhythm Part among them)
    // received on a port: the port whose part it sets, and the message for it with address 40 (a part of the port it is
    // sent on); null for another SysEx. Address 50 sets a part of the other one of ports A and B (SC-88 Pro: from port A,
    // 50 is group B; from port B, group A); unknown from ports C and D (null, as a message without its final F7).
    public static (int Port, NormalSysExEvent Event)? GsPart(byte[] d, int port)
    {
        if (d.Length < 9 || d[0] != 0x41 || d[2] != 0x42 || d[3] != 0x12 || d[4] is not (0x40 or 0x50) || (d[5] & 0xF0) is not (0x10 or 0x20)) return null;
        var data = (byte[])d.Clone();
        if (d[4] == 0x40) return (port, new NormalSysExEvent(data));
        if (port > 1 || d[^1] != 0xF7) return null;   // (the checksum is the byte before F7)
        data[4] = 0x40;
        MidiTools.FixRolandChecksum(data);
        return (port ^ 1, new NormalSysExEvent(data));
    }

    // A GS Use for Rhythm Part received on a port (see GsPart): the port and channel it sets, and its value (0: normal
    // part, 1 / 2: drum part); null for another SysEx.
    public static (int Port, int Channel, int Value)? RhythmPart(byte[] d, int port) =>
        GsPart(d, port) is { } part && ChannelMixer.RhythmPartChannel(part.Event.Data) is int channel and >= 0
            ? (part.Port, channel, part.Event.Data[7]) : null;

    // Main: a copy of the file without the events of the other ports (their other events are kept; an empty Sequencer
    // Specific event at the time of their last event keeps the length of the file). The events of a port: the channel
    // events of its tracks, and the GS SysEx of its parts (GsPart: those of its tracks, and those of the tracks of port
    // A at address 50); the other SysEx (resets, master volume, effects…) act on the whole engine, as on a module: they
    // stay in the main file.
    // Others: the events of the other ports, with their time and port, in time order (a time: in the order of the
    // tracks, then of the file).
    public static (MidiFile Main, IReadOnlyList<PortEvent> Others) Split(MidiFile midi)
    {
        var portOf = PortsOf(midi);
        var chunks = new List<MidiChunk>();
        var others = new List<PortEvent>();
        foreach (var chunk in midi.Chunks)
        {
            if (chunk is not TrackChunk track) { chunks.Add(chunk.Clone()); continue; }
            int port = portOf(track);
            var kept = new List<TimedEvent>();
            foreach (var t in track.GetTimedEvents())
            {
                if (port > 0 && t.Event is ChannelEvent) others.Add(new PortEvent(t.Time, port, t.Event));
                else if (t.Event is SysExEvent sx && GsPart(sx.Data, port) is { } part)
                {
                    if (part.Port > 0) others.Add(new PortEvent(t.Time, part.Port, part.Event));
                    else kept.Add(new TimedEvent(part.Event, t.Time));
                }
                else kept.Add(new TimedEvent(t.Event.Clone(), t.Time));
            }
            chunks.Add(kept.ToTrackChunk());
        }
        var sorted = others.OrderBy(e => e.Tick).ToList();   // stable: track order, then file order
        if (sorted.Count > 0)
            chunks.Add(new[] { new TimedEvent(new SequencerSpecificEvent(Array.Empty<byte>()), sorted[^1].Tick) }.ToTrackChunk());
        return (new MidiFile(chunks) { TimeDivision = midi.TimeDivision }, sorted);
    }
}
