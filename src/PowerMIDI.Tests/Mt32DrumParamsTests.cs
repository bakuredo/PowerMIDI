using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// MT-32 rhythm setup (03 01 10 + 4 × (key - 24): timbre, level, pan, reverb): read during playback, corrected with the
// values fixed in the Effects tab, and written in a saved file.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class Mt32DrumParamsTests
{
    const int Snare = 38;
    const int RhythmTemp = (0x03 << 14) | (0x01 << 7) | 0x10;

    static NormalSysExEvent Setup(int note, params byte[] data) => Mt32Effects.SysEx(RhythmTemp + 4 * (note - 24), data);
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
            Mt32DrumParams.Clear(null);
            Mt32Effects.Clear(null, Mt32Effects.RhythmParam);
            ChannelMixer.ResetChannelState(false);
            ChannelMixer.Mt32Mode = mt32;
        }
    }

    [Fact]
    public void SysEx_of_the_file_is_read_and_corrected() => Run(() =>
    {
        Assert.Equal(-1, Mt32DrumParams.FileValue(Snare, Mt32DrumParams.Level));        // the module's map
        // Snare: R2 (Acou SD), level 90, pan 7, reverb on; then the next note's timbre (one message across two notes).
        var message = Setup(Snare, 65, 90, 7, 1, 64);
        Assert.Same(message, Play(message));
        Assert.Equal((65, 90, 7, 1), (Mt32DrumParams.FileValue(Snare, 0), Mt32DrumParams.FileValue(Snare, 1),
                                       Mt32DrumParams.FileValue(Snare, 2), Mt32DrumParams.FileValue(Snare, 3)));
        Assert.Equal(64, Mt32DrumParams.FileValue(Snare + 1, Mt32DrumParams.Timbre));

        Mt32DrumParams.Set(null, Snare, Mt32DrumParams.Pan, 0);
        Assert.Equal(Setup(Snare, 65, 90, 0, 1, 64).Data, ((SysExEvent)Play(message)).Data);
        Assert.Equal(Setup(Snare + 4, 0, 0, 0, 0).Data.Take(7), Mt32DrumParams.ParamEvent(Snare + 4, 0, 3).Data.Take(7));   // address
    });

    [Fact]
    public void Saved_file_has_the_fixed_values() => Run(() =>
    {
        var file = new MidiFile(new TrackChunk(
            Mt32Effects.SysEx(0x7F << 14, 0x00),                         // MT-32 reset
            Setup(Snare, 65, 90, 7, 1),
            new NoteOnEvent((SevenBitNumber)Snare, (SevenBitNumber)100) { Channel = (FourBitNumber)9, DeltaTime = 480 }));
        Mt32DrumParams.Set(null, Snare, Mt32DrumParams.Level, 50);
        MidiFileEditor.WriteMt32DrumParams(file);
        var sysex = file.GetTrackChunks().Single().Events.OfType<SysExEvent>().Select(e => e.Data).ToList();
        Assert.Contains(sysex, d => d.SequenceEqual(Mt32DrumParams.ParamEvent(Snare, Mt32DrumParams.Level, 50).Data));   // after the reset
        Assert.Contains(sysex, d => d.SequenceEqual(Setup(Snare, 65, 50, 7, 1).Data));                                 // corrected
    });
}
