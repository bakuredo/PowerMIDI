using Melanchall.DryWetMidi.Core;

namespace PowerMIDI.Core;

// Changes made in the Event Editor, on a file in memory: an event is given by its track (1-based, as in MidiEventRow)
// and its place in that track (Index). The positions of the other events do not change (their delta times are worked
// out again).
public static class MidiEventEdits
{
    static TrackChunk Track(MidiFile file, int track) => file.GetTrackChunks().ElementAt(track - 1);

    // Events of a track with their position.
    static List<(long Time, MidiEvent Event)> Timed(TrackChunk chunk)
    {
        var list = new List<(long, MidiEvent)>(chunk.Events.Count);
        long time = 0;
        foreach (var e in chunk.Events) list.Add((time += e.DeltaTime, e));
        return list;
    }

    static void Write(TrackChunk chunk, List<(long Time, MidiEvent Event)> list)
    {
        chunk.Events.Clear();
        long previous = 0;
        foreach (var (time, e) in list)
        {
            e.DeltaTime = time - previous;
            previous = time;
            chunk.Events.Add(e);
        }
    }

    // Place of a new event at a position: after the events already there.
    static int PlaceAt(List<(long Time, MidiEvent Event)> list, long time)
    {
        int i = list.FindIndex(x => x.Time > time);
        return i < 0 ? list.Count : i;
    }

    // Replaces an event (same position).
    public static void Replace(MidiFile file, int track, int index, MidiEvent e)
    {
        var chunk = Track(file, track);
        e.DeltaTime = chunk.Events[index].DeltaTime;
        chunk.Events[index] = e;
    }

    // Changes a note (Note On at onIndex, Note Off at offIndex): channel, key, velocity, length (ticks, at least 1).
    // The Note On keeps its place; the Note Off moves to the new end.
    public static void SetNote(MidiFile file, int track, int onIndex, int offIndex, int channel, int note, int velocity, long length)
    {
        var chunk = Track(file, track);
        var list = Timed(chunk);
        long start = list[onIndex].Time;
        var off = list[offIndex].Event is NoteOffEvent oldOff ? (int)oldOff.Velocity : 0;
        list[onIndex] = (start, MidiTools.NoteOn(channel, note, velocity));
        list.RemoveAt(offIndex);
        long end = start + Math.Max(1, length);
        // The Note Off before the events at its new position (as a note ending there), but after its Note On.
        int i = list.FindIndex(x => x.Time >= end);
        i = Math.Max(i < 0 ? list.Count : i, onIndex + 1);
        list.Insert(i, (end, MidiTools.NoteOff(channel, note, off)));
        Write(chunk, list);
    }

    // Deletes events (a note: give its Note On and its Note Off).
    public static void Delete(MidiFile file, IEnumerable<(int Track, int Index)> events)
    {
        foreach (var group in events.Where(x => x.Track > 0 && x.Index >= 0).GroupBy(x => x.Track))
        {
            var chunk = Track(file, group.Key);
            var list = Timed(chunk);
            foreach (int index in group.Select(x => x.Index).Distinct().OrderByDescending(x => x)) list.RemoveAt(index);
            Write(chunk, list);
        }
    }

    // Adds an event at a position of a track (after the events already there); returns its place.
    public static int Add(MidiFile file, int track, long time, MidiEvent e)
    {
        var chunk = Track(file, track);
        var list = Timed(chunk);
        int i = PlaceAt(list, time);
        list.Insert(i, (time, e));
        Write(chunk, list);
        return i;
    }

    // Adds a note; returns the place of its Note On.
    public static int AddNote(MidiFile file, int track, long time, int channel, int note, int velocity, long length)
    {
        var chunk = Track(file, track);
        var list = Timed(chunk);
        int i = PlaceAt(list, time);
        list.Insert(i, (time, MidiTools.NoteOn(channel, note, velocity)));
        long end = time + Math.Max(1, length);
        int j = list.FindIndex(i + 1, x => x.Time >= end);
        list.Insert(j < 0 ? list.Count : j, (end, MidiTools.NoteOff(channel, note, 0)));
        Write(chunk, list);
        return i;
    }
}
