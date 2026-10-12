using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace PowerMIDI.Core;

// Settings inserted at a position of the track ("Insert" button): channel events (controllers, instrument,
// drum part) and effect SysEx, written at Tick into the existing tracks.
// Channel: 0-15 for the events of a channel (written in the track that already plays it), -1 for the effect
// SysEx (written in the track of the last reset, otherwise the first track).
public sealed class MidiInsertion
{
    public long Tick { get; init; }
    public List<(int Channel, MidiEvent Event)> Events { get; init; } = new();
    public string Description { get; init; } = "";
}

public static class MidiInsertions
{
    // Start of the beat containing a tick (bar and beat of the file's time signatures).
    public static long SnapToBeat(long tick, TempoMap tempoMap)
    {
        var bbt = TimeConverter.ConvertTo<BarBeatTicksTimeSpan>(tick, tempoMap);
        return TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(bbt.Bars, bbt.Beats, 0), tempoMap);
    }

    // Writes the insertions into the file. At the same tick, an inserted event comes after the file's own
    // non-note events (so that it wins over a file value set at the same moment) and before its notes.
    // Returns the number of events written.
    public static int Apply(MidiFile file, IEnumerable<MidiInsertion> insertions)
    {
        var list = insertions.ToList();
        if (list.Count == 0) return 0;
        var chunks = file.GetTrackChunks().ToList();
        if (chunks.Count == 0) return 0;

        // Track of each channel: the first one with an event of that channel.
        var channelChunk = new int[16];
        Array.Fill(channelChunk, -1);
        for (int k = 0; k < chunks.Count; k++)
            foreach (var e in chunks[k].Events.OfType<ChannelEvent>())
                if (channelChunk[e.Channel] < 0) channelChunk[e.Channel] = k;
        // Track of the effect SysEx: the one of the last reset, otherwise the first one.
        int sysExChunk = 0;
        long resetTime = -1;
        for (int k = 0; k < chunks.Count; k++)
        {
            long time = 0;
            foreach (var e in chunks[k].Events)
            {
                time += e.DeltaTime;
                if (MidiFileEditor.IsReset(e) && time > resetTime) { resetTime = time; sysExChunk = k; }
            }
        }

        // Events to add, by track: (tick, order of insertion, event).
        var added = new Dictionary<int, List<(long Tick, int Order, MidiEvent Event)>>();
        int order = 0, count = 0;
        foreach (var insertion in list.OrderBy(i => i.Tick))
            foreach (var (channel, e) in insertion.Events)
            {
                int k = channel >= 0 && channelChunk[channel] >= 0 ? channelChunk[channel] : sysExChunk;
                if (!added.TryGetValue(k, out var target)) added[k] = target = new();
                target.Add((insertion.Tick, order++, e.Clone()));
                count++;
            }

        foreach (var (k, events) in added)
        {
            // Priority at the same tick: file's other events (note ends included) 0, inserted 1, file's note starts 2.
            var items = new List<(long Tick, int Priority, int Order, MidiEvent Event)>();
            long time = 0;
            int index = 0;
            foreach (var e in chunks[k].Events)
            {
                time += e.DeltaTime;
                items.Add((time, e is NoteOnEvent on && on.Velocity > 0 ? 2 : 0, index++, e));
            }
            foreach (var (tick, o, e) in events) items.Add((tick, 1, o, e));
            items.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick)
                               : a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Order.CompareTo(b.Order));
            var chunk = chunks[k];
            chunk.Events.Clear();
            long previous = 0;
            foreach (var item in items)
            {
                item.Event.DeltaTime = item.Tick - previous;
                previous = item.Tick;
                chunk.Events.Add(item.Event);
            }
        }
        return count;
    }
}
