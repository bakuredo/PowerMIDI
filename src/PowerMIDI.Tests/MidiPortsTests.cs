using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// Files on several MIDI ports (MidiPorts) and the player of the other ports (OtherPortsPlayer), which follows the
// playback of the first one.
public class MidiPortsTests
{
    static ChannelEvent On(int channel, int key, int velocity = 100) => new NoteOnEvent((SevenBitNumber)key, (SevenBitNumber)velocity) { Channel = (FourBitNumber)channel };
    static ChannelEvent Off(int channel, int key) => new NoteOffEvent((SevenBitNumber)key, SevenBitNumber.MinValue) { Channel = (FourBitNumber)channel };
    static ChannelEvent Cc(int channel, int number, int value) => new ControlChangeEvent((SevenBitNumber)number, (SevenBitNumber)value) { Channel = (FourBitNumber)channel };
    static ChannelEvent Pc(int channel, int program) => new ProgramChangeEvent((SevenBitNumber)program) { Channel = (FourBitNumber)channel };

    static TrackChunk Track(int? port, params (long Time, MidiEvent Event)[] events)
    {
        var timed = events.Select(e => new TimedEvent(e.Event, e.Time)).ToList();
        if (port is int p) timed.Insert(0, new TimedEvent(new PortPrefixEvent((byte)p), 0));
        return timed.ToTrackChunk();
    }

    // Port 0: channel 1; port 1: channel 1 too (another instrument), and a text (kept in the main file).
    static MidiFile TwoPorts() => new(
        Track(null, (0, new SetTempoEvent(500000))),
        Track(0, (0, Pc(0, 5)), (0, On(0, 60)), (96, Off(0, 60))),
        Track(1, (0, Pc(0, 40)), (0, new TextEvent("port B")), (48, On(0, 72)), (480, Off(0, 72))))
    { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) };

    [Fact]
    public void Split_moves_channel_events_of_other_ports()
    {
        var midi = TwoPorts();
        Assert.True(MidiPorts.HasOtherPorts(midi));
        var (main, others) = MidiPorts.Split(midi);

        // The main file: no channel event of port 1 left; its text kept; its length kept (last event at 480).
        Assert.Equal(new[] { 5 }, main.GetTimedEvents().Select(t => t.Event).OfType<ProgramChangeEvent>().Select(p => (int)p.ProgramNumber));
        Assert.Contains(main.GetTimedEvents(), t => t.Event is TextEvent { Text: "port B" });
        Assert.Equal(480, main.GetTimedEvents().Max(t => t.Time));

        // The others: in time order, with their port.
        Assert.Equal(new long[] { 0, 48, 480 }, others.Select(e => e.Tick));
        Assert.All(others, e => Assert.Equal(1, e.Port));
        Assert.IsType<ProgramChangeEvent>(others[0].Event);
        Assert.False(MidiPorts.HasOtherPorts(main.Clone()) && main.GetTrackChunks().Any(c => c.Events.OfType<ChannelEvent>().Any() && MidiPorts.TrackPort(c) > 0));
    }

    // A playback position set by the test; the events sent, recorded. The speed given is high: moving the position
    // forward while running is then playing (not a jump), however fast the test moves it.
    sealed class Follow
    {
        public volatile bool Running;
        public long Tick;
        public volatile bool Gone;
        public readonly List<(MidiEvent Event, int Port)> Sent = new();
        public (bool, long, double)? Position() => Gone ? null : (Running, Interlocked.Read(ref Tick), 1000.0);
        public void Send(MidiEvent e, int port) { lock (Sent) Sent.Add((e, port)); }
        public List<string> Texts() { lock (Sent) return Sent.Select(s => $"{s.Port}:{Describe(s.Event)}").ToList(); }
        public void Clear() { lock (Sent) Sent.Clear(); }
        static string Describe(MidiEvent e) => e switch
        {
            NoteOnEvent on => $"on{on.Channel + 1}/{on.NoteNumber}",
            NoteOffEvent off => $"off{off.Channel + 1}/{off.NoteNumber}",
            ProgramChangeEvent pc => $"pc{pc.Channel + 1}/{pc.ProgramNumber}",
            ControlChangeEvent cc => $"cc{cc.Channel + 1}/{cc.ControlNumber}={cc.ControlValue}",
            _ => e.EventType.ToString(),
        };
    }

    static void Wait(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.True(condition());
    }

    [Fact]
    public void Player_follows_playback_releases_on_pause_and_restores_state_after_jump()
    {
        var midi = TwoPorts();
        var (main, others) = MidiPorts.Split(midi);
        var follow = new Follow();
        int transpose = 0;
        using var player = new OtherPortsPlayer(others, main.GetTempoMap(), follow.Position, follow.Send, () => transpose);
        Thread.Sleep(100);   // its first look: stopped at the start

        // Playing up to tick 60: the program and the note of port 1.
        follow.Running = true;
        Interlocked.Exchange(ref follow.Tick, 60);
        Wait(() => follow.Texts().Contains("1:on1/72"));
        Assert.Equal(new[] { "1:pc1/40", "1:on1/72" }, follow.Texts());

        // Paused: sustain off and all notes off.
        follow.Running = false;
        Wait(() => follow.Texts().Contains("1:cc1/123=0"));

        // Moved back to tick 10 while paused: sounds cut, then the state there (the program) sent again.
        follow.Clear();
        Interlocked.Exchange(ref follow.Tick, 10);
        Wait(() => follow.Texts().Contains("1:pc1/40"));
        Assert.Equal(new[] { "1:cc1/64=0", "1:cc1/120=0", "1:pc1/40" }, follow.Texts());

        // Transposed by +2 from now: the note sent 2 semitones up, and its Note Off to the same key even if the
        // transposition changes meanwhile.
        follow.Clear();
        transpose = 2;
        follow.Running = true;
        Interlocked.Exchange(ref follow.Tick, 60);
        Wait(() => follow.Texts().Contains("1:on1/74"));
        transpose = 0;
        Interlocked.Exchange(ref follow.Tick, 500);
        Wait(() => follow.Texts().Contains("1:off1/74"));

        // The playback gone: the player ends, its sounds cut.
        follow.Clear();
        follow.Gone = true;
        Wait(() => follow.Texts().Contains("1:cc1/120=0"));
    }
}
