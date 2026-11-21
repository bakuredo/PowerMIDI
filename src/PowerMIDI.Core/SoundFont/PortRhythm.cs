namespace PowerMIDI.Core;

// Drum parts chosen in the Mixer for the channels of the other MIDI ports of a file (its rows B1-B16, played by
// OtherPortsPlayer), in a track of a mode whose drums are a GS drum part (Use for Rhythm Part; not XG or GM2, whose bank
// decides): 0 normal part, 1 drums, as ChannelMixer.RhythmOverride on the first port. Kept while the playback of the
// track is rebuilt, cleared with the other manual settings, written into the tracks of their port by the save.
public static class PortRhythm
{
    static readonly Dictionary<(int Port, int Channel), int> chosen = new();   // locked

    // Drum part chosen for a channel, or null.
    public static int? Of(int port, int channel)
    {
        lock (chosen) return chosen.TryGetValue((port, channel), out int x) ? x : null;
    }

    public static void Set(int port, int channel, int value)
    {
        lock (chosen) chosen[(port, channel)] = value;
    }

    public static void Remove(int port, int channel)
    {
        lock (chosen) chosen.Remove((port, channel));
    }

    // All the choices, by port and channel (save, signature of the settings).
    public static List<(int Port, int Channel, int Value)> All()
    {
        lock (chosen) return chosen.OrderBy(x => x.Key.Port).ThenBy(x => x.Key.Channel).Select(x => (x.Key.Port, x.Key.Channel, x.Value)).ToList();
    }

    // Clears them all (nothing sent). Returns true if there were any.
    public static bool Clear()
    {
        lock (chosen)
        {
            bool had = chosen.Count > 0;
            chosen.Clear();
            return had;
        }
    }
}
