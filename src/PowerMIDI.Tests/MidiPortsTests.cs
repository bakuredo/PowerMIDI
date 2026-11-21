using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// Files on several MIDI ports (MidiPorts) and the player of the other ports (OtherPortsPlayer), which follows the
// playback of the first one.
[Collection("ChannelMixer")]   // the mode of the track (ChannelMixer.XgMode) and the settings of the Mixer: shared static state
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

    // A playback position set by the test; the events sent, recorded. The speed given is very high: moving the position
    // forward while running is then playing (not a jump), however fast the test moves it (a jump forward is more than
    // twice the music the speed plays between two looks: at speed 1000, two looks 1 ms apart allowed only 2 s).
    sealed class Follow
    {
        public volatile bool Running;
        public long Tick;
        public volatile bool Gone;
        public int Looks;   // positions asked for by the player
        public readonly List<(MidiEvent Event, int Port)> Sent = new();
        public (bool, long, double)? Position()
        {
            Interlocked.Increment(ref Looks);
            return Gone ? null : (Running, Interlocked.Read(ref Tick), 1_000_000.0);
        }
        // The player has made its first look (asked again after it): the start position is behind it.
        public void WaitFirstLook() => Wait(() => Volatile.Read(ref Looks) > 1);
        public void Send(MidiEvent e, int port) { lock (Sent) Sent.Add((e, port)); }
        public List<string> Texts() { lock (Sent) return Sent.Select(s => $"{s.Port}:{Describe(s.Event)}").ToList(); }
        public void Clear() { lock (Sent) Sent.Clear(); }
        static string Describe(MidiEvent e) => e switch
        {
            NoteOnEvent on => $"on{on.Channel + 1}/{on.NoteNumber}",
            NoteOffEvent off => $"off{off.Channel + 1}/{off.NoteNumber}",
            ProgramChangeEvent pc => $"pc{pc.Channel + 1}/{pc.ProgramNumber}",
            ControlChangeEvent cc => $"cc{cc.Channel + 1}/{cc.ControlNumber}={cc.ControlValue}",
            SysExEvent sx when ChannelMixer.RhythmPartChannel(sx.Data) is int c and >= 0 => $"rhythm{c + 1}={sx.Data[7]}",
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
        follow.WaitFirstLook();   // stopped at the start

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

    static Dictionary<int, PortPassages> Passages(MidiFile midi) => new()
    {
        [1] = new PortPassages(InstrumentPassages.Read(midi, true, chunk => MidiPorts.TrackPort(chunk) == 1),
                               ControllerPassages.Read(midi, true, chunk => MidiPorts.TrackPort(chunk) == 1)),
    };

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
            using var player = new OtherPortsPlayer(others, main.GetTempoMap(), follow.Position, follow.Send, () => 0, Passages(midi));
            follow.WaitFirstLook();
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

    // Port 1: a volume at the start, a note, then a change of volume after the note (the end of its first passage).
    static MidiFile Fade() => new(
        Track(null, (0, new SetTempoEvent(500000))),
        Track(0, (0, On(0, 60)), (96, Off(0, 60))),
        Track(1, (0, Cc(0, 7, 90)), (48, On(0, 72)), (240, Cc(0, 7, 50)), (480, Off(0, 72))))
    { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) };

    [Fact]
    public void Player_sends_fixed_value_instead_of_file_until_first_change()
    {
        var midi = Fade();
        var (main, others) = MidiPorts.Split(midi);
        const int volume = ChannelMixer.Volume;
        int resets = 0;
        PortControllers.Clear();
        try
        {
            // Volume of channel B1 fixed at 30 before the track plays: sent at the start, and instead of the file's 90.
            PortControllers.Set(1, 0, volume, 30, false);
            var follow = new Follow();
            using var player = new OtherPortsPlayer(others, main.GetTempoMap(), follow.Position, follow.Send, () => 0, Passages(midi),
                                                    () => Volatile.Read(ref resets));
            follow.WaitFirstLook();
            follow.Running = true;
            Interlocked.Exchange(ref follow.Tick, 60);
            Wait(() => follow.Texts().Contains("1:on1/72"));
            Assert.Equal(new[] { "1:cc1/7=30", "1:cc1/7=30", "1:on1/72" }, follow.Texts());
            Assert.True(player.IsFixedActive(1, 0, volume));
            Assert.Equal(30, player.Value(1, 0, volume));
            Assert.Equal(90, player.FileValue(1, 0, volume));

            // The first change of the volume after the note: the file's values play again.
            follow.Clear();
            Interlocked.Exchange(ref follow.Tick, 300);
            Wait(() => follow.Texts().Contains("1:cc1/7=50"));
            Wait(() => !player.IsFixedActive(1, 0, volume));
            Assert.Equal(50, player.Value(1, 0, volume));

            // For the whole track: the fixed value again; back to the file's.
            follow.Clear();
            player.SetWholeTrack(1, 0, volume, true);
            Assert.Equal(30, player.Value(1, 0, volume));
            player.ClearController(1, 0, volume);
            Assert.Null(PortControllers.Of(1, 0, volume));
            // Fixed past the first passage: for the whole track; muted and unmuted: back to the fixed value.
            player.SetController(1, 0, volume, 20);
            Assert.Equal((20, true), PortControllers.Of(1, 0, volume));
            player.SetMuted(1, 0, true);
            player.SetMuted(1, 0, false);
            Assert.Equal(new[] { "1:cc1/7=30", "1:cc1/7=50", "1:cc1/7=20", "1:cc1/7=0", "1:cc1/7=20" }, follow.Texts());

            // A mode reset played by the engine: the fixed value sent again.
            follow.Clear();
            Interlocked.Increment(ref resets);
            Wait(() => follow.Texts().Contains("1:cc1/7=20"));
        }
        finally { PortControllers.Clear(); }
    }

    [Fact]
    public void Save_writes_fixed_values_of_other_port_in_its_tracks()
    {
        var midi = Fade();
        int controllers = ChannelMixer.ControllerNumbers.Length;
        var applyCc = Enumerable.Range(0, controllers).Select(_ => new bool[16]).ToArray();
        var ccValues = Enumerable.Range(0, controllers).Select(_ => new int[16]).ToArray();
        var wholeTrack = Enumerable.Range(0, controllers).Select(_ => new bool[16]).ToArray();
        (applyCc[ChannelMixer.Volume][0], ccValues[ChannelMixer.Volume][0]) = (true, 30);   // B1, until the first change
        MidiFileEditor.ApplyChanges(midi, Array.Empty<(int, long, int, int, int)>(), applyCc, ccValues, Enumerable.Repeat(-1, 16).ToArray(),
                                    true, wholeTrack, chunk => MidiPorts.TrackPort(chunk) == 1, "B", changeSysEx: false);
        // In the track of port 1: 30 instead of 90 at the start, the change after the note kept.
        var volumes = midi.GetTrackChunks().ElementAt(2).GetTimedEvents().Where(t => t.Event is ControlChangeEvent cc && cc.ControlNumber == 7)
                          .Select(t => (t.Time, (int)((ControlChangeEvent)t.Event).ControlValue)).ToList();
        Assert.Equal(new[] { (0L, 30), (240L, 50) }, volumes);
        Assert.DoesNotContain(midi.GetTrackChunks().ElementAt(1).Events, e => e is ControlChangeEvent);   // port 0 untouched
    }

    [Fact]
    public void Track_of_channel_among_tracks_of_a_port()
    {
        var midi = TwoPorts();   // tracks: 1 tempo, 2 port 0, 3 port 1; channel 1 in tracks 2 and 3
        Func<TrackChunk, bool> Port(int p) => chunk => MidiPorts.TrackPort(chunk) == p;
        Assert.Equal(2, MidiEventEdits.TrackOfChannel(midi, 0));
        Assert.Equal(2, MidiEventEdits.TrackOfChannel(midi, 0, Port(0)));
        Assert.Equal(3, MidiEventEdits.TrackOfChannel(midi, 0, Port(1)));
        // A channel with no event: the first track of the port.
        Assert.Equal(1, MidiEventEdits.TrackOfChannel(midi, 5, Port(0)));
        Assert.Equal(3, MidiEventEdits.TrackOfChannel(midi, 5, Port(1)));
    }

    // Port 0: program 5, a note at 96; port 1: program 40 and a volume at 0, a note at 48, a change of volume at 240.
    static MidiFile Listed() => new(
        Track(null, (0, new SetTempoEvent(500000))),
        Track(0, (0, Pc(0, 5)), (96, On(0, 60)), (192, Off(0, 60))),
        Track(1, (0, Pc(0, 40)), (0, Cc(0, 7, 90)), (48, On(0, 72)), (240, Cc(0, 7, 50)), (480, Off(0, 72))))
    { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) };

    [Fact]
    public void Event_list_by_port_follows_channels_of_each_port()
    {
        var midi = Listed();
        Func<int, int, int, int, int, string> name = (c, program, msb, lsb, rhythm) => $"P{program}";
        // On 16 channels: the note of port A named after the Program Change of port B before it (both on channel 1).
        var merged = MidiEventList.Build(midi, name);
        Assert.Equal("P40", merged.Single(r => r.Type == "Note" && r.Track == 2).Description);
        Assert.All(merged, r => Assert.Equal(0, r.Port));

        // By port: the channels of each port on their own; the rows of track 3 on port B.
        var rows = MidiEventList.Build(midi, name, byPort: true);
        Assert.Equal("P5", rows.Single(r => r.Type == "Note" && r.Track == 2).Description);
        Assert.Equal("P40", rows.Single(r => r.Type == "Note" && r.Track == 3).Description);
        Assert.All(rows.Where(r => r.Track == 2), r => Assert.Equal(0, r.Port));
        Assert.All(rows.Where(r => r.Track == 3), r => Assert.Equal(1, r.Port));

        // In blue: the rows of B1 its settings in the Mixer tab replace (instrument chosen; volume fixed until its first
        // change, at 240).
        var (main, others) = MidiPorts.Split(midi);
        PortInstruments.Clear();
        PortControllers.Clear();
        try
        {
            PortInstruments.Set(1, 0, InstrumentPassages.First, 0, 0, 50);
            PortControllers.Set(1, 0, ChannelMixer.Volume, 30, false);
            var follow = new Follow();
            using var player = new OtherPortsPlayer(others, main.GetTempoMap(), follow.Position, follow.Send, () => 0, Passages(midi));
            var pc = rows.Single(r => r.Track == 3 && r.Type == "Program Change");
            Assert.Equal("Mixer: P50 (bank 0 / 0, PC 51)", MidiEventList.FixedText(pc, name, player));
            var volumes = rows.Where(r => r.Track == 3 && r.Type == "Control Change").ToList();
            Assert.Equal("Fixed in Mixer tab: 30", MidiEventList.FixedText(volumes[0], name, player));
            Assert.Null(MidiEventList.FixedText(volumes[1], name, player));
            Assert.Null(MidiEventList.FixedText(pc, name));   // no player: the ports are not played apart
        }
        finally
        {
            PortInstruments.Clear();
            PortControllers.Clear();
        }
    }

    static List<(int Port, int Channel, MidiEvent Event)> Settings(params (int Port, int Channel, MidiEvent Event)[] events) => events.ToList();

    [Fact]
    public void Append_of_Mixer_writes_settings_of_each_port_in_its_tracks()
    {
        // Volume of A1, instrument of B1, at 48: each before the notes there, in the track of its channel and port.
        var midi = TwoPorts();
        var first = MidiEventEdits.InsertByChannel(midi, 48, Settings((0, 0, Cc(0, 7, 20)), (1, 0, Pc(0, 50))), true);
        var tracks = midi.GetTrackChunks().ToList();
        Assert.Equal((2, 3), first);
        var a = tracks[1].GetTimedEvents().ToList();
        Assert.Equal((48L, 20), (a[3].Time, (int)((ControlChangeEvent)a[3].Event).ControlValue));
        var b = tracks[2].GetTimedEvents().ToList();
        Assert.Equal((48L, 50), (b[3].Time, (int)((ProgramChangeEvent)b[3].Event).ProgramNumber));
        Assert.IsType<NoteOnEvent>(b[4].Event);
    }

    [Fact]
    public void Append_at_event_of_port_B_writes_settings_of_port_A_in_its_tracks()
    {
        // After the Note On of track 3 (port B, index 3, at 48): instrument and volume of B1 there, the last one selected;
        // the volume of A1 at the same position in the track of channel 1 of port A.
        var midi = TwoPorts();
        var selected = MidiEventEdits.InsertSettings(midi, 3, 3, 48, Settings((0, 0, Cc(0, 7, 20)), (1, 0, Pc(0, 50)), (1, 0, Cc(0, 7, 30))),
                                                     Array.Empty<(int, int)>(), true);
        var tracks = midi.GetTrackChunks().ToList();
        Assert.Equal((3, 5), selected);
        var b = tracks[2].GetTimedEvents().ToList();
        Assert.IsType<NoteOnEvent>(b[3].Event);
        Assert.Equal((48L, 50), (b[4].Time, (int)((ProgramChangeEvent)b[4].Event).ProgramNumber));
        Assert.Equal((48L, 30), (b[5].Time, (int)((ControlChangeEvent)b[5].Event).ControlValue));
        var a = Assert.Single(tracks[1].GetTimedEvents(), t => t.Event is ControlChangeEvent);
        Assert.Equal((48L, 20), (a.Time, (int)((ControlChangeEvent)a.Event).ControlValue));

        // Replace of the Program Change of track 3 with a setting of A1 only: it stays (nothing of port B to put there).
        midi = TwoPorts();
        selected = MidiEventEdits.InsertSettings(midi, 3, 1, 0, Settings((0, 0, Cc(0, 7, 20))), new[] { (3, 1) }, true);
        tracks = midi.GetTrackChunks().ToList();
        Assert.Equal((2, 2), selected);
        Assert.Equal(40, (int)tracks[2].Events.OfType<ProgramChangeEvent>().Single().ProgramNumber);

        // A file played on one port: all of them at the place chosen, as before.
        midi = TwoPorts();
        selected = MidiEventEdits.InsertSettings(midi, 3, 3, 48, Settings((0, 0, Cc(0, 7, 20)), (0, 5, Cc(5, 10, 64))), Array.Empty<(int, int)>(), false);
        Assert.Equal((3, 5), selected);
        Assert.Empty(midi.GetTrackChunks().ElementAt(1).Events.OfType<ControlChangeEvent>());
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

    // ---------- Ports named by Device Name events (FF 09) ----------

    static TrackChunk Named(string device, params (long Time, MidiEvent Event)[] events)
    {
        var timed = events.Select(e => new TimedEvent(e.Event, e.Time)).ToList();
        if (device != null) timed.Insert(0, new TimedEvent(new DeviceNameEvent(device), 0));
        return timed.ToTrackChunk();
    }

    static int[] Ports(MidiFile midi)
    {
        var portOf = MidiPorts.PortsOf(midi);
        return midi.GetTrackChunks().Select(portOf).ToArray();
    }

    [Fact]
    public void Device_names_set_ports_of_tracks_whose_channels_collide()
    {
        // Two outputs of a module: by the last word of their names (B first in the file); a track without one: port A.
        var midi = new MidiFile(Track(null, (0, new SetTempoEvent(500000))),
                                Named("Roland SC-8850 PART B", (0, Pc(0, 40))), Named("Roland SC-8850 PART A", (0, Pc(0, 5))));
        Assert.Equal(new[] { 0, 1, 0 }, Ports(midi));
        Assert.True(MidiPorts.HasOtherPorts(midi));
        var (_, others) = MidiPorts.Split(midi);
        Assert.Equal((1, 40), (others.Single().Port, (int)((ProgramChangeEvent)others.Single().Event).ProgramNumber));
        Assert.True(MidiPorts.TracksOf(midi, 1)(midi.GetTrackChunks().ElementAt(1)));

        // Names without a port letter: in the order they come in, after the tracks without one.
        midi = new MidiFile(Named(null, (0, Pc(0, 1))), Named("Synth", (0, Pc(0, 2))), Named("Wavetable", (0, Pc(0, 3))));
        Assert.Equal(new[] { 0, 1, 2 }, Ports(midi));

        // One port: the channels of the names apart (nothing collides); more names than the engine has ports (instrument
        // names, for example); a file with MIDI Port events (they decide).
        midi = new MidiFile(Named("Out A", (0, Pc(0, 1))), Named("Out B", (0, Pc(1, 2))));
        Assert.False(MidiPorts.HasOtherPorts(midi));
        midi = new MidiFile(Enumerable.Range(0, 5).Select(i => Named($"Voice {i}x", (0, Pc(0, i)))));
        Assert.False(MidiPorts.HasOtherPorts(midi));
        midi = new MidiFile(Named("Out A", (0, Pc(0, 1))), Named("Out B", (0, Pc(0, 2))));
        Assert.True(MidiPorts.HasOtherPorts(midi));
        midi.GetTrackChunks().First().Events.Insert(0, new PortPrefixEvent(0));
        Assert.False(MidiPorts.HasOtherPorts(midi));
    }

    // ---------- GS SysEx of the parts of a port ----------

    // Use for Rhythm Part of a channel at an address (40, or 50: a part of the other one of ports A and B).
    static NormalSysExEvent RhythmPart(int address, int channel, int value)
    {
        var data = ChannelMixer.RhythmPartEvent(channel, value).Data;
        data[4] = (byte)address;
        MidiTools.FixRolandChecksum(data);
        return new NormalSysExEvent(data);
    }

    static readonly byte[] GsReset = { 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 };

    [Fact]
    public void Split_plays_GS_part_SysEx_on_port_of_their_part()
    {
        // Port A: a reset, and the drum part of B2 (address 50); port B: the drum part of its channel 3, that of A4
        // (address 50), and a reset.
        var midi = new MidiFile(
            Track(0, (0, new NormalSysExEvent(GsReset)), (0, RhythmPart(0x50, 1, 1)), (0, On(0, 60)), (96, Off(0, 60))),
            Track(1, (0, RhythmPart(0x40, 2, 2)), (0, RhythmPart(0x50, 3, 1)), (0, new NormalSysExEvent(GsReset)), (48, On(2, 36)), (96, Off(2, 36))))
        { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) };
        Assert.Equal((1, 1, 1), MidiPorts.RhythmPart(RhythmPart(0x50, 1, 1).Data, 0));
        Assert.Equal((0, 3, 1), MidiPorts.RhythmPart(RhythmPart(0x50, 3, 1).Data, 1));
        Assert.Null(MidiPorts.RhythmPart(RhythmPart(0x50, 3, 1).Data, 2));   // from port C: not known
        Assert.Null(MidiPorts.RhythmPart(GsReset, 1));

        var (main, others) = MidiPorts.Split(midi);
        // The main file: both resets (they act on the whole engine, as on a module), and the drum part of A4 at address 40.
        var kept = main.GetTimedEvents().Select(t => t.Event).OfType<SysExEvent>().ToList();
        Assert.Equal(2, kept.Count(e => MidiTools.IsGsReset(e.Data)));
        Assert.Equal(3, ChannelMixer.RhythmPartChannel(Assert.Single(kept, e => ChannelMixer.RhythmPartChannel(e.Data) >= 0).Data));
        // The others: the drum parts of B2 and B3 at address 40 (right checksum), with the notes of port B.
        var parts = others.Where(e => e.Event is SysExEvent).Select(e => (e.Port, ChannelMixer.RhythmPartChannel(((SysExEvent)e.Event).Data), (int)((SysExEvent)e.Event).Data[7]));
        Assert.Equal(new[] { (1, 1, 1), (1, 2, 2) }, parts);
        foreach (var e in others.Select(e => e.Event).OfType<SysExEvent>())
        {
            var data = (byte[])e.Data.Clone();
            MidiTools.FixRolandChecksum(data);
            Assert.Equal(data, e.Data);
        }
        Assert.Equal(2, others.Count(e => e.Event is ChannelEvent));
    }

    [Fact]
    public void Player_follows_drum_parts_and_sends_state_again_after_reset()
    {
        // Port B: program 40 on channel 1; channel 2 a drum part at 10, then a note on it (not transposed) and one on
        // channel 1 (transposed by 2).
        var midi = new MidiFile(
            Track(null, (0, new SetTempoEvent(500000))),
            Track(0, (0, On(0, 60)), (96, Off(0, 60))),
            Track(1, (0, Pc(0, 40)), (10, RhythmPart(0x40, 1, 1)), (20, On(1, 36)), (30, Off(1, 36)), (40, On(0, 60)), (90, Off(0, 60))))
        { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) };
        var (main, others) = MidiPorts.Split(midi);
        var follow = new Follow();
        int resets = 0;
        using var player = new OtherPortsPlayer(others, main.GetTempoMap(), follow.Position, follow.Send, () => 2, null, () => Volatile.Read(ref resets));
        follow.WaitFirstLook();
        Assert.Equal(1, player.Rhythm(1, 9));   // the 10th channel: drums
        Assert.Equal(0, player.Rhythm(1, 1));
        follow.Running = true;
        Interlocked.Exchange(ref follow.Tick, 45);
        Wait(() => follow.Texts().Contains("1:on1/62"));
        Assert.Equal(new[] { "1:pc1/40", "1:rhythm2=1", "1:on2/36", "1:off2/36", "1:on1/62" }, follow.Texts());
        Assert.Equal(1, player.Rhythm(1, 1));

        // A mode reset sent to the engine (all its channels set back): the drum part and the program sent again.
        follow.Clear();
        Interlocked.Increment(ref resets);
        Wait(() => follow.Texts().Contains("1:pc1/40"));
        Assert.Equal(new[] { "1:rhythm2=1", "1:pc1/40" }, follow.Texts());

        // Back before the drum part: sounds cut, the channel a normal part again.
        follow.Clear();
        Interlocked.Exchange(ref follow.Tick, 5);
        Wait(() => follow.Texts().Contains("1:pc1/40"));
        Assert.Equal(new[] { "1:cc1/64=0", "1:cc1/120=0", "1:cc2/64=0", "1:cc2/120=0", "1:rhythm2=0", "1:pc1/40" }, follow.Texts());
        Assert.Equal(0, player.Rhythm(1, 1));
    }

    [Fact]
    public void Event_list_by_port_sets_drum_parts_on_their_port()
    {
        // The drum part of channel 2 in a track of port B, then a note of B2 and one of A2.
        Func<int, int, int, int, int, string> name = (c, program, msb, lsb, rhythm) => rhythm > 0 ? "drums" : "sound";
        var midi = new MidiFile(
            Track(0, (60, On(1, 60)), (96, Off(1, 60))),
            Track(1, (0, RhythmPart(0x40, 1, 1)), (48, On(1, 36)), (96, Off(1, 36))))
        { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) };
        string[] Notes(bool byPort) => MidiEventList.Build(midi, name, byPort: byPort).Where(r => r.Type == "Note").Select(r => r.Description).ToArray();
        Assert.Equal(new[] { "drums", "drums" }, Notes(false));   // on 16 channels: both
        Assert.Equal(new[] { "drums", "sound" }, Notes(true));

        // The same from a track of port A, at address 50.
        midi = new MidiFile(
            Track(0, (0, RhythmPart(0x50, 1, 1)), (60, On(1, 60)), (96, Off(1, 60))),
            Track(1, (48, On(1, 36)), (96, Off(1, 36))))
        { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) };
        Assert.Equal(new[] { "drums", "sound" }, Notes(true));
    }

    [Fact]
    public void Player_follows_XG_drum_banks_in_XG_track()
    {
        // Port B: drum kit bank on channel 2 (not transposed), a normal bank on channel 10, a note on each.
        var midi = new MidiFile(
            Track(null, (0, new SetTempoEvent(500000))),
            Track(0, (0, On(0, 60)), (96, Off(0, 60))),
            Track(1, (0, Cc(1, 0, 127)), (0, Pc(1, 0)), (0, Cc(9, 0, 0)), (0, Pc(9, 0)), (20, On(1, 36)), (30, Off(1, 36)), (20, On(9, 60)), (30, Off(9, 60))))
        { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) };
        var (main, others) = MidiPorts.Split(midi);
        try
        {
            ChannelMixer.XgMode = true;
            var follow = new Follow();
            using var player = new OtherPortsPlayer(others, main.GetTempoMap(), follow.Position, follow.Send, () => 2);
            follow.WaitFirstLook();
            Assert.True(player.IsDrum(1, 9));    // before any bank select: the 10th channel
            Assert.False(player.IsDrum(1, 1));
            follow.Running = true;
            Interlocked.Exchange(ref follow.Tick, 40);
            Wait(() => follow.Texts().Contains("1:off10/62"));
            Assert.True(player.IsDrum(1, 1));
            Assert.False(player.IsDrum(1, 9));
            Assert.Contains("1:on2/36", follow.Texts());    // drums: as they are
            Assert.Contains("1:on10/62", follow.Texts());   // a normal channel: transposed
            Assert.Equal(0, player.Rhythm(1, 1));            // (no GS drum part)

            // Another mode: the GS drum part decides (the 10th channel).
            ChannelMixer.XgMode = false;
            Assert.False(player.IsDrum(1, 1));
            Assert.True(player.IsDrum(1, 9));
        }
        finally { ChannelMixer.XgMode = false; }
    }

    [Fact]
    public void Player_follows_GM2_drum_banks_in_GM2_track()
    {
        // Port B: the GM2 drum kit bank on channel 2 (not transposed), the normal bank on channel 10.
        var midi = new MidiFile(
            Track(null, (0, new SetTempoEvent(500000))),
            Track(0, (0, On(0, 60)), (96, Off(0, 60))),
            Track(1, (0, Cc(1, 0, 120)), (0, Pc(1, 0)), (0, Cc(9, 0, 121)), (0, Pc(9, 0)), (20, On(1, 36)), (30, Off(1, 36))))
        { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) };
        var (main, others) = MidiPorts.Split(midi);
        try
        {
            ChannelMixer.Gm2Mode = true;
            // The rule (any port: channel 26 is the 10th of port B); another mode: none (the GS drum part decides).
            Assert.Equal(new bool?[] { false, true, false, true, true }, new[] { (2, 0), (9, 0), (9, 121), (2, 120), (25, 0) }.Select(x => InstrumentDefinitions.SoundFontBankDrum(x.Item1, x.Item2)));
            var follow = new Follow();
            using var player = new OtherPortsPlayer(others, main.GetTempoMap(), follow.Position, follow.Send, () => 2);
            follow.WaitFirstLook();
            Assert.True(player.IsDrum(1, 9));
            Assert.False(player.IsDrum(1, 1));
            follow.Running = true;
            Interlocked.Exchange(ref follow.Tick, 40);
            Wait(() => follow.Texts().Contains("1:off2/36"));
            Assert.True(player.IsDrum(1, 1));
            Assert.False(player.IsDrum(1, 9));
            Assert.Contains("1:on2/36", follow.Texts());   // drums: as they are
            ChannelMixer.Gm2Mode = false;
            Assert.Null(InstrumentDefinitions.SoundFontBankDrum(9, 0));
        }
        finally { ChannelMixer.Gm2Mode = false; }
    }

    [Fact]
    public void Player_plays_drum_part_chosen_instead_of_file()
    {
        // Port B: channel 2 a drum part at 10 (GS), a note on it.
        var midi = new MidiFile(
            Track(null, (0, new SetTempoEvent(500000))),
            Track(0, (0, On(0, 60)), (96, Off(0, 60))),
            Track(1, (10, RhythmPart(0x40, 1, 1)), (20, On(1, 36)), (30, Off(1, 36))))
        { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) };
        var (main, others) = MidiPorts.Split(midi);
        PortRhythm.Clear();
        try
        {
            var follow = new Follow();
            using var player = new OtherPortsPlayer(others, main.GetTempoMap(), follow.Position, follow.Send, () => 0);
            follow.WaitFirstLook();
            follow.Running = true;
            Interlocked.Exchange(ref follow.Tick, 40);
            Wait(() => follow.Texts().Contains("1:off2/36"));
            Assert.Equal(1, player.Rhythm(1, 1));

            // The normal part chosen: sent at once; the file's drum part still known.
            follow.Clear();
            player.SetRhythm(1, 1, 0);
            Assert.Equal(new[] { "1:rhythm2=0" }, follow.Texts());
            Assert.Equal((0, 1, (int?)0), (player.Rhythm(1, 1), player.FileRhythm(1, 1), PortRhythm.Of(1, 1)));

            // Back before the file's drum part, then played again: the choice instead of it.
            follow.Clear();
            Interlocked.Exchange(ref follow.Tick, 5);
            Wait(() => follow.Texts().Contains("1:cc2/120=0"));
            Interlocked.Exchange(ref follow.Tick, 40);
            Wait(() => follow.Texts().Contains("1:off2/36"));
            Assert.Contains("1:rhythm2=0", follow.Texts());
            Assert.DoesNotContain("1:rhythm2=1", follow.Texts());

            // Back to the file's.
            follow.Clear();
            player.ClearRhythm(1, 1);
            Assert.Equal(new[] { "1:rhythm2=1" }, follow.Texts());
            Assert.Null(PortRhythm.Of(1, 1));
        }
        finally { PortRhythm.Clear(); }
    }

    [Fact]
    public void Save_writes_drum_part_of_port_B_in_its_tracks()
    {
        // Channel 2 a drum part on port A; on port B, the drum part chosen for its channel 2.
        var midi = new MidiFile(
            Track(0, (0, RhythmPart(0x40, 1, 1)), (48, On(1, 60)), (96, Off(1, 60))),
            Track(1, (48, On(1, 36)), (96, Off(1, 36))))
        { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) };
        int controllers = ChannelMixer.ControllerNumbers.Length;
        var noCc = Enumerable.Range(0, controllers).Select(_ => new bool[16]).ToArray();
        var noValues = Enumerable.Range(0, controllers).Select(_ => new int[16]).ToArray();
        var parts = Enumerable.Repeat(-1, 16).ToArray();
        parts[1] = 1;
        MidiFileEditor.ApplyChanges(midi, Array.Empty<(int, long, int, int, int)>(), noCc, noValues, parts, true, null,
                                    MidiPorts.TracksOf(midi, 1), "B", changeSysEx: false);
        int[] Parts(int track) => midi.GetTrackChunks().ElementAt(track).Events.OfType<SysExEvent>()
                                      .Where(e => ChannelMixer.RhythmPartChannel(e.Data) == 1).Select(e => (int)e.Data[7]).ToArray();
        Assert.Equal(new[] { 1 }, Parts(0));   // port A: its own, as it was
        Assert.Equal(new[] { 1 }, Parts(1));   // port B: in its track, played on port B
        Assert.Contains(MidiPorts.Split(midi).Others, e => e.Port == 1 && e.Event is SysExEvent sx && ChannelMixer.RhythmPartChannel(sx.Data) == 1);

        // In the Event Editor (listed by port): blue while a drum part is chosen on that channel of port B (not port A's).
        PortRhythm.Clear();
        try
        {
            Func<int, int, int, int, int, string> name = (c, program, msb, lsb, rhythm) => "";
            var rows = MidiEventList.Build(midi, name, byPort: true).Where(r => r.Type == "SysEx").ToList();
            var (_, others) = MidiPorts.Split(midi);
            using var player = new OtherPortsPlayer(others, midi.GetTempoMap(), () => null, (e, p) => { }, () => 0);
            Assert.All(rows, r => Assert.Null(MidiEventList.FixedText(r, name, player)));
            PortRhythm.Set(1, 1, 0);
            Assert.Null(MidiEventList.FixedText(rows.Single(r => r.Port == 0), name, player));
            Assert.Equal("Drum part chosen in Mixer tab", MidiEventList.FixedText(rows.Single(r => r.Port == 1), name, player));
        }
        finally { PortRhythm.Clear(); }
    }

    [Fact]
    public void Event_list_of_XG_track_starts_10th_channel_on_drum_bank()
    {
        // Names by the bank, as in SF2 mode with an XG track: a note of channel 10 before any bank select, one of
        // channel 3 after MSB 127.
        Func<int, int, int, int, int, string> name = (c, program, msb, lsb, rhythm) => InstrumentDefinitions.IsXgDrumBank(msb) ? "drums" : "sound";
        var midi = new MidiFile(Track(null, (0, On(9, 36)), (48, Off(9, 36)), (60, Cc(2, 0, 127)), (60, On(2, 38)), (96, Off(2, 38))))
        { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) };
        string[] Notes(bool xg) => MidiEventList.Build(midi, name, xgBanks: xg).Where(r => r.Type == "Note").Select(r => r.Description).ToArray();
        Assert.Equal(new[] { "drums", "drums" }, Notes(true));
        Assert.Equal(new[] { "sound", "drums" }, Notes(false));
    }

    [Fact]
    public void Save_of_drum_part_of_port_A_leaves_those_of_port_B()
    {
        var midi = new MidiFile(
            Track(0, (0, RhythmPart(0x40, 1, 1)), (48, On(1, 60)), (96, Off(1, 60))),
            Track(1, (0, RhythmPart(0x40, 1, 1)), (48, On(1, 36)), (96, Off(1, 36))))
        { TimeDivision = new TicksPerQuarterNoteTimeDivision(96) };
        int controllers = ChannelMixer.ControllerNumbers.Length;
        var noCc = Enumerable.Range(0, controllers).Select(_ => new bool[16]).ToArray();
        var noValues = Enumerable.Range(0, controllers).Select(_ => new int[16]).ToArray();
        var rhythm = Enumerable.Repeat(-1, 16).ToArray();
        rhythm[1] = 0;   // A2: normal part
        MidiFileEditor.ApplyChanges(midi, Array.Empty<(int, long, int, int, int)>(), noCc, noValues, rhythm, true, null, MidiPorts.TracksOf(midi, 0), "A");
        int[] Parts(int track) => midi.GetTrackChunks().ElementAt(track).Events.OfType<SysExEvent>()
                                      .Where(e => ChannelMixer.RhythmPartChannel(e.Data) == 1).Select(e => (int)e.Data[7]).ToArray();
        Assert.Equal(new[] { 0 }, Parts(0));   // port A: replaced
        Assert.Equal(new[] { 1 }, Parts(1));   // port B: kept
    }
}
