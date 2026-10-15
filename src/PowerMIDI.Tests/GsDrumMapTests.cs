using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// GS drum maps (DT1 41 mp rr vv, NRPN 18..1F rr on the channel of a drum part): read during playback, corrected with
// the values fixed in the Effects tab, and written in a saved file.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class GsDrumMapTests
{
    const int Snare = 38;

    static NormalSysExEvent Map(int m, int p, int note, params byte[] values)
    {
        var d = new byte[] { 0x41, 0x10, 0x42, 0x12, 0x41, (byte)(m << 4 | p), (byte)note }.Concat(values).Concat(new byte[] { 0, 0xF7 }).ToArray();
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
            GsDrumMap.Clear(null);
            ChannelMixer.ResetChannelState(false);
            ChannelMixer.GsMode = gs;
        }
    }

    [Fact]
    public void SysEx_of_the_file_is_read_and_corrected() => Run(() =>
    {
        Assert.Equal(64, GsDrumMap.FileValue(0, Snare, GsDrumMap.PitchCoarse));
        Assert.Equal(-1, GsDrumMap.FileValue(0, Snare, 1));             // level: depends on the note
        // Play note 41 for the snare (pitch +3) and the next note, then the level of the snare.
        var pitch = Map(0, 1, Snare, 41, 39);
        Assert.Same(pitch, Play(pitch));
        Assert.Equal((67, 64), (GsDrumMap.FileValue(0, Snare, GsDrumMap.PitchCoarse), GsDrumMap.FileValue(0, Snare + 1, GsDrumMap.PitchCoarse)));
        Play(Map(0, 2, Snare, 100));
        Assert.Equal(100, GsDrumMap.FileValue(0, Snare, 1));

        GsDrumMap.Set(null, 0, Snare, GsDrumMap.PitchCoarse, 60);       // -4: plays note 34
        Assert.Equal(Map(0, 1, Snare, 34, 39).Data, ((SysExEvent)Play(pitch)).Data);
        Assert.Equal(Map(0, 1, Snare, 34).Data, GsDrumMap.ParamEvent(0, Snare, GsDrumMap.PitchCoarse, 60).Data);
        var other = Map(1, 1, Snare, 41);                                // map 2: unchanged
        Assert.Same(other, Play(other));
    });

    [Fact]
    public void Nrpn_on_a_drum_part_and_drum_set_change() => Run(() =>
    {
        Play(Cc(9, 99, 0x1A));
        Play(Cc(9, 98, Snare));
        var entry = Cc(9, 6, 90);
        Assert.Same(entry, Play(entry));
        Assert.Equal(90, GsDrumMap.FileValue(0, Snare, 1));
        GsDrumMap.Set(null, 0, Snare, 1, 30);
        Assert.Equal(30, (int)((ControlChangeEvent)Play(Cc(9, 6, 90))).ControlValue);

        Play(Cc(0, 99, 0x1A));                                           // channel 1: normal part
        Play(Cc(0, 98, Snare));
        var normal = Cc(0, 6, 90);
        Assert.Same(normal, Play(normal));

        Assert.Equal(0, GsDrumMap.ProgramChanged(9));                   // new drum set: map initialized, fixed values to send
        Assert.Equal(-1, GsDrumMap.FileValue(0, Snare, 1));
        Assert.Equal(-1, GsDrumMap.ProgramChanged(0));
    });

    [Fact]
    public void Saved_file_has_the_fixed_values() => Run(() =>
    {
        var file = new MidiFile(new TrackChunk(
            new NormalSysExEvent(new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 }),   // GS Reset
            new ProgramChangeEvent((SevenBitNumber)8) { Channel = (FourBitNumber)9, DeltaTime = 240 },
            Cc(9, 99, 0x1C), Cc(9, 98, Snare), Cc(9, 6, 20),
            Map(0, 4, Snare, 20),
            new NoteOnEvent((SevenBitNumber)Snare, (SevenBitNumber)100) { Channel = (FourBitNumber)9, DeltaTime = 480 }));
        GsDrumMap.Set(null, 0, Snare, GsDrumMap.Pan, 100);
        MidiFileEditor.WriteGsDrumMap(file);

        var events = file.GetTrackChunks().Single().Events.ToList();
        var fixedPan = GsDrumMap.ParamEvent(0, Snare, GsDrumMap.Pan, 100).Data;
        int pc = events.FindIndex(e => e is ProgramChangeEvent);
        Assert.Contains(events.Take(pc).OfType<SysExEvent>(), e => e.Data.SequenceEqual(fixedPan));     // after the reset
        Assert.Contains(events.Skip(pc).OfType<SysExEvent>(), e => e.Data.SequenceEqual(fixedPan));     // after the drum set change
        Assert.Equal(100, events.OfType<ControlChangeEvent>().Single(c => c.ControlNumber == 6).ControlValue);
        Assert.DoesNotContain(events.OfType<SysExEvent>(), e => e.Data.SequenceEqual(Map(0, 4, Snare, 20).Data));
    });
}
