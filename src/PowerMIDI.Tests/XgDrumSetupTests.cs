using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// XG drum setups (F0 43 1n 4C 3s rr pp vv F7, and NRPN 14..1F rr on the channel of a part assigned to one): read
// during playback, corrected with the values fixed in the Effects tab, and written in a saved file.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class XgDrumSetupTests
{
    const int Coarse = 0, Decay1 = 5, Decay2 = 6, Level = 7, Snare = 38;

    static NormalSysExEvent Setup(int s, int note, int address, params byte[] values) =>
        new(new byte[] { 0x43, 0x10, 0x4C, (byte)(0x30 + s), (byte)note, (byte)address }.Concat(values).Append((byte)0xF7).ToArray());
    static ControlChangeEvent Cc(int channel, int number, int value) =>
        new((SevenBitNumber)number, (SevenBitNumber)value) { Channel = (FourBitNumber)channel };
    static MidiEvent Play(MidiEvent e) => ChannelMixer.Callback(e, 0, TimeSpan.Zero);

    static void Run(Action test)
    {
        bool xg = ChannelMixer.XgMode;
        try
        {
            ChannelMixer.XgMode = true;
            ChannelMixer.ResetChannelState(true);
            XgDrumSetup.Clear(null);
            test();
        }
        finally
        {
            XgDrumSetup.Clear(null);
            XgDrumSetup.ClearPartMode(null);
            ChannelMixer.ResetChannelState(false);
            ChannelMixer.XgMode = xg;
        }
    }

    [Fact]
    public void SysEx_of_the_file_is_read_and_corrected() => Run(() =>
    {
        Assert.Equal(64, XgDrumSetup.FileValue(0, Snare, Coarse));
        Assert.Equal(-1, XgDrumSetup.FileValue(0, Snare, Level));       // depends on the note
        var message = Setup(0, Snare, 0x00, 0x43, 0x40, 0x50);           // coarse, fine, level in one message
        Assert.Same(message, Play(message));
        Assert.Equal((0x43, 0x50), (XgDrumSetup.FileValue(0, Snare, Coarse), XgDrumSetup.FileValue(0, Snare, Level)));

        XgDrumSetup.Set(null, 0, Snare, Level, 0x7F);
        var played = (SysExEvent)Play(message);
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x30, Snare, 0x00, 0x43, 0x40, 0x7F, 0xF7 }, played.Data);
        var other = Setup(1, Snare, 0x02, 0x50);
        Assert.Same(other, Play(other));                                 // another setup: unchanged
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x32, 0x24, 0x0E, 0x30, 0xF7 }, XgDrumSetup.ParamEvent(2, 36, Decay1, 0x30).Data);
    });

    [Fact]
    public void Nrpn_on_the_part_of_a_setup_is_read_and_corrected() => Run(() =>
    {
        Play(Cc(9, 99, 0x18));
        Play(Cc(9, 98, Snare));
        Assert.Same(Cc(9, 6, 70) is var entry ? entry : null, Play(entry));
        Assert.Equal(70, XgDrumSetup.FileValue(0, Snare, Coarse));

        XgDrumSetup.Set(null, 0, Snare, Coarse, 60);
        var played = (ControlChangeEvent)Play(Cc(9, 6, 70));
        Assert.Equal(60, (int)played.ControlValue);

        // An RPN selected afterwards: data entries are no longer drum setup values.
        Play(Cc(9, 101, 0));
        Play(Cc(9, 100, 0));
        var bend = Cc(9, 6, 12);
        Assert.Same(bend, Play(bend));

        // Channel 1 (normal part) has no drum setup.
        Play(Cc(0, 99, 0x18));
        Play(Cc(0, 98, Snare));
        var other = Cc(0, 6, 70);
        Assert.Same(other, Play(other));
    });

    [Fact]
    public void Decay_nrpn_writes_both_decays() => Run(() =>
    {
        XgDrumSetup.Set(null, 0, Snare, Decay2, 20);
        Play(Cc(9, 99, 0x17));
        Play(Cc(9, 98, Snare));
        var played = XgDrumSetup.ReadController(Cc(9, 6, 50), out var again);
        Assert.Equal(20, (int)((ControlChangeEvent)played).ControlValue);
        Assert.Equal(XgDrumSetup.ParamEvent(0, Snare, Decay1, 50).Data, ((SysExEvent)again).Data);   // decay 1: the file's
        Assert.Equal(50, XgDrumSetup.FileValue(0, Snare, Decay1));
    });

    [Fact]
    public void Part_mode_and_program_change_reset() => Run(() =>
    {
        Assert.Equal(0, XgDrumSetup.SetupOf(9));
        Assert.Equal(-1, XgDrumSetup.SetupOf(10));
        Play(new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x08, 10, 0x07, 0x03, 0xF7 }));   // part 11: DRUMS2
        Assert.Equal(1, XgDrumSetup.SetupOf(10));

        Play(Setup(1, Snare, 0x00, 0x50));
        Assert.Equal(-1, XgDrumSetup.ProgramChanged(10));                // reset, nothing fixed to send again
        Assert.Equal(64, XgDrumSetup.FileValue(1, Snare, Coarse));
        XgDrumSetup.Set(null, 1, Snare, Coarse, 70);
        Assert.Equal(1, XgDrumSetup.ProgramChanged(10));

        // Drum Setup Reset 00 00 7D 01.
        Play(Setup(1, Snare, 0x00, 0x50));
        Assert.Null(XgDrumSetup.Read(new byte[] { 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7D, 0x01, 0xF7 }, out int reset));
        Assert.Equal(1, reset);
        Assert.Equal(64, XgDrumSetup.FileValue(1, Snare, Coarse));
    });

    [Fact]
    public void Saved_file_has_the_fixed_values() => Run(() =>
    {
        var file = new MidiFile(new TrackChunk(
            new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7 }),     // XG System On
            new ProgramChangeEvent((SevenBitNumber)8) { Channel = (FourBitNumber)9, DeltaTime = 240 },
            Cc(9, 99, 0x18), Cc(9, 98, Snare), Cc(9, 6, 70),
            Setup(0, Snare, 0x02, 0x50),
            new NoteOnEvent((SevenBitNumber)Snare, (SevenBitNumber)100) { Channel = (FourBitNumber)9, DeltaTime = 480 }));
        XgDrumSetup.Set(null, 0, Snare, Coarse, 60);
        XgDrumSetup.Set(null, 0, Snare, Level, 100);
        MidiFileEditor.WriteXgDrumSetup(file);

        var events = file.GetTrackChunks().Single().Events.ToList();
        var fixedCoarse = XgDrumSetup.ParamEvent(0, Snare, Coarse, 60).Data;
        int pc = events.FindIndex(e => e is ProgramChangeEvent);
        Assert.Contains(events.Take(pc).OfType<SysExEvent>(), e => e.Data.SequenceEqual(fixedCoarse));   // after the reset
        Assert.Contains(events.Skip(pc).OfType<SysExEvent>(), e => e.Data.SequenceEqual(fixedCoarse));   // again after the Program Change
        Assert.Equal(60, events.OfType<ControlChangeEvent>().Single(c => c.ControlNumber == 6).ControlValue);
        Assert.Contains(events.OfType<SysExEvent>(), e => e.Data.SequenceEqual(Setup(0, Snare, 0x02, 100).Data));
    });

    [Fact]
    public void Part_mode_chosen_in_the_interface() => Run(() =>
    {
        XgDrumSetup.SetPartMode(null, 10, 3);                            // channel 11: drum setup 2
        XgDrumSetup.SetPartMode(null, 9, XgDrumSetup.Drum);              // channel 10: no setup
        Assert.Equal((1, -1), (XgDrumSetup.SetupOf(10), XgDrumSetup.SetupOf(9)));

        // The file's part mode is stored, and replaced in the message played.
        var played = (SysExEvent)Play(new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x08, 10, 0x07, 0x00, 0xF7 }));
        Assert.Equal(new byte[] { 0x43, 0x10, 0x4C, 0x08, 10, 0x07, 0x03, 0xF7 }, played.Data);
        Assert.Equal(0, XgDrumSetup.FilePartMode(10));
        XgDrumSetup.ClearPartMode(null, 10);
        Assert.Equal(-1, XgDrumSetup.SetupOf(10));

        // Saved file: the part mode after the reset, the file's one corrected.
        XgDrumSetup.SetPartMode(null, 10, 3);
        var file = new MidiFile(new TrackChunk(
            new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7 }),
            new NormalSysExEvent(new byte[] { 0x43, 0x10, 0x4C, 0x08, 10, 0x07, 0x00, 0xF7 }) { DeltaTime = 480 }));
        MidiFileEditor.WriteXgDrumSetup(file);
        var modes = file.GetTrackChunks().Single().Events.OfType<SysExEvent>().Where(e => e.Data.Length > 6 && e.Data[3] == 0x08 && e.Data[4] == 10).ToList();
        Assert.Equal(2, modes.Count);
        Assert.All(modes, e => Assert.Equal(3, e.Data[6]));
    });
}
