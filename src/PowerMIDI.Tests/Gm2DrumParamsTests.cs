using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// GM2 Key-Based Instrument Control (F0 7F 7F 0A 01 0n kk nn vv … F7): read during playback, corrected with the values
// fixed in the Effects tab, and written in a saved file.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class Gm2DrumParamsTests
{
    const int Snare = 38;

    static NormalSysExEvent Kbic(int channel, int note, params byte[] pairs) =>
        new(new byte[] { 0x7F, 0x7F, 0x0A, 0x01, (byte)channel, (byte)note }.Concat(pairs).Append((byte)0xF7).ToArray());
    static MidiEvent Play(MidiEvent e) => ChannelMixer.Callback(e, 0, TimeSpan.Zero);

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
            Gm2DrumParams.Clear(null);
            ChannelMixer.ResetChannelState(false);
            ChannelMixer.Gm2Mode = gm2;
        }
    }

    [Fact]
    public void Messages_of_the_file_are_read_and_corrected() => Run(() =>
    {
        Assert.Equal(64, Gm2DrumParams.FileValue(9, Snare, Gm2DrumParams.Level));
        Assert.Equal(-1, Gm2DrumParams.FileValue(9, Snare, Gm2DrumParams.Pan));
        var message = Kbic(9, Snare, 0x07, 0x50, 0x0A, 0x20);           // level and pan in one message
        Assert.Same(message, Play(message));
        Assert.Equal((0x50, 0x20), (Gm2DrumParams.FileValue(9, Snare, Gm2DrumParams.Level), Gm2DrumParams.FileValue(9, Snare, Gm2DrumParams.Pan)));

        Gm2DrumParams.Set(null, 9, Snare, Gm2DrumParams.Pan, 0x60);
        Assert.Equal(Kbic(9, Snare, 0x07, 0x50, 0x0A, 0x60).Data, ((SysExEvent)Play(message)).Data);
        Assert.Equal(Kbic(9, Snare, 0x0A, 0x60).Data, Gm2DrumParams.ParamEvent(9, Snare, Gm2DrumParams.Pan, 0x60).Data);
        var other = Kbic(9, Snare + 1, 0x0A, 0x20);                     // another note: unchanged
        Assert.Same(other, Play(other));
    });

    [Fact]
    public void Saved_file_has_the_fixed_values() => Run(() =>
    {
        var file = new MidiFile(new TrackChunk(
            new NormalSysExEvent(new byte[] { 0x7E, 0x7F, 0x09, 0x03, 0xF7 }),                                 // GM2 System On
            new ProgramChangeEvent((SevenBitNumber)8) { Channel = (FourBitNumber)9, DeltaTime = 240 },
            Kbic(9, Snare, 0x5B, 0x10),
            new NoteOnEvent((SevenBitNumber)Snare, (SevenBitNumber)100) { Channel = (FourBitNumber)9, DeltaTime = 480 }));
        Gm2DrumParams.Set(null, 9, Snare, 2, 100);                      // reverb send
        MidiFileEditor.WriteGm2DrumParams(file);

        var events = file.GetTrackChunks().Single().Events.ToList();
        var fixedReverb = Gm2DrumParams.ParamEvent(9, Snare, 2, 100).Data;
        int pc = events.FindIndex(e => e is ProgramChangeEvent);
        Assert.Contains(events.Take(pc).OfType<SysExEvent>(), e => e.Data.SequenceEqual(fixedReverb));   // after the reset
        Assert.Equal(2, events.Skip(pc).OfType<SysExEvent>().Count(e => e.Data.SequenceEqual(fixedReverb)));   // after the kit + corrected
    });
}
