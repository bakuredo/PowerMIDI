namespace PowerMIDI.Core;

// Instruments chosen in the Mixer for the channels of the other MIDI ports of a file (its rows B1-B16, played by
// OtherPortsPlayer), by passage (see InstrumentPassages), as ChannelMixer keeps those of the first port: kept while the
// playback of the track is rebuilt, cleared with the other manual settings, written into the file by the save.
public static class PortInstruments
{
    static readonly Dictionary<(int Port, int Channel, long Passage), (int Msb, int Lsb, int Program)> chosen = new();   // locked

    // Instrument chosen for a passage of a channel, or null.
    public static (int Msb, int Lsb, int Program)? Of(int port, int channel, long passage)
    {
        lock (chosen) return chosen.TryGetValue((port, channel, passage), out var x) ? x : null;
    }

    public static void Set(int port, int channel, long passage, int msb, int lsb, int program)
    {
        lock (chosen) chosen[(port, channel, passage)] = (msb, lsb, program);
    }

    // The choice of a passage of a channel removed, or those of all its passages.
    public static void Remove(int port, int channel, long passage, bool allPassages)
    {
        lock (chosen)
            foreach (var key in chosen.Keys.Where(k => k.Port == port && k.Channel == channel && (allPassages || k.Passage == passage)).ToList())
                chosen.Remove(key);
    }

    // Passages of a channel with an instrument chosen, other than the one given.
    public static int OtherPassages(int port, int channel, long passage)
    {
        lock (chosen) return chosen.Keys.Count(k => k.Port == port && k.Channel == channel && k.Passage != passage);
    }

    // All the choices, by port, channel and passage (save, signature of the settings).
    public static List<(int Port, int Channel, long Passage, int Msb, int Lsb, int Program)> All()
    {
        lock (chosen)
            return chosen.OrderBy(x => x.Key.Port).ThenBy(x => x.Key.Channel).ThenBy(x => x.Key.Passage)
                         .Select(x => (x.Key.Port, x.Key.Channel, x.Key.Passage, x.Value.Msb, x.Value.Lsb, x.Value.Program)).ToList();
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
