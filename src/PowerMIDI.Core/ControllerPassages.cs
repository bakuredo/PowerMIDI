using Melanchall.DryWetMidi.Core;

namespace PowerMIDI.Core;

// The first passage of each Mixer controller of each channel (volume, pan, sends…: see ChannelMixer.ControllerNumbers),
// for the values fixed in the Mixer tab: a fixed value replaces the file's until the controller changes for the first
// time in the music, then the file's values play again (its fades, its changes). The first passage starts at the
// beginning, and again at each reset of the file; it ends at the first value of that controller (Control Change or XG
// Multi Part SysEx) after the first note of its channel. The values before that note, or at its tick, set the channel up:
// they belong to the first passage.
public sealed class ControllerPassages
{
    static readonly int Controllers = ChannelMixer.ControllerNumbers.Length;

    // First passages of each controller k of each channel c: [Start, End) in ticks, End = long.MaxValue if the
    // controller never changes after notes.
    readonly List<(long Start, long End)>[,] firsts = new List<(long, long)>[Controllers, 16];

    ControllerPassages()
    {
        for (int k = 0; k < Controllers; k++)
            for (int c = 0; c < 16; c++) firsts[k, c] = new List<(long, long)>();
    }

    // Is a tick in a first passage of controller k on channel c?
    public bool InFirst(int k, int c, long tick)
    {
        foreach (var (start, end) in firsts[k, c])
            if (tick >= start && tick < end) return true;
        return false;
    }

    // followResets: false in a forced mode (the resets of the file are ignored, see ChannelMixer.ForceMode).
    public static ControllerPassages Read(MidiFile file, bool followResets = true)
    {
        var result = new ControllerPassages();
        // All the events in the order they play (position, then track, then place in the track), as InstrumentPassages.
        var all = new List<(long Tick, int Track, int Index, MidiEvent Event)>();
        int track = 0;
        foreach (var chunk in file.GetTrackChunks())
        {
            long time = 0;
            for (int i = 0; i < chunk.Events.Count; i++)
            {
                time += chunk.Events[i].DeltaTime;
                all.Add((time, track, i, chunk.Events[i]));
            }
            track++;
        }
        all.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Track != b.Track ? a.Track.CompareTo(b.Track) : a.Index.CompareTo(b.Index));

        var start = new long[Controllers, 16];   // start of the first passage being read; -1: over
        var firstNote = new long[16];            // tick of the first note of the channel in it; -1: none yet
        Array.Fill(firstNote, -1L);

        void Change(int k, int c, long tick)
        {
            if (start[k, c] < 0 || firstNote[c] < 0 || tick <= firstNote[c]) return;
            result.firsts[k, c].Add((start[k, c], tick));
            start[k, c] = -1;
        }

        foreach (var (tick, _, _, e) in all)
        {
            switch (e)
            {
                case NoteOnEvent on when on.Velocity > 0:
                    if (firstNote[on.Channel] < 0) firstNote[on.Channel] = tick;
                    break;
                case ControlChangeEvent cc when ChannelMixer.ControllerIndex(cc.ControlNumber) is int k and >= 0:
                    Change(k, cc.Channel, tick);
                    break;
                case SysExEvent sx when followResets && MidiFileEditor.IsReset(sx):
                    for (int c = 0; c < 16; c++)
                    {
                        for (int k = 0; k < Controllers; k++)
                        {
                            if (start[k, c] >= 0 && start[k, c] < tick) result.firsts[k, c].Add((start[k, c], tick));
                            start[k, c] = tick;
                        }
                        firstNote[c] = -1;
                    }
                    break;
                case SysExEvent sx when ChannelMixer.MultiPartChannel(sx.Data) is int part and >= 0:
                    for (int pos = 6; pos < sx.Data.Length - 1; pos++)
                        if (ChannelMixer.MultiPartController(sx.Data[5] + pos - 6) is int k and >= 0) Change(k, part, tick);
                    break;
            }
        }
        for (int k = 0; k < Controllers; k++)
            for (int c = 0; c < 16; c++)
                if (start[k, c] >= 0) result.firsts[k, c].Add((start[k, c], long.MaxValue));
        return result;
    }
}
