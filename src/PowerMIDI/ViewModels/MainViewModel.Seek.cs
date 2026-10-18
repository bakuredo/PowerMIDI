using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// A jump in the track (position bar, Previous, playback rebuilt after a change): the channels play as the file has them
// at the new position. The playback library only sends again the programs and controllers it follows, in its own
// order (a program can come before its bank select) and without going through the mixer (the Mixer tab would show
// those of the old position); the SysEx (drum parts, part and effect parameters) are not sent at all.
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

    // The state of the channels at a position, from the events of the track before it: read again (Mixer and Effects
    // tabs), then sent to the module: the parameter SysEx (the last one of each address: not the resets, nor the bulk
    // dumps of an MT-32), the instrument of each channel (bank select, then program), the controllers and the settings
    // of the interface. Everything goes through the mixer, as when played. from: the position before a jump back; a
    // parameter set between the two positions and not before the new one gets its value after a reset again: the reset
    // of the mode is sent first.
    void ChaseState(long tick, long from = -1)
    {
        if (playedFile == null) return;
        var std = CurrentStandard;
        RebuildFileState(playedFile, tick, std);
        if (output == null) return;
        var sysEx = new Dictionary<string, (int Order, SysExEvent Event)>();
        int order = 0;
        foreach (var t in playedFile.GetTimedEvents())
        {
            if (t.Time >= tick) break;
            if (t.Event is SysExEvent sx && ParameterKey(sx) is { } key) sysEx[key] = (order++, sx);
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
