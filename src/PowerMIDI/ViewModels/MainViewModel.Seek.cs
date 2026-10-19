using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// A jump in the track (position bar, Previous, playback rebuilt after a change): the channels play as the file has them
// at the new position. The playback library would send again the programs and controllers it follows in its own
// order (a program before its bank select), without going through the mixer (the settings in blue lost, the Mixer tab
// showing those of the old position), and no SysEx (drum parts, part and effect parameters): it only sends the pitch
// bend (see CreatePlayback), the rest is sent here.
public sealed partial class MainViewModel
{
    MidiFile playedFile;   // the track being played (with its changes), see CreatePlayback

    // Moves the playback to a time; playing or not, as it was.
    void MoveTo(ITimeSpan time)
    {
        bool running = playback.IsRunning;
        if (running) playback.Stop();   // the state is read and sent again first (no event of the track meanwhile)
        long from = CurrentTick();
        playback.MoveToTime(time);
        ChaseState(CurrentTick(), from);
        if (running) playback.Start();
    }

    // The state of the channels at a position, from the events of the track before it (since its last reset): read again
    // (Mixer and Effects tabs), then sent to the module: the parameter SysEx (the last one of each address: not the
    // resets, nor the bulk dumps of an MT-32), the instrument of each channel (bank select, then program), the other
    // controllers (their last value; mono / poly), the RPN / NRPN (the last value of each), then the controllers and the
    // settings of the interface. Everything goes through the mixer, as when played. from: the position before a jump
    // back; a parameter set between the two positions and not before the new one gets its value after a reset again: the
    // reset of the mode is sent first.
    void ChaseState(long tick, long from = -1)
    {
        if (playedFile == null) return;
        var std = CurrentStandard;
        RebuildFileState(playedFile, tick, std);
        if (output == null) return;
        var sysEx = new Dictionary<string, (int Order, SysExEvent Event)>();
        var controllers = new Dictionary<(int Channel, int Number), (int Order, int Value)>();
        var parameters = new Dictionary<(int Channel, bool Rpn, int Msb, int Lsb, bool Fine), (int Order, int Value)>();
        var selected = new NrpnState();
        int order = 0;
        foreach (var t in playedFile.GetTimedEvents())
        {
            if (t.Time >= tick) break;
            if (MidiFileEditor.IsReset(t.Event)) { sysEx.Clear(); controllers.Clear(); parameters.Clear(); selected.Reset(); continue; }
            if (t.Event is SysExEvent sx && ParameterKey(sx) is { } key) sysEx[key] = (order++, sx);
            else if (t.Event is ControlChangeEvent cc)
            {
                int c = cc.Channel, n = cc.ControlNumber;
                if (selected.Read(cc) is { } entry) parameters[(c, entry.Rpn, entry.Msb, entry.Lsb, entry.Fine)] = (order++, cc.ControlValue);
                else if (n == 121)   // Reset All Controllers: modulation, expression and pedals of the channel back to their default
                    foreach (int r in new[] { 1, 11, 64, 65, 66, 67 }) controllers.Remove((c, r));
                else if (n is 126 or 127) { controllers.Remove((c, 126)); controllers.Remove((c, 127)); controllers[(c, n)] = (order++, cc.ControlValue); }
                else if (n is not (0 or 32 or 6 or 38 or 96 or 97 or 98 or 99 or 100 or 101) && n < 120) controllers[(c, n)] = (order++, cc.ControlValue);
            }
        }
        bool reset = from > tick && playedFile.GetTimedEvents().Any(t => t.Time >= tick && t.Time < from &&
                                                                         t.Event is SysExEvent sx && ParameterKey(sx) is { } key && !sysEx.ContainsKey(key));
        if (reset && std != "MT-32") MidiTools.SendReset(output, std);   // (an MT-32 restarts completely: never)
        foreach (var (_, e) in sysEx.Values.OrderBy(x => x.Order))
            if (ChannelMixer.Callback(e.Clone(), 0, TimeSpan.Zero) is { } played) output.SendEvent(played);
        for (int c = 0; c < 16; c++)
        {
            if (!ChannelMixer.Used[c]) continue;
            var channel = (FourBitNumber)c;
            foreach (MidiEvent e in new MidiEvent[]
            {
                new ControlChangeEvent((SevenBitNumber)0, (SevenBitNumber)ChannelMixer.BankMsb[c]) { Channel = channel },
                new ControlChangeEvent((SevenBitNumber)32, (SevenBitNumber)ChannelMixer.BankLsb[c]) { Channel = channel },
                new ProgramChangeEvent((SevenBitNumber)ChannelMixer.Program[c]) { Channel = channel },
            })
                if (ChannelMixer.Callback(e, 0, TimeSpan.Zero) is { } played) output.SendEvent(played);
        }
        var events = new List<MidiEvent>();
        foreach (var ((c, n), _) in controllers.OrderBy(x => x.Value.Order)) events.Add(MidiTools.Cc(c, n, controllers[(c, n)].Value));
        foreach (var ((c, rpn, msb, lsb, fine), (_, value)) in parameters.OrderBy(x => x.Value.Order))
        {
            events.Add(MidiTools.Cc(c, rpn ? 101 : 99, msb));
            events.Add(MidiTools.Cc(c, rpn ? 100 : 98, lsb));
            events.Add(MidiTools.Cc(c, fine ? 38 : 6, value));
        }
        foreach (int c in parameters.Keys.Select(k => k.Channel).Distinct())   // no parameter selected afterwards (RPN null)
        {
            events.Add(MidiTools.Cc(c, 101, 127));
            events.Add(MidiTools.Cc(c, 100, 127));
        }
        foreach (var e in events)
            if (ChannelMixer.Callback(e, 0, TimeSpan.Zero) is { } played) output.SendEvent(played);
        ApplyMixerState();
    }

    // Address of a parameter SysEx (header and address bytes: a later one at the same address replaces it); null for a
    // reset, a long message (bulk dump, MT-32 timbre) or an MT-32 message (the module needs time between them).
    static string ParameterKey(SysExEvent sx)
    {
        var d = sx.Data;
        if (d.Length > 24 || MidiFileEditor.IsReset(sx)) return null;
        if (d.Length >= 9 && d[0] == 0x41 && d[3] == 0x12) return d[2] == 0x16 ? null : Convert.ToHexString(d, 0, 7);   // Roland DT1
        if (d.Length >= 7 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10) return Convert.ToHexString(d, 0, 6);               // Yamaha
        if (d.Length >= 6 && d[0] == 0x7F && d[2] == 0x04) return Convert.ToHexString(d, 0, Math.Min(d.Length - 2, 6)); // universal (GM2, master)
        return null;
    }
}
