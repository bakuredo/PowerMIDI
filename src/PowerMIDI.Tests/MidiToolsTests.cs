using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

public class MidiToolsTests
{
    static MidiFile File(params MidiEvent[] events) =>
        new(new TrackChunk(events)) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };

    static MidiEvent Reset(string mode) => MidiTools.GetModeEvents(mode)[0];
    static NoteOnEvent On(int note, long delta = 0) => new((SevenBitNumber)note, (SevenBitNumber)90) { DeltaTime = delta };
    static NoteOffEvent Off(int note, long delta) => new((SevenBitNumber)note, (SevenBitNumber)0) { DeltaTime = delta };
    static ControlChangeEvent Cc(int number, int value, long delta = 0) => new((SevenBitNumber)number, (SevenBitNumber)value) { DeltaTime = delta };

    // GS parameter SysEx that is not a reset (reverb macro).
    static NormalSysExEvent GsParameter() => new(new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x01, 0x30, 0x04, 0x0B, 0xF7 });

    [Fact]
    public void Standard_of_a_file_without_reset_is_the_default()
    {
        Assert.Equal("GM", MidiTools.DefaultStandard);
        Assert.Equal("GM", MidiTools.GetStandard(File(On(60), Off(60, 480))));
    }

    [Theory]
    [InlineData("GS")]
    [InlineData("XG")]
    [InlineData("GM")]
    [InlineData("GM2")]
    [InlineData("MT-32")]
    public void Standard_follows_the_reset_of_the_file(string mode) =>
        Assert.Equal(mode, MidiTools.GetStandard(File(Reset(mode), On(60), Off(60, 480))));

    [Fact]
    public void Last_reset_of_the_file_wins()
    {
        var gs = Reset("GS");
        gs.DeltaTime = 10;
        Assert.Equal("GS", MidiTools.GetStandard(File(Reset("XG"), gs)));
    }

    [Fact]
    public void Gm_reset_followed_by_gs_parameters_is_gs() =>
        Assert.Equal("GS", MidiTools.GetStandard(File(Reset("GM"), GsParameter())));

    [Fact]
    public void Mode_events_are_the_reset_of_the_mode()
    {
        Assert.Single(MidiTools.GetModeEvents("GS"));
    }

    [Fact]
    public void File_extensions()
    {
        Assert.True(MidiTools.IsMidiFile(@"C:\music\song.MID"));
        Assert.True(MidiTools.IsMidiFile("song.kar"));
        Assert.False(MidiTools.IsMidiFile("song.mp3"));
    }

    [Theory]
    [InlineData("loopStart", "loopEnd")]
    [InlineData("Loop_Start", "LOOP END")]
    [InlineData("[", "]")]
    public void Loop_from_markers(string start, string end)
    {
        var loop = MidiTools.GetLoop(File(
            On(72), Off(72, 400),
            new MarkerEvent(start) { DeltaTime = 80 },
            On(60), Off(60, 960),
            new MarkerEvent(end),
            On(79), Off(79, 400)));
        Assert.NotNull(loop);
        Assert.Equal(480, loop.Value.Start);
        Assert.Equal(1440, loop.Value.End);
    }

    [Fact]
    public void Loop_from_cc111_ends_with_the_file()
    {
        var loop = MidiTools.GetLoop(File(On(72), Off(72, 480), Cc(111, 0), On(60), Off(60, 960)));
        Assert.NotNull(loop);
        Assert.Equal((480, 1440, "CC111"), loop.Value);
    }

    [Fact]
    public void No_loop_without_a_start_and_no_empty_loop()
    {
        Assert.Null(MidiTools.GetLoop(File(On(60), Off(60, 480))));
        Assert.Null(MidiTools.GetLoop(File(On(60), Off(60, 480), new MarkerEvent("loopStart"), new MarkerEvent("loopEnd"))));
    }

    // The state at the loop start is sent again where the loop changed it; what is unchanged or cannot be restored is left out.
    [Fact]
    public void Loop_end_notes_are_those_ending_in_its_last_moments_or_held_past_it()
    {
        // Loop 480-1440: C4 ends at 880, E4 at the loop end; G4 and D4 (played again at 1420) started in the loop and
        // held past its end (pads); B3 started before the loop and held across it (a drone: kept); A4 starts at the
        // loop end (not in the loop).
        var file = File(
            On(59, 0),                                 // 0-2000
            On(60, 480), Off(60, 400),                 // 480-880
            On(64, 80), On(62, 0), On(67, 0),          // 960
            Off(62, 440),                              // 1400
            On(62, 20),                                // 1420
            Off(64, 20),                               // 1440
            On(69, 0), Off(69, 100), Off(62, 0), Off(67, 0),
            Off(59, 460));
        Assert.Equal(new[] { 62, 64, 67 }, MidiTools.GetLoopEndNotes(file, 480, 1300, 1440).Select(x => x.Note).OrderBy(x => x));
        Assert.Equal(new[] { 60, 62, 64, 67 }, MidiTools.GetLoopEndNotes(file, 480, 0, 1440).Select(x => x.Note).OrderBy(x => x));
    }

    [Fact]
    public void Loop_restore_gives_back_the_state_of_the_loop_start()
    {
        var file = File(
            Cc(0, 0), Cc(32, 0), new ProgramChangeEvent((SevenBitNumber)11),
            Cc(7, 110), Cc(10, 30),
            On(72), Off(72, 400),
            new MarkerEvent("loopStart") { DeltaTime = 80 },
            On(60), Off(60, 300),
            Cc(7, 40, 20), Cc(11, 50), Cc(10, 30),                 // fade: volume, expression (never set before); pan unchanged
            new ProgramChangeEvent((SevenBitNumber)20),
            new PitchBendEvent(12000),
            Cc(101, 0), Cc(100, 0), Cc(6, 12), Cc(99, 1), Cc(98, 8), Cc(6, 70),   // bend range 12, then an NRPN (not restored)
            On(64, 160), Off(64, 480),
            new MarkerEvent("loopEnd"),
            Cc(7, 5));                                             // at the loop end: not part of the loop
        var loop = MidiTools.GetLoop(file).Value;

        var restored = MidiTools.GetLoopRestore(file, loop.Start, loop.End).Select(e => e switch
        {
            ControlChangeEvent c => $"cc{(int)c.ControlNumber}={(int)c.ControlValue}",
            ProgramChangeEvent p => $"pc={(int)p.ProgramNumber}",
            PitchBendEvent b => $"pb={b.PitchValue}",
            _ => e.ToString(),
        });

        Assert.Equal("cc0=0 cc32=0 pc=11 cc7=110 cc11=127 cc101=0 cc100=0 cc6=2 cc101=127 cc100=127 pb=8192", string.Join(" ", restored));
    }

    [Fact]
    public void Loop_restore_is_empty_when_the_loop_changes_nothing()
    {
        var file = File(Cc(7, 100), new MarkerEvent("loopStart"), On(60), Off(60, 480), new MarkerEvent("loopEnd"));
        var loop = MidiTools.GetLoop(file).Value;
        Assert.Empty(MidiTools.GetLoopRestore(file, loop.Start, loop.End));
    }
}
