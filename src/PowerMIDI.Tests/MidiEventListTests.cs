using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// Event list of the Event Editor (MidiEventList): one row per event, a note as one row, sorted by position then track,
// with readable texts; the events replaced by a value fixed in the Mixer or Effects tab.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class MidiEventListTests
{
    static NoteOnEvent On(int note, long delta = 0, int channel = 0) =>
        new((SevenBitNumber)note, (SevenBitNumber)90) { DeltaTime = delta, Channel = (FourBitNumber)channel };
    static NoteOffEvent Off(int note, long delta, int channel = 0) =>
        new((SevenBitNumber)note, (SevenBitNumber)0) { DeltaTime = delta, Channel = (FourBitNumber)channel };
    static ControlChangeEvent Cc(int number, int value, long delta = 0) => new((SevenBitNumber)number, (SevenBitNumber)value) { DeltaTime = delta };

    static List<MidiEventRow> Build(params TrackChunk[] tracks) =>
        MidiEventList.Build(new MidiFile(tracks) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) },
                            (c, program, msb, lsb, rhythm) => rhythm > 0 ? "Drums" : $"P{program + 1} {msb}/{lsb}");

    [Fact]
    public void Notes_are_one_row_and_rows_follow_position_then_track()
    {
        var rows = Build(
            new TrackChunk(new SequenceTrackNameEvent("Piano"), On(60, 0), Off(60, 480), new SetTempoEvent(500000) { DeltaTime = 0 }),
            new TrackChunk(On(64, 240), Off(64, 240)));
        Assert.Equal(new[] { (0L, 1, "Track Name"), (0, 1, "Note"), (240, 2, "Note"), (480, 1, "Tempo") },
                     rows.Select(r => (r.Tick, r.Track, r.Type)));
        Assert.Equal("C3 (60) · vel 90 · len 480", rows[1].Data);
        Assert.Equal(("Piano", "120 bpm"), (rows[0].Data, rows[3].Data));
    }

    [Fact]
    public void Program_change_names_the_instrument_with_its_bank()
    {
        var rows = Build(new TrackChunk(Cc(0, 8), Cc(32, 2), new ProgramChangeEvent((SevenBitNumber)4), On(60), Off(60, 10)));
        var pc = rows.Single(r => r.Kind == EventKind.Program);
        Assert.Equal(("5 · bank 8 / 2", "P5 8/2"), (pc.Data, pc.Description));
        Assert.Equal("P5 8/2", rows.Single(r => r.Kind == EventKind.Note).Description);   // the instrument playing the note
        Assert.Equal("Bank Select LSB", rows[1].Description);
    }

    [Fact]
    public void Data_entry_names_its_rpn()
    {
        var rows = Build(new TrackChunk(Cc(101, 0), Cc(100, 0), Cc(6, 12), Cc(99, 1), Cc(98, 8), Cc(6, 70)));
        Assert.Equal("Data Entry · Pitch Bend Sensitivity", rows[2].Description);
        Assert.Equal("Data Entry · NRPN 01 08", rows[5].Description);
    }

    [Fact]
    public void SysEx_resets_rhythm_part_and_display()
    {
        var gsReset = new NormalSysExEvent(new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 });
        var rhythm = ChannelMixer.RhythmPartEvent(1, 1);   // channel 2 on drum part 1
        var text = new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x06, 0x00, 0x00, (byte)'H', (byte)'i', 0xF7 });
        var rows = Build(new TrackChunk(gsReset, rhythm, text, new ProgramChangeEvent((SevenBitNumber)0) { Channel = (FourBitNumber)1 }));
        Assert.Equal(new[] { "GS Reset", "Use for rhythm part ch 2: drum 1", "Display: \"Hi\"", "Drums" }, rows.Select(r => r.Description));
        Assert.Equal("41 10 42 12 40 00 7F 00 41", rows[0].Data);
        Assert.Equal(-1, rows[0].Channel);
    }

    [Fact]
    public void Events_replaced_by_fixed_values()
    {
        var reverb = new NormalSysExEvent(new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x01, 0x30, 0x04, 0x0B, 0xF7 });   // GS reverb macro
        var rows = Build(new TrackChunk(Cc(7, 100), Cc(10, 64), Cc(0, 8), new ProgramChangeEvent((SevenBitNumber)4), reverb, On(60), Off(60, 10)));
        int volume = ChannelMixer.ControllerIndex(7), reverbMacro = Enumerable.Range(0, GsXgEffects.Count).First(e => GsXgEffects.ValuesOf(reverb.Data).Any(x => x.Param == e));
        try
        {
            Assert.All(rows, r => Assert.Null(MidiEventList.FixedText(r)));
            ChannelMixer.SetController(null, volume, 0, 90);
            ChannelMixer.SetOverride(null, 0, 0, 0, 10);
            GsXgEffects.Set(null, reverbMacro, 5);
            Assert.Equal(new[] { "Fixed in Mixer tab: 90", null, "Mixer: bank 0 / 0, PC 11", "Mixer: bank 0 / 0, PC 11", "Fixed in Effects tab", null },
                         rows.Select(r => MidiEventList.FixedText(r)));
        }
        finally
        {
            ChannelMixer.ClearController(null, volume, 0);
            ChannelMixer.ClearOverride(null, 0);
            GsXgEffects.Clear(null, reverbMacro);
        }
    }

    [Fact]
    public void Xg_events_replaced_by_fixed_values()
    {
        // XG track: voice map, Multi Part (volume of part 1), portamento switch, mono, pitch bend range (RPN 0 0).
        var voiceMap = new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x49, 0x00, 0x00, 0x12, 0x00, 0xF7 });
        var multiPart = new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x08, 0x00, 0x0B, 0x64, 0xF7 });
        var rows = Build(new TrackChunk(voiceMap, multiPart, Cc(65, 0), Cc(126, 0), Cc(101, 0), Cc(100, 0), Cc(6, 2)));
        int volume = ChannelMixer.ControllerIndex(7);
        bool xg = ChannelMixer.XgMode;
        ChannelMixer.XgMode = true;
        try
        {
            Assert.All(rows, r => Assert.Null(MidiEventList.FixedText(r)));
            ChannelMixer.VoiceMapOverride = 1;
            ChannelMixer.SetController(null, volume, 0, 90);
            ChannelMixer.SetController(null, ChannelMixer.PortamentoTime, 0, 20);
            XgPartParams.Set(null, 0, XgPartParams.MonoPoly, 1);   // poly
            XgPartParams.Set(null, 0, XgPartParams.BendRange, 0x40 + 12);
            Assert.Equal(new[] { "Voice map chosen in Mixer tab", "Fixed in Mixer tab (Multi Part)", "Portamento time fixed in Mixer tab: switch 127",
                                 "Fixed in Effects tab (Part)", null, null, "Fixed in Effects tab (Drums / Part)" },
                         rows.Select(r => MidiEventList.FixedText(r)));
        }
        finally
        {
            ChannelMixer.XgMode = xg;
            ChannelMixer.VoiceMapOverride = -1;
            ChannelMixer.ClearController(null, volume, 0);
            ChannelMixer.ClearController(null, ChannelMixer.PortamentoTime, 0);
            XgPartParams.Clear(null, 0, XgPartParams.MonoPoly);
            XgPartParams.Clear(null, 0, XgPartParams.BendRange);
        }
    }
}
