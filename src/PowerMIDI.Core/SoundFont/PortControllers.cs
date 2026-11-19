namespace PowerMIDI.Core;

// Controller values fixed in the Mixer on the channels of the other MIDI ports of a file (its rows B1-B16, played by
// OtherPortsPlayer), as ChannelMixer fixes those of the first port: by port, channel and controller (index k of
// ChannelMixer.ControllerNumbers), each for the first passage of its controller (see ControllerPassages) or for the whole
// track. Kept while the playback of the track is rebuilt, cleared with the other manual settings, written into the file
// by the save.
public static class PortControllers
{
    static readonly Dictionary<(int Port, int Channel, int K), (int Value, bool WholeTrack)> fixedValues = new();   // locked

    // Value fixed for a controller of a channel, or null.
    public static (int Value, bool WholeTrack)? Of(int port, int channel, int k)
    {
        lock (fixedValues) return fixedValues.TryGetValue((port, channel, k), out var x) ? x : null;
    }

    public static void Set(int port, int channel, int k, int value, bool wholeTrack)
    {
        lock (fixedValues) fixedValues[(port, channel, k)] = (value, wholeTrack);
    }

    public static void Remove(int port, int channel, int k)
    {
        lock (fixedValues) fixedValues.Remove((port, channel, k));
    }

    // All the fixed values, by port, channel and controller (save, signature of the settings).
    public static List<(int Port, int Channel, int K, int Value, bool WholeTrack)> All()
    {
        lock (fixedValues)
            return fixedValues.OrderBy(x => x.Key.Port).ThenBy(x => x.Key.Channel).ThenBy(x => x.Key.K)
                              .Select(x => (x.Key.Port, x.Key.Channel, x.Key.K, x.Value.Value, x.Value.WholeTrack)).ToList();
    }

    // Clears them all (nothing sent). Returns true if there were any.
    public static bool Clear()
    {
        lock (fixedValues)
        {
            bool had = fixedValues.Count > 0;
            fixedValues.Clear();
            return had;
        }
    }
}
