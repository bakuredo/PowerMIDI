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

        // The state the Mixer shows: program, used (program or note), last note; an unused channel.
        Assert.Equal(40, player.Program(1, 0));
        Assert.True(player.IsUsed(1, 0));
        Assert.True(player.LastNoteOn(1, 0) > 0);
        Assert.False(player.IsUsed(1, 1));
        Assert.Equal(-1, player.Control(1, 0, 7));
        // Muted: its volume to 0; unmuted: back to 100 (the file has not set it).
        player.SetMuted(1, 0, true);
        Assert.True(player.IsMuted(1, 0));
        player.SetMuted(1, 0, false);
        Assert.Equal(new[] { "1:cc1/7=0", "1:cc1/7=100" }, follow.Texts().Skip(2));

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

    static Dictionary<int, InstrumentPassages> PortPassages(MidiFile midi) =>
        new() { [1] = InstrumentPassages.Read(midi, true, chunk => MidiPorts.TrackPort(chunk) == 1) };

    [Fact]
    public void Player_plays_instrument_chosen_for_passage_instead_of_file()
    {
        var midi = TwoPorts();
        var (main, others) = MidiPorts.Split(midi);
        PortInstruments.Clear();
        try
        {
            // Chosen for the first passage of channel B1 before the track plays (a playback rebuilt keeps it).
            PortInstruments.Set(1, 0, InstrumentPassages.First, 3, 0, 60);
            var follow = new Follow();
            using var player = new OtherPortsPlayer(others, main.GetTempoMap(), follow.Position, follow.Send, () => 0, PortPassages(midi));
            Thread.Sleep(100);
            // At the start: the choice sent; playing: the file's Program Change replaced by it, with its bank.
            Assert.Equal(new[] { "1:cc1/0=3", "1:cc1/32=0", "1:pc1/60" }, follow.Texts());
            follow.Running = true;
            Interlocked.Exchange(ref follow.Tick, 60);
            Wait(() => follow.Texts().Contains("1:on1/72"));
            Assert.Equal(new[] { "1:cc1/0=3", "1:cc1/32=0", "1:pc1/60", "1:on1/72" }, follow.Texts().Skip(3));
            // The file's instrument is still known (the Mixer shows it in the tooltip), in the first passage.
            Assert.Equal(40, player.Program(1, 0));
            Assert.Equal(InstrumentPassages.First, player.Passage(1, 0));

            // Back to the file's instrument; another one chosen while playing.
            follow.Clear();
            player.ClearInstrument(1, 0, false);
            Assert.Null(PortInstruments.Of(1, 0, InstrumentPassages.First));
            player.ChooseInstrument(1, 0, 0, 0, 7);
            Assert.Equal(new[] { "1:cc1/0=0", "1:cc1/32=0", "1:pc1/40", "1:cc1/0=0", "1:cc1/32=0", "1:pc1/7" }, follow.Texts());
            Assert.Equal((0, 0, 7), PortInstruments.Of(1, 0, InstrumentPassages.First));
        }
        finally { PortInstruments.Clear(); }
    }

    [Fact]
    public void Save_writes_settings_of_each_port_in_its_tracks()
    {
        var midi = TwoPorts();
        int controllers = ChannelMixer.ControllerNumbers.Length;
        var noCc = Enumerable.Range(0, controllers).Select(_ => new bool[16]).ToArray();
        var noValues = Enumerable.Range(0, controllers).Select(_ => new int[16]).ToArray();
        var noRhythm = Enumerable.Repeat(-1, 16).ToArray();
        // Channel A1, then channel B1: each in the tracks of its port only (both are channel 1 of the file).
        MidiFileEditor.ApplyChanges(midi, new[] { (0, InstrumentPassages.First, 0, 0, 10) }, noCc, noValues, noRhythm, true, null,
                                    chunk => MidiPorts.TrackPort(chunk) == 0, "A");
        var report = MidiFileEditor.ApplyChanges(midi, new[] { (0, InstrumentPassages.First, 0, 0, 20) }, noCc, noValues, noRhythm, true, null,
                                                 chunk => MidiPorts.TrackPort(chunk) == 1, "B", changeSysEx: false);
        Assert.StartsWith("channel B1: instrument", report);
        var programs = midi.GetTrackChunks().Select(c => c.Events.OfType<ProgramChangeEvent>().Select(p => (int)p.ProgramNumber).ToList()).ToList();
        Assert.Equal(new[] { 10 }, programs[1]);   // port 0
        Assert.Equal(new[] { 20 }, programs[2]);   // port 1
        Assert.Equal(1, MidiPorts.TrackPort(midi.GetTrackChunks().ElementAt(2)));   // its port kept
    }
}
