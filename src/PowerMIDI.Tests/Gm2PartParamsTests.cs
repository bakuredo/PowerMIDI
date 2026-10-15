using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// GM2 part parameters (RPN: pitch bend range, fine / coarse tuning, modulation depth range): read during playback,
// corrected with the values fixed in the Effects tab, and written in a saved file.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class Gm2PartParamsTests
{
    static ControlChangeEvent Cc(int channel, int number, int value) =>
        new((SevenBitNumber)number, (SevenBitNumber)value) { Channel = (FourBitNumber)channel };
    static MidiEvent Play(MidiEvent e) => ChannelMixer.Callback(e, 0, TimeSpan.Zero);
    static (int, int) Pair(MidiEvent e) => ((int)((ControlChangeEvent)e).ControlNumber, ((ControlChangeEvent)e).ControlValue);

    static void Run(Action test)
    {
        bool gm2 = ChannelMixer.Gm2Mode;
        try
        {
            ChannelMixer.Gm2Mode = true;
            ChannelMixer.ResetChannelState(false);
            test();
        }
        finally
        {
            Gm2PartParams.Clear(null);
            ChannelMixer.ResetChannelState(false);
            ChannelMixer.Gm2Mode = gm2;
        }
    }

    [Fact]
    public void Rpn_of_the_file_are_read_and_corrected() => Run(() =>
    {
        Assert.Equal((2, 64), (Gm2PartParams.FileValue(4, Gm2PartParams.BendRange), Gm2PartParams.FileValue(4, Gm2PartParams.ModDepthRange)));
        Play(Cc(4, 101, 0));
        Play(Cc(4, 100, 0));
        var bend = Cc(4, 6, 12);
        Assert.Same(bend, Play(bend));
        Assert.Equal(12, Gm2PartParams.FileValue(4, Gm2PartParams.BendRange));
        Gm2PartParams.Set(null, 4, Gm2PartParams.BendRange, 5);
        Assert.Equal((6, 5), Pair(Play(Cc(4, 6, 12))));

        // Modulation depth range: MSB and LSB (1 semitone + 32/128).
        Play(Cc(4, 100, 5));
        Play(Cc(4, 6, 1));
        Play(Cc(4, 38, 32));
        Assert.Equal(1 * 128 + 32, Gm2PartParams.FileValue(4, Gm2PartParams.ModDepthRange));
        Gm2PartParams.Set(null, 4, Gm2PartParams.ModDepthRange, 0 * 128 + 100);
        Assert.Equal((6, 0), Pair(Play(Cc(4, 6, 1))));
        Assert.Equal((38, 100), Pair(Play(Cc(4, 38, 32))));
        var other = Cc(5, 6, 1);                                         // channel 6: no RPN selected
        Assert.Same(other, Play(other));
    });

    [Fact]
    public void Fixed_value_messages_and_saved_file() => Run(() =>
    {
        Assert.Equal(new[] { (101, 0), (100, 2), (6, 0x46), (101, 127), (100, 127) },
                     Gm2PartParams.ParamEvents(3, Gm2PartParams.CoarseTune, 0x46).Select(Pair));
        Assert.Equal(new[] { (101, 0), (100, 5), (6, 1), (38, 0), (101, 127), (100, 127) },
                     Gm2PartParams.ParamEvents(3, Gm2PartParams.ModDepthRange, 128).Select(Pair));

        var file = new MidiFile(new TrackChunk(
            new NormalSysExEvent(new byte[] { 0x7E, 0x7F, 0x09, 0x03, 0xF7 }),                                 // GM2 System On
            Cc(3, 101, 0), Cc(3, 100, 0), Cc(3, 6, 2),
            new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { Channel = (FourBitNumber)3, DeltaTime = 480 }));
        Gm2PartParams.Set(null, 3, Gm2PartParams.BendRange, 12);
        MidiFileEditor.WriteGm2PartParams(file);
        var entries = file.GetTrackChunks().Single().Events.OfType<ControlChangeEvent>().Where(c => c.ControlNumber == 6).Select(c => (int)c.ControlValue);
        Assert.Equal(new[] { 12, 12 }, entries);                         // the file's corrected, the fixed one after the reset
    });
}
