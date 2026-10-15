using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// XG part parameters of the Effects tab's Part page (Multi Part SysEx, NRPN, pitch bend range RPN, CC126 / CC127):
// read during playback, corrected with the values fixed in the interface, and written in a saved file.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class XgPartParamsTests
{
    const int BassGain = XgPartParams.BassGain, TrebleFrequency = 3;

    static ControlChangeEvent Cc(int channel, int number, int value) =>
        new((SevenBitNumber)number, (SevenBitNumber)value) { Channel = (FourBitNumber)channel };
    static NormalSysExEvent Part(int block, int part, params byte[] addressAndValues) =>
        new(new byte[] { 0x43, 0x10, 0x4C, (byte)block, (byte)part }.Concat(addressAndValues).Append((byte)0xF7).ToArray());
    static MidiEvent Play(MidiEvent e) => ChannelMixer.Callback(e, 0, TimeSpan.Zero);

    static void Run(Action test)
    {
        bool xg = ChannelMixer.XgMode;
        try
        {
            ChannelMixer.XgMode = true;
            ChannelMixer.ResetChannelState(true);
            test();
        }
        finally
        {
            XgPartParams.Clear(null);
            ChannelMixer.ResetChannelState(false);
            ChannelMixer.XgMode = xg;
        }
    }

    [Fact]
    public void Eq_SysEx_and_nrpn_of_the_file_are_read_and_corrected() => Run(() =>
    {
        Assert.Equal(64, XgPartParams.FileValue(2, BassGain));
        var message = Part(0x08, 2, 0x72, 70);
        Assert.Same(message, Play(message));
        Assert.Equal(70, XgPartParams.FileValue(2, BassGain));

        XgPartParams.Set(null, 2, BassGain, 58);
        Assert.Equal(Part(0x08, 2, 0x72, 58).Data, ((SysExEvent)Play(message)).Data);
        Assert.Equal(58, ((SysExEvent)Play(Part(0x08, 2, 0x72, 70, 0x73, 64))).Data[6]);   // bass and treble gain

        Play(Cc(2, 99, 0x01));
        Play(Cc(2, 98, 0x35));
        var entry = Cc(2, 6, 0x30);
        Assert.Same(entry, Play(entry));
        Assert.Equal(0x30, XgPartParams.FileValue(2, TrebleFrequency));
        Play(Cc(2, 98, 0x30));
        Assert.Equal(58, (int)((ControlChangeEvent)Play(Cc(2, 6, 70))).ControlValue);
        Assert.Equal(70, XgPartParams.FileValue(2, BassGain));
    });

    [Fact]
    public void Detune_high_pass_and_mono_poly() => Run(() =>
    {
        // Detune: two nibbles (09: bits 7-4, 0A: bits 3-0).
        Play(Part(0x08, 1, 0x09, 0x08, 0x0C));
        Assert.Equal(0x8C, XgPartParams.FileValue(1, XgPartParams.Detune));
        XgPartParams.Set(null, 1, XgPartParams.Detune, 0x75);
        Assert.Equal(Part(0x08, 1, 0x09, 0x07, 0x05).Data, ((SysExEvent)Play(Part(0x08, 1, 0x09, 0x08, 0x0C))).Data);
        Assert.Equal(Part(0x08, 1, 0x09, 0x07, 0x05).Data, XgPartParams.ParamEvent(1, XgPartParams.Detune, 0x75).Data);

        // High pass filter: block 0A, and NRPN 01 24.
        Play(Part(0x0A, 1, 0x20, 0x50));
        Assert.Equal(0x50, XgPartParams.FileValue(1, XgPartParams.HighPass));
        Play(Cc(1, 99, 0x01));
        Play(Cc(1, 98, 0x24));
        Play(Cc(1, 6, 0x30));
        Assert.Equal(0x30, XgPartParams.FileValue(1, XgPartParams.HighPass));

        // Mono / poly: CC126 / CC127 replaced by the fixed mode.
        Play(Cc(1, 126, 0));
        Assert.Equal(0, XgPartParams.FileValue(1, XgPartParams.MonoPoly));
        XgPartParams.Set(null, 1, XgPartParams.MonoPoly, 1);
        Assert.Equal(127, (int)((ControlChangeEvent)Play(Cc(1, 126, 0))).ControlNumber);
        var poly = Cc(1, 127, 0);
        Assert.Same(poly, Play(poly));
    });

    [Fact]
    public void Bend_range_rpn() => Run(() =>
    {
        Play(Cc(5, 101, 0));
        Play(Cc(5, 100, 0));
        var entry = Cc(5, 6, 12);
        Assert.Same(entry, Play(entry));
        Assert.Equal(0x40 + 12, XgPartParams.FileValue(5, XgPartParams.BendRange));

        XgPartParams.Set(null, 5, XgPartParams.BendRange, 0x40 + 7);
        Assert.Equal(7, (int)((ControlChangeEvent)Play(Cc(5, 6, 12))).ControlValue);
        // Negative range: the data entry writes 0, the SysEx follows.
        XgPartParams.Set(null, 5, XgPartParams.BendRange, 0x40 - 2);
        var played = XgPartParams.FixDataEntry(Cc(5, 6, 12), new DataEntry(true, 0, 0), out var again);
        Assert.Equal(0, (int)((ControlChangeEvent)played).ControlValue);
        Assert.Equal(Part(0x08, 5, 0x23, 0x3E).Data, ((SysExEvent)again).Data);

        // Another RPN (fine tuning) and an NRPN selected afterwards are not the bend range.
        Play(Cc(5, 100, 1));
        var fine = Cc(5, 6, 70);
        Assert.Same(fine, Play(fine));
    });

    [Fact]
    public void Saved_file_has_the_fixed_values() => Run(() =>
    {
        var file = new MidiFile(new TrackChunk(
            new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7 }),
            Cc(4, 99, 0x01), Cc(4, 98, 0x30), Cc(4, 6, 70),
            Cc(4, 101, 0), Cc(4, 100, 0), Cc(4, 6, 2),
            new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { Channel = (FourBitNumber)4, DeltaTime = 480 }));
        XgPartParams.Set(null, 4, BassGain, 76);
        XgPartParams.Set(null, 4, XgPartParams.BendRange, 0x40 + 12);
        MidiFileEditor.WriteXgPartParams(file);
        var events = file.GetTrackChunks().Single().Events.ToList();
        Assert.Contains(events.OfType<SysExEvent>(), e => e.Data.SequenceEqual(XgPartParams.ParamEvent(4, BassGain, 76).Data));
        Assert.Equal(new[] { 76, 12 }, events.OfType<ControlChangeEvent>().Where(c => c.ControlNumber == 6).Select(c => (int)c.ControlValue));
    });
}
