using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// XG insertion effects 1 to 4 (SysEx F0 43 1n 4C 03 0x aa vv … F7): values read from the file, messages sent.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class XgInsertionTests
{
    static readonly InstrumentDefinitions Defs = InstrumentDefinitions.Load(Path.Combine(AppContext.BaseDirectory, "instruments"));

    const int Ins1 = GsXgEffects.InsFirst, Ins2 = Ins1 + GsXgEffects.InsSize, Ins4 = Ins1 + 3 * GsXgEffects.InsSize;
    const int Part = 1, Param1 = 2, Param10 = 11, Param11 = 12;   // offsets in an insertion effect
    const int OverDrive = 0x4A * 128, DelayLcr = 0x05 * 128;

    static NormalSysExEvent SysEx(params byte[] data) => new(new byte[] { 0x43, 0x10, 0x4C }.Concat(data).Append((byte)0xF7).ToArray());
    static byte[] Sent(MidiEvent e) => ((SysExEvent)e).Data;
    static MidiEvent Play(MidiEvent e) => ChannelMixer.Callback(e, 0, TimeSpan.Zero);

    static void Reset()
    {
        ChannelMixer.ResetChannelState(true);
        for (int e = 0; e < GsXgEffects.Count; e++) GsXgEffects.Clear(null, e);
    }

    [Fact]
    public void Layout_of_the_four_effects()
    {
        Assert.Equal(GsXgEffects.Count, GsXgEffects.Addresses.Length);
        foreach (var table in new[] { GsXgEffects.AddressHi, GsXgEffects.Block, GsXgEffects.Min, GsXgEffects.Max, GsXgEffects.Defaults })
            Assert.Equal(GsXgEffects.Count, table.Length);
        Assert.Equal(GsXgEffects.Count, GsXgEffects.TypeDependent.Length);
        for (int n = 0; n < GsXgEffects.InsCount; n++)
        {
            int type = Ins1 + n * GsXgEffects.InsSize;
            Assert.True(GsXgEffects.IsType(type));
            Assert.True(GsXgEffects.IsXg(type));
            Assert.Equal(GsXgEffects.InsBlockFirst + n, GsXgEffects.Block[type + Param11]);
            Assert.Equal(type, GsXgEffects.InsertionType(type + Param10));
        }
        Assert.False(GsXgEffects.IsInsertion(Ins1 - 1));
    }

    [Fact]
    public void Values_of_the_file_are_read()
    {
        Reset();
        Assert.Equal(0x49 * 128, GsXgEffects.Value(Ins1));           // after a reset: Distortion, on no part
        Assert.Equal(127, GsXgEffects.Value(Ins1 + Part));
        Assert.Equal(-1, GsXgEffects.Value(Ins1 + Param1));

        Play(SysEx(0x03, 0x00, 0x00, 0x4A, 0x00));                   // Insertion 1: Over Drive
        Play(SysEx(0x03, 0x00, 0x02, 0x10, 0x16));                   // parameters 1 and 2 in one message
        Play(SysEx(0x03, 0x00, 0x0B, 0x52));                         // parameter 10
        Play(SysEx(0x03, 0x00, 0x0C, 0x09));                         // part 10
        Play(SysEx(0x03, 0x00, 0x20, 0x33));                         // parameter 11
        Assert.Equal(OverDrive, GsXgEffects.Value(Ins1));
        Assert.Equal(0x10, GsXgEffects.Value(Ins1 + Param1));
        Assert.Equal(0x16, GsXgEffects.Value(Ins1 + Param1 + 1));
        Assert.Equal(0x52, GsXgEffects.Value(Ins1 + Param10));
        Assert.Equal(9, GsXgEffects.Value(Ins1 + Part));
        Assert.Equal(0x33, GsXgEffects.Value(Ins1 + Param11));
        Assert.Equal(-1, GsXgEffects.Value(Ins2 + Param1));          // the other effects are untouched

        // Another effect, and the 2-byte form of the parameters 1 to 10 (delay types).
        Play(SysEx(0x03, 0x03, 0x00, 0x05, 0x00));
        Play(SysEx(0x03, 0x03, 0x30, 0x1A, 0x05));                   // parameter 1 = 1A 05
        Play(SysEx(0x03, 0x03, 0x42, 0x00, 0x40));                   // parameter 10
        Assert.Equal(DelayLcr, GsXgEffects.Value(Ins4));
        Assert.Equal(0x1A * 128 + 0x05, GsXgEffects.Value(Ins4 + Param1));
        Assert.Equal(0x40, GsXgEffects.Value(Ins4 + Param10));

        // A new type: its parameters are the type's again.
        Play(SysEx(0x03, 0x00, 0x00, 0x49, 0x00));
        Assert.Equal(-1, GsXgEffects.Value(Ins1 + Param1));
        Assert.Equal(9, GsXgEffects.Value(Ins1 + Part));             // the part does not depend on the type
        Reset();
    }

    [Fact]
    public void Messages_follow_the_type()
    {
        Reset();
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x03, 0x01, 0x00, 0x4A, 0x00, 0xF7 }, Sent(GsXgEffects.ParamEvent(Ins2, OverDrive)));
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x03, 0x01, 0x0C, 0x05, 0xF7 }, Sent(GsXgEffects.ParamEvent(Ins2 + Part, 5)));
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x03, 0x01, 0x21, 0x07, 0xF7 }, Sent(GsXgEffects.ParamEvent(Ins2 + Param11 + 1, 7)));
        // Parameters 1 to 10: 1 byte at 02..0B, or MSB + LSB at 30, 32 … 42 for the types that need it.
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x03, 0x01, 0x03, 0x28, 0xF7 }, Sent(GsXgEffects.ParamEvent(Ins2 + Param1 + 1, 0x28)));
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x03, 0x01, 0x32, 0x1A, 0x05, 0xF7 },
                     Sent(GsXgEffects.ParamEvent(Ins2 + Param1 + 1, 0x1A * 128 + 0x05, DelayLcr)));
        GsXgEffects.Set(null, Ins2, DelayLcr);                       // the current type is the default
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x03, 0x01, 0x42, 0x00, 0x40, 0xF7 }, Sent(GsXgEffects.ParamEvent(Ins2 + Param10, 0x40)));
        Reset();
    }

    [Fact]
    public void A_fixed_value_replaces_the_one_of_the_file()
    {
        Reset();
        GsXgEffects.Set(null, Ins1 + Part, 3);
        var played = Play(SysEx(0x03, 0x00, 0x0C, 0x09));
        Assert.Equal(3, Sent(played)[6]);
        Assert.Equal(3, GsXgEffects.Value(Ins1 + Part));
        Assert.Equal(9, GsXgEffects.FileValue(Ins1 + Part));
        GsXgEffects.Set(null, Ins1 + Param1, 0x22);
        Assert.Equal(0x22, Sent(Play(SysEx(0x03, 0x00, 0x02, 0x10)))[6]);
        Reset();
    }

    // The types whose parameters 1 to 10 are 2 bytes are those with such a value in the definition file.
    [Fact]
    public void Two_byte_types_match_the_definitions()
    {
        Assert.NotEmpty(Defs.XgVariationTypes);
        foreach (var (code, type) in Defs.XgVariationTypes)
        {
            bool wide = type.Params.Any(p => p.Key >= 0x42 && p.Key <= 0x54 && p.Value.Max > 127);
            Assert.True(wide == GsXgEffects.InsertionNeedsMsb(code), $"{type.Name} ({code >> 7:X2} {code & 0x7F:X2})");
        }
    }
}
