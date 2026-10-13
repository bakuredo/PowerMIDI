using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// XG Multi EQ (SysEx F0 43 1n 4C 02 40 aa vv F7): values read from the file, messages sent.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class XgMultiEqTests
{
    const int Eq = GsXgEffects.XgEqFirst;
    const int Gain1 = Eq + 1, Frequency1 = Eq + 2, Gain2 = Eq + 5, Frequency5 = Eq + 15, Shape5 = Eq + 17;

    static NormalSysExEvent SysEx(params byte[] data) => new(new byte[] { 0x43, 0x10, 0x4C }.Concat(data).Append((byte)0xF7).ToArray());
    static byte[] Sent(MidiEvent e) => ((SysExEvent)e).Data;
    static MidiEvent Play(MidiEvent e) => ChannelMixer.Callback(e, 0, TimeSpan.Zero);

    static void Reset()
    {
        ChannelMixer.ResetChannelState(true);
        for (int e = 0; e < GsXgEffects.Count; e++) GsXgEffects.Clear(null, e);
    }

    [Fact]
    public void Layout()
    {
        Assert.Equal(GsXgEffects.Count, GsXgEffects.Addresses.Length);
        foreach (var table in new[] { GsXgEffects.AddressHi, GsXgEffects.Block, GsXgEffects.Min, GsXgEffects.Max, GsXgEffects.Defaults })
            Assert.Equal(GsXgEffects.Count, table.Length);
        Assert.Equal(GsXgEffects.Count, GsXgEffects.TypeDependent.Length);
        Assert.Equal(GsXgEffects.Count, Eq + GsXgEffects.XgEqSize);
        Assert.True(GsXgEffects.IsType(Eq));
        Assert.True(GsXgEffects.IsXg(Shape5));
        Assert.False(GsXgEffects.IsInsertion(Eq));
        Assert.Equal(0x14, GsXgEffects.Addresses[Shape5]);
        Assert.Equal(GsXgEffects.XgEqBlock, GsXgEffects.Block[Shape5]);
        for (int e = Eq; e < GsXgEffects.Count; e++)
            Assert.InRange(GsXgEffects.Defaults[e], GsXgEffects.Min[e], GsXgEffects.Max[e]);
    }

    [Fact]
    public void Values_of_the_file_are_read_and_fixed_values_replace_them()
    {
        Reset();
        Assert.Equal(0, GsXgEffects.Value(Eq));                      // after a reset: flat
        Assert.Equal(64, GsXgEffects.Value(Gain1));
        Assert.Equal(0x34, GsXgEffects.Value(Frequency5));

        Play(SysEx(0x02, 0x40, 0x01, 0x46, 0x10));                   // band 1: gain +6 dB, frequency
        Play(SysEx(0x02, 0x40, 0x12, 0x30));                         // band 5: frequency
        Assert.Equal(0x46, GsXgEffects.Value(Gain1));
        Assert.Equal(0x10, GsXgEffects.Value(Frequency1));
        Assert.Equal(0x30, GsXgEffects.Value(Frequency5));
        Assert.Equal(64, GsXgEffects.Value(Gain2));

        // The same address in the area of the other effects (02 01) is another parameter: the reverb return.
        Play(SysEx(0x02, 0x01, 0x0C, 0x20));
        Assert.Equal(0x20, GsXgEffects.Value(25));
        Assert.Equal(0x46, GsXgEffects.Value(Gain1));

        GsXgEffects.Set(null, Gain1, 0x40);
        Assert.Equal(0x40, Sent(Play(SysEx(0x02, 0x40, 0x01, 0x4C)))[6]);
        Assert.Equal(0x4C, GsXgEffects.FileValue(Gain1));

        Play(SysEx(0x02, 0x40, 0x00, 0x03));                         // a type sets the bands
        Assert.Equal(3, GsXgEffects.Value(Eq));
        Assert.Equal(-1, GsXgEffects.Value(Frequency5));
        Assert.Equal(0x40, GsXgEffects.Value(Gain1));                // still fixed
        Reset();
    }

    [Fact]
    public void Messages()
    {
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x02, 0x40, 0x00, 0x02, 0xF7 }, Sent(GsXgEffects.ParamEvent(Eq, 2)));
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x02, 0x40, 0x14, 0x01, 0xF7 }, Sent(GsXgEffects.ParamEvent(Shape5, 1)));
        // The other XG effects keep their area (02 01).
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x02, 0x01, 0x0C, 0x40, 0xF7 }, Sent(GsXgEffects.ParamEvent(25, 0x40)));
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x02, 0x01, 0x00, 0x01, 0x00, 0xF7 }, Sent(GsXgEffects.ParamEvent(24, 128)));
    }
}
