using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// MT-32 part parameters (patch temporary area 03 00 xx, partial reserve 10 00 04..0C): read during playback, corrected
// with the values fixed in the Effects tab, sent again after a Program Change, and written in a saved file.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class Mt32PartParamsTests
{
    static NormalSysExEvent Dt1(int a1, int a2, int a3, params byte[] data) => Mt32Effects.SysEx(a1 << 14 | a2 << 7 | a3, data);
    static MidiEvent Play(MidiEvent e) => ChannelMixer.Callback(e, 0, TimeSpan.Zero);

    static void Run(Action test)
    {
        bool mt32 = ChannelMixer.Mt32Mode;
        try
        {
            ChannelMixer.Mt32Mode = true;
            ChannelMixer.ResetChannelState(false);
            test();
        }
        finally
        {
            Mt32PartParams.Clear(null);
            ChannelMixer.ResetChannelState(false);
            ChannelMixer.Mt32Mode = mt32;
        }
    }

    [Fact]
    public void SysEx_of_the_file_is_read_and_corrected() => Run(() =>
    {
        Assert.Equal(-1, Mt32PartParams.FileValue(1, Mt32PartParams.KeyShift));       // the patch's
        Assert.Equal(10, Mt32PartParams.FileValue(1, Mt32PartParams.PartialReserve));  // factory reserve
        Assert.Equal(32, Mt32PartParams.ReserveTotal);

        // Part 2: key shift +5, fine tune 0, bender range 12 in one message (03 00 12..14).
        var patch = Dt1(0x03, 0x00, 0x12, 29, 50, 12);
        Assert.Same(patch, Play(patch));
        Assert.Equal((29, 50, 12), (Mt32PartParams.FileValue(1, 0), Mt32PartParams.FileValue(1, 1), Mt32PartParams.FileValue(1, 2)));

        Mt32PartParams.Set(null, 1, Mt32PartParams.BenderRange, 2);
        Assert.Equal(Dt1(0x03, 0x00, 0x12, 29, 50, 2).Data, ((SysExEvent)Play(patch)).Data);

        // Partial reserve of the 9 parts in one message.
        Play(Dt1(0x10, 0x00, 0x04, 4, 4, 4, 4, 4, 4, 2, 0, 6));
        Assert.Equal(4, Mt32PartParams.FileValue(0, Mt32PartParams.PartialReserve));
        Assert.Equal(6, Mt32PartParams.FileValue(Mt32PartParams.RhythmPart, Mt32PartParams.PartialReserve));
    });

    [Fact]
    public void Program_change_loads_the_patch() => Run(() =>
    {
        Play(Dt1(0x03, 0x00, 0x02, 30));                                 // part 1 (channel 2): key shift +6
        Assert.Equal(30, Mt32PartParams.FileValue(0, Mt32PartParams.KeyShift));
        Assert.Equal(-1, Mt32PartParams.ProgramChanged(1));             // channel 2: part 1, nothing fixed
        Assert.Equal(-1, Mt32PartParams.FileValue(0, Mt32PartParams.KeyShift));
        Mt32PartParams.Set(null, 0, Mt32PartParams.KeyShift, 20);
        Mt32PartParams.Set(null, 0, Mt32PartParams.PartialReserve, 8);
        Assert.Equal(0, Mt32PartParams.ProgramChanged(1));
        Assert.Equal(new[] { Dt1(0x03, 0x00, 0x02, 20).Data }, Mt32PartParams.FixedPatchEvents(0).Select(e => ((SysExEvent)e).Data));
    });

    [Fact]
    public void Saved_file_has_the_fixed_values() => Run(() =>
    {
        var file = new MidiFile(new TrackChunk(
            Dt1(0x7F, 0x00, 0x00, 0x00),                                 // MT-32 reset
            new ProgramChangeEvent((SevenBitNumber)8) { Channel = (FourBitNumber)1, DeltaTime = 240 },
            new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { Channel = (FourBitNumber)1, DeltaTime = 480 }));
        Mt32PartParams.Set(null, 0, Mt32PartParams.FineTune, 60);
        MidiFileEditor.WriteMt32PartParams(file);
        var events = file.GetTrackChunks().Single().Events.ToList();
        int pc = events.FindIndex(e => e is ProgramChangeEvent);
        var fine = Dt1(0x03, 0x00, 0x03, 60).Data;
        Assert.Contains(events.Take(pc).OfType<SysExEvent>(), e => e.Data.SequenceEqual(fine));        // after the reset
        Assert.Contains(events.Skip(pc).OfType<SysExEvent>(), e => e.Data.SequenceEqual(fine));        // after the Program Change
    });
}
