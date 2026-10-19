using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// XG Multi Part SysEx (F0 43 1n 4C 08 pp aa vv … F7): read like the bank selects, Program Change and controllers
// of the channel, and corrected with the values fixed in the interface.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class XgMultiPartTests
{
    const int Volume = 0, Pan = 1, Reverb = 2, Chorus = 3, Cutoff = 8;

    static NormalSysExEvent Part(int part, int address, params byte[] values) =>
        new(new byte[] { 0x43, 0x10, 0x4C, 0x08, (byte)part, (byte)address }.Concat(values).Append((byte)0xF7).ToArray());
    static byte[] Sent(MidiEvent e) => ((SysExEvent)e).Data;
    static MidiEvent Play(MidiEvent e) => ChannelMixer.Callback(e, 0, TimeSpan.Zero);

    static void Reset()
    {
        ChannelMixer.ResetChannelState(true);
        for (int c = 0; c < 16; c++)
        {
            ChannelMixer.ClearOverride(null, c, true);
            ChannelMixer.Muted[c] = false;
            for (int k = 0; k < ChannelMixer.ControllerNumbers.Length; k++) ChannelMixer.ClearController(null, k, c);
        }
    }

    [Fact]
    public void Values_of_the_file_are_read()
    {
        Reset();
        Assert.False(ChannelMixer.SysExPatch[13]);
        var message = Part(13, 0x01, 0x30, 0x08, 0x11);              // bank MSB, LSB and program in one message
        Assert.Same(message, Play(message));
        Assert.Equal((0x30, 0x08, 0x11), (ChannelMixer.BankMsb[13], ChannelMixer.BankLsb[13], ChannelMixer.Program[13]));
        Assert.True(ChannelMixer.SysExPatch[13] && ChannelMixer.Used[13]);
        Assert.False(ChannelMixer.SysExPatch[12]);

        Play(Part(13, 0x0B, 0x7A));
        Play(Part(13, 0x0E, 0x20));
        Play(Part(13, 0x12, 0x11, 0x22));                            // chorus send, reverb send
        Play(Part(13, 0x18, 0x50));
        Assert.Equal(0x7A, ChannelMixer.Value(Volume, 13));
        Assert.Equal(0x20, ChannelMixer.Value(Pan, 13));
        Assert.Equal(0x11, ChannelMixer.Value(Chorus, 13));
        Assert.Equal(0x22, ChannelMixer.Value(Reverb, 13));
        Assert.Equal(0x50, ChannelMixer.Value(Cutoff, 13));
        Assert.Equal(100, ChannelMixer.Value(Volume, 12));

        // Other addresses (part mode, note shift…) and parts above 16 are left alone.
        var mode = Part(10, 0x07, 0x03);
        Assert.Same(mode, Play(mode));
        var far = Part(25, 0x0B, 0x10);
        Assert.Same(far, Play(far));
        Assert.Equal(-1, ChannelMixer.MultiPartChannel(far.Data));

        ChannelMixer.ResetChannelState(true);
        Assert.False(ChannelMixer.SysExPatch[13]);
        Reset();
    }

    [Fact]
    public void Fixed_values_replace_the_ones_of_the_file()
    {
        Reset();
        ChannelMixer.SetController(null, Volume, 3, 90);
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x08, 0x03, 0x0B, 90, 0xF7 }, Sent(Play(Part(3, 0x0B, 0x40))));
        Assert.Equal(0x40, ChannelMixer.FileValue(Volume, 3));
        Assert.Equal(90, ChannelMixer.Value(Volume, 3));

        ChannelMixer.SetOverride(null, 3, 0, 0, 5);
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x08, 0x03, 0x01, 0, 0, 5, 0xF7 }, Sent(Play(Part(3, 0x01, 0x30, 0x08, 0x11))));
        Assert.Equal(0x11, ChannelMixer.Program[3]);                 // the file's is remembered

        // A muted channel: its volume is written as 0.
        ChannelMixer.Muted[4] = true;
        Assert.Equal(0, Sent(Play(Part(4, 0x0B, 0x64)))[6]);
        Reset();
    }

    [Fact]
    public void Saved_file_has_the_fixed_values()
    {
        var file = new MidiFile(new TrackChunk(
            new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7 }),
            Part(2, 0x01, 0x30, 0x08, 0x11),
            Part(2, 0x0B, 0x40),
            Part(2, 0x0E, 0x20),
            Part(5, 0x0B, 0x33),
            new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { Channel = (FourBitNumber)2, DeltaTime = 480 },
            new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { Channel = (FourBitNumber)2, DeltaTime = 480 }));

        int controllers = ChannelMixer.ControllerNumbers.Length;
        var applyCc = Enumerable.Range(0, controllers).Select(_ => new bool[16]).ToArray();
        var ccValues = Enumerable.Range(0, controllers).Select(_ => new int[16]).ToArray();
        (applyCc[Volume][2], ccValues[Volume][2]) = (true, 90);
        MidiFileEditor.ApplyChanges(file, new[] { (2, InstrumentPassages.First, 0, 0, 5) }, applyCc, ccValues, Enumerable.Repeat(-1, 16).ToArray());

        var parts = file.GetTrackChunks().SelectMany(c => c.Events).OfType<SysExEvent>().Select(e => e.Data).Where(d => d[3] == 0x08).ToList();
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x08, 0x02, 0x01, 0, 0, 5, 0xF7 }, parts[0]);
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x08, 0x02, 0x0B, 90, 0xF7 }, parts[1]);
        Assert.Equal(0x20, parts[2][6]);                             // the pan is not fixed
        Assert.Equal(0x33, parts[3][6]);                             // another part
        // The fixed values are also written once after the reset, as Program Change and controller.
        Assert.Contains(file.GetTrackChunks().SelectMany(c => c.Events).OfType<ProgramChangeEvent>(), p => p.Channel == 2 && p.ProgramNumber == 5);

    }
}
