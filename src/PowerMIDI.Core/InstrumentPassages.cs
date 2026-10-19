using Melanchall.DryWetMidi.Core;

namespace PowerMIDI.Core;

// The passages of the instrument of each channel of a file, for the instruments chosen in the Mixer tab: an instrument
// is chosen for a passage, from a Program Change of the file to the next one. A Program Change starts a new passage only
// after notes of its channel (the Program Changes before the first note, or repeated before a note, belong to the same
// passage); the first passage starts at the beginning, and again at each reset of the file. A passage is named by the
// tick of its Program Change (First for the first one). The bank selects belong to the next Program Change of their
// channel, the XG Multi Part SysEx to the passage they are in.
public sealed class InstrumentPassages
{
    public const long First = -1;

    // An instrument event (Program Change, bank select, Multi Part SysEx): Passage, the one it belongs to; Before, the
    // one in effect just before it (a Program Change starting a passage: the previous one; otherwise Passage); Msb / Lsb,
    // the bank the file asks for at that Program Change.
    public readonly record struct Info(int Channel, long Passage, long Before, int Msb, int Lsb);

    readonly Dictionary<(int Track, int Index), Info> events = new();
    readonly List<(long Tick, long Passage)>[] starts = Enumerable.Range(0, 16).Select(_ => new List<(long, long)>()).ToArray();

    // Track: number of the track chunk from 0; index: place of the event in it.
    public Info? Of(int track, int index) => events.TryGetValue((track, index), out var info) ? info : null;

    // Passage in effect on a channel at a tick, the events there included (a Program Change at that tick: the passage
    // it is in).
    public long At(int channel, long tick)
    {
        var list = starts[channel];
        int low = 0, high = list.Count - 1, found = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (list[middle].Tick <= tick) { found = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return found >= 0 ? list[found].Passage : First;
    }

    // followResets: false in a forced mode (the resets of the file are ignored, see ChannelMixer.ForceMode).
    public static InstrumentPassages Read(MidiFile file, bool followResets = true)
    {
        var result = new InstrumentPassages();
        // All the events in the order they play (position, then track, then place in the track), as MidiEventList.
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

        var current = Enumerable.Repeat(First, 16).ToArray();
        var noted = new bool[16];   // notes since the start of the passage
        int[] msb = new int[16], lsb = new int[16];
        var pending = Enumerable.Range(0, 16).Select(_ => new List<(int, int)>()).ToArray();   // bank selects waiting for their Program Change
        foreach (var (tick, t, index, e) in all)
        {
            switch (e)
            {
                case NoteOnEvent on when on.Velocity > 0:
                    noted[on.Channel] = true;
                    break;
                case ControlChangeEvent cc when (int)cc.ControlNumber is 0 or 32:
                    if (cc.ControlNumber == 0) msb[cc.Channel] = cc.ControlValue; else lsb[cc.Channel] = cc.ControlValue;
                    pending[cc.Channel].Add((t, index));
                    break;
                case ProgramChangeEvent pc:
                {
                    int c = pc.Channel;
                    long before = current[c];
                    if (noted[c])
                    {
                        current[c] = tick;
                        result.starts[c].Add((tick, tick));
                        noted[c] = false;
                    }
                    var info = new Info(c, current[c], before, msb[c], lsb[c]);
                    result.events[(t, index)] = info;
                    foreach (var bank in pending[c]) result.events[bank] = info;
                    pending[c].Clear();
                    break;
                }
                case SysExEvent sx when MidiFileEditor.IsReset(sx):
                    if (!followResets) break;
                    for (int c = 0; c < 16; c++)
                    {
                        if (current[c] != First) result.starts[c].Add((tick, First));
                        current[c] = First;
                        noted[c] = false;
                        msb[c] = lsb[c] = 0;
                    }
                    if (sx.Data.Length >= 3 && sx.Data[0] == 0x43 && sx.Data[2] == 0x4C) msb[9] = 127;   // XG: drums on channel 10
                    break;
                case SysExEvent sx when ChannelMixer.MultiPartChannel(sx.Data) is int part and >= 0:
                    result.events[(t, index)] = new Info(part, current[part], current[part], msb[part], lsb[part]);
                    break;
            }
        }
        return result;
    }
}
