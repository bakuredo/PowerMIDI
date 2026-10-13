using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// GM On of a file whose track is played as GS or XG (GS / XG SysEx without a reset of that mode): replaced by the
// reset of that mode, so that the module keeps reading the bank selects.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class GmOnTests
{
    static readonly byte[] GmOn = { 0x7E, 0x7F, 0x09, 0x01, 0xF7 };
    static readonly byte[] Gm2On = { 0x7E, 0x7F, 0x09, 0x03, 0xF7 };
    static byte[] Played(byte[] reset) => ((SysExEvent)ChannelMixer.Callback(new NormalSysExEvent(reset), 0, TimeSpan.Zero)).Data;

    [Fact]
    public void Gm_on_becomes_the_reset_of_the_mode_of_the_track()
    {
        try
        {
            ChannelMixer.GsMode = ChannelMixer.XgMode = false;
            Assert.Equal(GmOn, Played(GmOn));                                       // GM track: as it is

            ChannelMixer.GsMode = true;
            Assert.Equal(new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 }, Played(GmOn));
            Assert.Equal(0, ChannelMixer.BankMsb[9]);
            Assert.Equal(Gm2On, Played(Gm2On));                                     // only the GM (level 1) reset

            ChannelMixer.GsMode = false;
            ChannelMixer.XgMode = true;
            Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7 }, Played(GmOn));
            Assert.Equal(127, ChannelMixer.BankMsb[9]);                             // state after an XG reset

            // Mode forced in the interface: the resets of the file are dropped.
            ChannelMixer.ForceMode = true;
            Assert.Null(ChannelMixer.Callback(new NormalSysExEvent(GmOn), 0, TimeSpan.Zero));
        }
        finally
        {
            ChannelMixer.ForceMode = ChannelMixer.GsMode = ChannelMixer.XgMode = false;
            ChannelMixer.ResetChannelState(false);
        }
    }
}
