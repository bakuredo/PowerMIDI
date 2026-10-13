using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// XG voice map (MU Basic / MU100 Native): the file's choice is followed, unless one is chosen in the interface.
public class VoiceMapTests
{
    static NormalSysExEvent VoiceMapSysEx(int map) => new(new byte[] { 0x43, 0x10, 0x49, 0x00, 0x00, 0x12, (byte)map, 0xF7 });
    static byte Sent(MidiEvent e) => ((SysExEvent)e).Data[6];

    [Fact]
    public void File_choice_is_followed_then_replaced_by_the_interface_choice()
    {
        ChannelMixer.VoiceMapOverride = -1;
        ChannelMixer.ResetChannelState(true);
        Assert.Equal(ChannelMixer.VoiceMapNative, ChannelMixer.VoiceMap);              // after a reset

        var played = ChannelMixer.Callback(VoiceMapSysEx(ChannelMixer.VoiceMapBasic), 0, TimeSpan.Zero);
        Assert.Equal(ChannelMixer.VoiceMapBasic, Sent(played));                        // Auto: the file's message as it is
        Assert.Equal(ChannelMixer.VoiceMapBasic, ChannelMixer.VoiceMap);

        ChannelMixer.SetVoiceMap(null, ChannelMixer.VoiceMapNative);
        Assert.Equal(ChannelMixer.VoiceMapNative, ChannelMixer.VoiceMap);
        played = ChannelMixer.Callback(VoiceMapSysEx(ChannelMixer.VoiceMapBasic), 0, TimeSpan.Zero);
        Assert.Equal(ChannelMixer.VoiceMapNative, Sent(played));                       // the file's message is replaced

        ChannelMixer.SetVoiceMap(null, -1);
        Assert.Equal(ChannelMixer.VoiceMapBasic, ChannelMixer.VoiceMap);               // back to the file's
        ChannelMixer.ResetChannelState(true);
        Assert.Equal(ChannelMixer.VoiceMapNative, ChannelMixer.VoiceMap);
    }
}
