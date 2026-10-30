using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// SysEx changing a sound of the module (status line: "Custom SysEx patches detected"), MidiTools.IsCustomSoundSysEx.
public class CustomSoundTests
{
    static byte[] Xg(int hi, int mid, int lo, params byte[] data) =>
        new byte[] { 0x43, 0x10, 0x4C, (byte)hi, (byte)mid, (byte)lo }.Concat(data).Append((byte)0xF7).ToArray();

    static byte[] Gs(int a1, int a2, int a3, params byte[] data)
    {
        var d = new byte[] { 0x41, 0x10, 0x42, 0x12, (byte)a1, (byte)a2, (byte)a3 }.Concat(data).Concat(new byte[] { 0, 0xF7 }).ToArray();
        MidiTools.FixRolandChecksum(d);
        return d;
    }

    [Fact]
    public void Xg_drum_setups_and_part_tones_are_custom_sounds()
    {
        Assert.True(MidiTools.IsCustomSoundSysEx(Xg(0x30, 36, 0x00, 70), "XG"));    // drum setup 1, note 36: pitch
        Assert.True(MidiTools.IsCustomSoundSysEx(Xg(0x08, 0, 0x18, 80), "XG"));     // part 1: filter cutoff
        Assert.True(MidiTools.IsCustomSoundSysEx(Xg(0x08, 0, 0x13, 1, 2, 3), "XG"));   // 13-15: up to vibrato rate
        Assert.False(MidiTools.IsCustomSoundSysEx(Xg(0x08, 0, 0x03, 48), "XG"));    // voice chosen: a preset sound
        Assert.False(MidiTools.IsCustomSoundSysEx(Xg(0x08, 0, 0x0B, 100), "XG"));   // volume
        Assert.False(MidiTools.IsCustomSoundSysEx(Xg(0x02, 1, 0x00, 1, 0), "XG"));  // reverb type
        Assert.False(MidiTools.IsCustomSoundSysEx(Xg(0x30, 36, 0x00, 70), "GS"));   // XG message in GS
    }

    [Fact]
    public void Gs_drum_maps_and_tone_modify_are_custom_sounds()
    {
        Assert.True(MidiTools.IsCustomSoundSysEx(Gs(0x41, 0x01, 36, 70), "GS"));    // drum map 1, note 36: pitch
        Assert.True(MidiTools.IsCustomSoundSysEx(Gs(0x40, 0x11, 0x32, 80), "GS"));  // part 1: TVF cutoff
        Assert.False(MidiTools.IsCustomSoundSysEx(Gs(0x40, 0x11, 0x19, 100), "GS")); // part level
        Assert.False(MidiTools.IsCustomSoundSysEx(Gs(0x40, 0x01, 0x30, 4), "GS"));  // reverb macro
    }

    [Fact]
    public void Rosenoelfee_has_custom_sounds()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Rosenoelfee.mid");
        if (!File.Exists(path)) return;   // test file of the repository folder, not committed
        var midi = MidiTools.ReadFile(path);
        Assert.Contains(midi.GetTrackChunks().SelectMany(c => c.Events).OfType<SysExEvent>(),
                        e => MidiTools.IsCustomSoundSysEx(e.Data, "XG"));
    }
}
