using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// GS part parameters of the Effects tab's Part page (DT1 40 1x / 40 2x, pitch bend range and fine tuning RPN,
// CC126 / CC127): read during playback, corrected with the values fixed in the interface, and written in a saved file.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class GsPartParamsTests
{
    // DT1 40 hb aa vv … (hb: 10 / 20 + part block) with its checksum.
    static NormalSysExEvent Dt1(int hi, int address, params byte[] values)
    {
        var d = new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, (byte)hi, (byte)address }.Concat(values).Concat(new byte[] { 0, 0xF7 }).ToArray();
        MidiTools.FixRolandChecksum(d);
        return new NormalSysExEvent(d);
    }
    static ControlChangeEvent Cc(int channel, int number, int value) =>
        new((SevenBitNumber)number, (SevenBitNumber)value) { Channel = (FourBitNumber)channel };
    static MidiEvent Play(MidiEvent e) => ChannelMixer.Callback(e, 0, TimeSpan.Zero);

    static void Run(Action test)
    {
        bool gs = ChannelMixer.GsMode;
        try
        {
            ChannelMixer.GsMode = true;
            ChannelMixer.ResetChannelState(false);
            test();
        }
        finally
        {
            GsPartParams.Clear(null);
            ChannelMixer.ResetChannelState(false);
            ChannelMixer.GsMode = gs;
        }
    }

    [Fact]
    public void Dt1_of_the_file_is_read_and_corrected() => Run(() =>
    {
        // Key shift +5 on part 1 (block 1), part 10 (block 0) and part 11 (block A).
        var shift = Dt1(0x11, 0x16, 0x45);
        Assert.Same(shift, Play(shift));
        Assert.Equal(0x45, GsPartParams.FileValue(0, GsPartParams.KeyShift));
        Play(Dt1(0x10, 0x16, 0x3E));
        Assert.Equal(0x3E, GsPartParams.FileValue(9, GsPartParams.KeyShift));
        Play(Dt1(0x1A, 0x16, 0x50));
        Assert.Equal(0x50, GsPartParams.FileValue(10, GsPartParams.KeyShift));

        GsPartParams.Set(null, 0, GsPartParams.KeyShift, 0x3A);
        Assert.Equal(Dt1(0x11, 0x16, 0x3A).Data, ((SysExEvent)Play(shift)).Data);

        // Offset fine: two nibbles; bend range: 40 2x 10.
        Play(Dt1(0x12, 0x17, 0x09, 0x04));
        Assert.Equal(0x94, GsPartParams.FileValue(1, GsPartParams.OffsetFine));
        Play(Dt1(0x22, 0x10, 0x4C));
        Assert.Equal(0x4C, GsPartParams.FileValue(1, GsPartParams.BendRange));
        Assert.Equal(Dt1(0x22, 0x10, 0x48).Data, GsPartParams.ParamEvent(1, GsPartParams.BendRange, 0x48).Data);
        Assert.Equal(Dt1(0x12, 0x17, 0x07, 0x0B).Data, GsPartParams.ParamEvent(1, GsPartParams.OffsetFine, 0x7B).Data);
    });

    [Fact]
    public void Rpn_and_mono_poly() => Run(() =>
    {
        Play(Cc(3, 101, 0));
        Play(Cc(3, 100, 0));
        Play(Cc(3, 6, 12));
        Assert.Equal(0x40 + 12, GsPartParams.FileValue(3, GsPartParams.BendRange));
        GsPartParams.Set(null, 3, GsPartParams.BendRange, 0x40 + 5);
        Assert.Equal(5, (int)((ControlChangeEvent)Play(Cc(3, 6, 12))).ControlValue);

        Play(Cc(3, 100, 1));                                             // fine tuning
        Play(Cc(3, 6, 70));
        Assert.Equal(70, GsPartParams.FileValue(3, GsPartParams.FineTune));

        GsPartParams.Set(null, 3, GsPartParams.MonoPoly, 0);
        Assert.Equal(126, (int)((ControlChangeEvent)Play(Cc(3, 127, 0))).ControlNumber);
        Assert.Equal(1, GsPartParams.FileValue(3, GsPartParams.MonoPoly));
    });

    [Fact]
    public void Saved_file_has_the_fixed_values() => Run(() =>
    {
        var file = new MidiFile(new TrackChunk(
            new NormalSysExEvent(new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 }),   // GS Reset
            Dt1(0x13, 0x16, 0x40),
            Cc(2, 101, 0), Cc(2, 100, 0), Cc(2, 6, 2),
            new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { Channel = (FourBitNumber)2, DeltaTime = 480 }));
        GsPartParams.Set(null, 2, GsPartParams.KeyShift, 0x4C);
        GsPartParams.Set(null, 2, GsPartParams.BendRange, 0x40 + 12);
        MidiFileEditor.WriteGsPartParams(file);
        var events = file.GetTrackChunks().Single().Events.ToList();
        Assert.Equal(2, events.OfType<SysExEvent>().Count(e => e.Data.SequenceEqual(Dt1(0x13, 0x16, 0x4C).Data)));   // after the reset + corrected
        Assert.Equal(12, events.OfType<ControlChangeEvent>().Single(c => c.ControlNumber == 6).ControlValue);
    });
}
