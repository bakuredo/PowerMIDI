using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace PowerMIDI.Core;

// A channel event of a track on another MIDI port than the first (port 1: the channels 17-32 of the SoundFont engine).
public readonly record struct PortEvent(long Tick, int Port, MidiEvent Event);

// Files with more than 16 channels: tracks assigned to MIDI ports by the MIDI Port meta event (FF 21; e.g. a module
// tracker song of 32 channels, converted to MIDI on two ports). On a 16-channel output they all land on the same 16
// channels and collide (programs, volumes, pitch bends of two tracks on one channel). With the SoundFont engine, the
// tracks of port 1 and more are played on its channels above 16 (OtherPortsPlayer).
public static class MidiPorts
{
    // Port of a track: its first MIDI Port event; 0 without one.
    public static int TrackPort(TrackChunk chunk) => chunk.Events.OfType<PortPrefixEvent>().FirstOrDefault()?.Port ?? 0;

    public static bool HasOtherPorts(MidiFile midi) => midi.GetTrackChunks().Any(c => TrackPort(c) > 0);

    // Main: a copy of the file without the channel events of the tracks of the other ports (their other events are
    // kept; an empty Sequencer Specific event at the time of their last channel event keeps the length of the file).
    // Others: those channel events, with their time and port, in time order (a time: in the order of the tracks, then
    // of the file).
    public static (MidiFile Main, IReadOnlyList<PortEvent> Others) Split(MidiFile midi)
    {
        var chunks = new List<MidiChunk>();
        var others = new List<PortEvent>();
        foreach (var chunk in midi.Chunks)
        {
            int port = chunk is TrackChunk t0 ? TrackPort(t0) : 0;
            if (chunk is not TrackChunk track || port <= 0) { chunks.Add(chunk.Clone()); continue; }
            var timed = track.GetTimedEvents().ToList();
            others.AddRange(timed.Where(t => t.Event is ChannelEvent).Select(t => new PortEvent(t.Time, port, t.Event)));
            chunks.Add(timed.Where(t => t.Event is not ChannelEvent).Select(t => new TimedEvent(t.Event.Clone(), t.Time)).ToTrackChunk());
        }
        var sorted = others.OrderBy(e => e.Tick).ToList();   // stable: track order, then file order
        if (sorted.Count > 0)
            chunks.Add(new[] { new TimedEvent(new SequencerSpecificEvent(Array.Empty<byte>()), sorted[^1].Tick) }.ToTrackChunk());
        return (new MidiFile(chunks) { TimeDivision = midi.TimeDivision }, sorted);
    }
}
