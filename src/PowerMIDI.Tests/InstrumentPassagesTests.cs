using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// An instrument chosen in the Mixer applies to the passage being played, from a Program Change of the file to the next
// one: during playback, in the saved file and in the Event Editor.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class InstrumentPassagesTests
{
    const int Channel = 2;

    sealed class Recorder : IOutputDevice
    {
        public readonly List<MidiEvent> Sent = new();
        public event EventHandler<MidiEventSentEventArgs> EventSent { add { } remove { } }
        public void PrepareForEventsSending() { }
        public void SendEvent(MidiEvent midiEvent) => Sent.Add(midiEvent);
        public void Dispose() { }
    }

    static MidiEvent Cc(int number, int value, long delta = 0) =>
        new ControlChangeEvent((SevenBitNumber)number, (SevenBitNumber)value) { Channel = (FourBitNumber)Channel, DeltaTime = delta };
    static MidiEvent Pc(int program, long delta = 0) => new ProgramChangeEvent((SevenBitNumber)program) { Channel = (FourBitNumber)Channel, DeltaTime = delta };
    static MidiEvent On(long delta = 0) => new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { Channel = (FourBitNumber)Channel, DeltaTime = delta };
    static MidiEvent Off(long delta) => new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { Channel = (FourBitNumber)Channel, DeltaTime = delta };

    // GS Reset; tick 0: bank 8 / 0, program 1, then again program 1 before the first note (same passage); tick 960:
    // program 2 (bank 8 still); tick 1920: bank 0, program 3.
    static MidiFile Song() => new(new TrackChunk(
        new NormalSysExEvent(new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 }),
        Cc(0, 8), Cc(32, 0), Pc(1), Pc(1), On(), Off(480),
        Pc(2, 480), On(), Off(480),
        Cc(0, 0, 480), Pc(3), On(), Off(480)))
        { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };

    [Fact]
    public void Program_changes_after_notes_start_passages()
    {
        var passages = InstrumentPassages.Read(Song());
        Assert.Equal(InstrumentPassages.First, passages.At(Channel, 0));
        Assert.Equal(InstrumentPassages.First, passages.At(Channel, 959));
        Assert.Equal(960, passages.At(Channel, 960));
        Assert.Equal(1920, passages.At(Channel, 5000));
        // Bank selects: the passage of their Program Change; the second Program Change before the first note: the first passage.
        Assert.Equal(InstrumentPassages.First, passages.Of(0, 1)?.Passage);
        Assert.Equal(InstrumentPassages.First, passages.Of(0, 4)?.Passage);
        Assert.Equal(new InstrumentPassages.Info(Channel, 960, InstrumentPassages.First, 8, 0), passages.Of(0, 7));
        Assert.Equal(1920, passages.Of(0, 10)?.Passage);   // bank select of the last one
        Assert.Null(passages.Of(0, 6));                     // a note
    }

    [Fact]
    public void Played_only_in_its_passage()
    {
        var device = new Recorder();
        ChannelMixer.ResetChannelState(false);
        ChannelMixer.Device = device;
        ChannelMixer.Passages = InstrumentPassages.Read(Song());
        try
        {
            // Playing the second passage: an instrument is chosen for it.
            ChannelMixer.Callback(Cc(0, 8), 0, TimeSpan.Zero);
            ChannelMixer.Callback(Pc(1), 0, TimeSpan.Zero);
            ChannelMixer.Callback(Pc(2), 960, TimeSpan.Zero);
            ChannelMixer.SetOverride(null, Channel, 16, 0, 50);
            Assert.Equal(960, ChannelMixer.Passage[Channel]);

            // From the start again: the file's instrument, then the one chosen (with its bank), then the file's again
            // (with the file's bank: the module has the one chosen).
            ChannelMixer.ResetChannelState(false);
            device.Sent.Clear();
            Assert.Equal(1, ((ProgramChangeEvent)ChannelMixer.Callback(Pc(1), 0, TimeSpan.Zero)).ProgramNumber);
            Assert.False(ChannelMixer.Override[Channel]);
            Assert.Equal(50, ((ProgramChangeEvent)ChannelMixer.Callback(Pc(2), 960, TimeSpan.Zero)).ProgramNumber);
            Assert.True(ChannelMixer.Override[Channel]);
            Assert.Equal(new[] { (0, 16), (32, 0) }, device.Sent.OfType<ControlChangeEvent>().Select(c => ((int)c.ControlNumber, (int)c.ControlValue)));
            device.Sent.Clear();
            Assert.Equal(3, ((ProgramChangeEvent)ChannelMixer.Callback(Pc(3), 1920, TimeSpan.Zero)).ProgramNumber);
            Assert.False(ChannelMixer.Override[Channel]);
            Assert.Equal(new[] { (0, 0), (32, 0) }, device.Sent.OfType<ControlChangeEvent>().Select(c => ((int)c.ControlNumber, (int)c.ControlValue)));
        }
        finally
        {
            ChannelMixer.ClearOverride(null, Channel, true);
            ChannelMixer.Device = null;
            ChannelMixer.Passages = null;
            ChannelMixer.ResetChannelState(false);
        }
    }

    [Fact]
    public void Saved_in_its_passage_only()
    {
        var file = Song();
        int controllers = ChannelMixer.ControllerNumbers.Length;
        var applyCc = Enumerable.Range(0, controllers).Select(_ => new bool[16]).ToArray();
        var ccValues = Enumerable.Range(0, controllers).Select(_ => new int[16]).ToArray();
        MidiFileEditor.ApplyChanges(file, new[] { (Channel, 960L, 16, 0, 50) }, applyCc, ccValues, Enumerable.Repeat(-1, 16).ToArray());

        var events = file.GetTimedEvents().Where(t => t.Event is ProgramChangeEvent or ControlChangeEvent)
                         .Select(t => (t.Time, t.Event is ProgramChangeEvent p ? $"PC {p.ProgramNumber}" : $"CC{((ControlChangeEvent)t.Event).ControlNumber} {((ControlChangeEvent)t.Event).ControlValue}"))
                         .ToList();
        Assert.Equal(new[]
        {
            (0L, "CC0 8"), (0L, "CC32 0"), (0L, "PC 1"), (0L, "PC 1"),       // first passage: the file's
            (960L, "CC0 16"), (960L, "CC32 0"), (960L, "PC 50"),             // chosen
            (1920L, "CC0 0"), (1920L, "CC32 0"), (1920L, "PC 3"),            // the file's, with its whole bank
        }, events);
    }

    [Fact]
    public void First_passage_replaced_in_place()
    {
        var file = Song();
        int controllers = ChannelMixer.ControllerNumbers.Length;
        var applyCc = Enumerable.Range(0, controllers).Select(_ => new bool[16]).ToArray();
        var ccValues = Enumerable.Range(0, controllers).Select(_ => new int[16]).ToArray();
        MidiFileEditor.ApplyChanges(file, new[] { (Channel, InstrumentPassages.First, 0, 0, 40) }, applyCc, ccValues, Enumerable.Repeat(-1, 16).ToArray());

        var programs = file.GetTimedEvents().Where(t => t.Event is ProgramChangeEvent)
                           .Select(t => (t.Time, (int)((ProgramChangeEvent)t.Event).ProgramNumber)).ToList();
        Assert.Equal(new[] { (0L, 40), (0L, 40), (960L, 2), (1920L, 3) }, programs);
        // The second passage gets the file's bank again (8 / 0).
        Assert.Contains(file.GetTimedEvents(), t => t.Time == 960 && t.Event is ControlChangeEvent cc && cc.ControlNumber == 0 && cc.ControlValue == 8);
    }

    [Fact]
    public void Blue_in_the_event_list_in_its_passage_only()
    {
        var rows = MidiEventList.Build(Song(), (c, p, m, l, r) => "");
        ChannelMixer.ResetChannelState(false);
        ChannelMixer.Passages = InstrumentPassages.Read(Song());
        try
        {
            ChannelMixer.Callback(Pc(2), 960, TimeSpan.Zero);
            ChannelMixer.SetOverride(null, Channel, 16, 0, 50);
            var blue = rows.Where(r => MidiEventList.FixedText(r) != null).Select(r => (r.Tick, r.Type)).ToList();
            Assert.Equal(new[] { (960L, "Program Change") }, blue);
        }
        finally
        {
            ChannelMixer.ClearOverride(null, Channel, true);
            ChannelMixer.Passages = null;
            ChannelMixer.ResetChannelState(false);
        }
    }
}
