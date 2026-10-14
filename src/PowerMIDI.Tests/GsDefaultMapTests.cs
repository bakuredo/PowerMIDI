using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// GS default map (TONE MAP-0 NUMBER, DT1 40 4x 01): map of the sounds whose bank LSB is 0.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class GsDefaultMapTests
{
    static readonly InstrumentDefinitions Defs = InstrumentDefinitions.Load(Path.Combine(AppContext.BaseDirectory, "instruments"));

    // Roland DT1 message 40 4x 01 mm for part block x, with its checksum.
    static NormalSysExEvent Map0(int block, int map)
    {
        var data = new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, (byte)(0x40 | block), 0x01, (byte)map, 0, 0xF7 };
        MidiTools.FixRolandChecksum(data);
        return new NormalSysExEvent(data);
    }
    static MidiEvent Play(MidiEvent e) => ChannelMixer.Callback(e, 0, TimeSpan.Zero);

    [Fact]
    public void File_choice_is_followed_then_replaced_by_the_interface_choice()
    {
        try
        {
            ChannelMixer.GsMode = true;
            ChannelMixer.GsDefaultMapOverride = -1;
            ChannelMixer.ResetChannelState(false);
            Assert.Equal(InstrumentDefinitions.DefaultGsMap, ChannelMixer.GsDefaultMap);   // SC-8850 map after a reset

            var fromFile = Map0(1, 1);                                   // part 1: SC-55 map
            Assert.Same(fromFile, Play(fromFile));
            Assert.Equal(1, ChannelMixer.GsDefaultMap);

            ChannelMixer.SetGsDefaultMap(null, 3);                       // SC-88Pro map chosen in the interface
            Assert.Equal(3, ChannelMixer.GsDefaultMap);
            Assert.Equal(Map0(1, 3).Data, ((SysExEvent)Play(Map0(1, 2))).Data);   // replaced, checksum included

            ChannelMixer.SetGsDefaultMap(null, -1);
            Assert.Equal(2, ChannelMixer.GsDefaultMap);                  // the file's last one
            ChannelMixer.ResetChannelState(false);
            Assert.Equal(InstrumentDefinitions.DefaultGsMap, ChannelMixer.GsDefaultMap);
        }
        finally
        {
            ChannelMixer.GsMode = false;
            ChannelMixer.GsDefaultMapOverride = -1;
            ChannelMixer.ResetChannelState(false);
        }
    }

    [Fact]
    public void Names_and_lists_of_bank_lsb_0_follow_the_default_map()
    {
        try
        {
            ChannelMixer.GsDefaultMapOverride = -1;
            ChannelMixer.ResetChannelState(false);
            var sc8850 = Defs.Resolve("GS", 0, 0, 0, 0, 0);
            Assert.Contains("SC-8850", sc8850.Detail);
            ChannelMixer.GsDefaultMapOverride = 1;
            var sc55 = Defs.Resolve("GS", 0, 0, 0, 0, 0);
            Assert.Contains("SC-55", sc55.Detail);
            Assert.Equal(Defs.GetChoices("GS", false, 1).Count, Defs.GetChoices("GS", false, 0).Count);
            Assert.Contains("SC-88Pro", Defs.Resolve("GS", 0, 0, 0, 3, 0).Detail);   // an explicit map is kept
        }
        finally { ChannelMixer.GsDefaultMapOverride = -1; }
    }
}
