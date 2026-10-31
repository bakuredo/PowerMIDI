using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// A controller value fixed in the Mixer replaces the file's until the first change of that controller in the music (its
// first passage, see ControllerPassages), or in the whole track: during playback, in the saved file and in the Event Editor.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class ControllerPassagesTests
{
    const int Channel = 2, Volume = 0;

    static MidiEvent Cc(int number, int value, long delta = 0) =>
        new ControlChangeEvent((SevenBitNumber)number, (SevenBitNumber)value) { Channel = (FourBitNumber)Channel, DeltaTime = delta };
    static MidiEvent On(long delta = 0) => new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { Channel = (FourBitNumber)Channel, DeltaTime = delta };
    static MidiEvent Off(long delta) => new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { Channel = (FourBitNumber)Channel, DeltaTime = delta };
    static readonly byte[] GsReset = { 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 };

    // Tick 0: volume 90 before the first note, and 95 at its tick after it (both setting the channel up); tick 480: fade
    // 80, 70; tick 1920: GS Reset, volume 100, a note, volume 60 at 2400.
    static MidiFile Song() => new(new TrackChunk(
        Cc(7, 90), On(), Cc(7, 95), Off(480),
        Cc(7, 80), Cc(7, 70, 480), On(), Off(480),
        new NormalSysExEvent(GsReset) { DeltaTime = 480 }, Cc(7, 100), On(), Off(480), Cc(7, 60)))
        { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };

    [Fact]
    public void First_change_after_the_first_note_ends_the_first_passage()
    {
        var passages = ControllerPassages.Read(Song());
        Assert.True(passages.InFirst(Volume, Channel, 0));
        Assert.True(passages.InFirst(Volume, Channel, 479));
        Assert.False(passages.InFirst(Volume, Channel, 480));
        Assert.False(passages.InFirst(Volume, Channel, 1919));
        Assert.True(passages.InFirst(Volume, Channel, 1920));    // a reset: first passage again
        Assert.False(passages.InFirst(Volume, Channel, 2400));
        Assert.True(passages.InFirst(1, Channel, 5000));          // pan: never changed after notes
        // Forced mode (resets ignored): no first passage again.
        Assert.False(ControllerPassages.Read(Song(), false).InFirst(Volume, Channel, 1920));
    }

    [Fact]
    public void Played_until_the_first_change_or_in_the_whole_track()
    {
        ChannelMixer.ResetChannelState(false);
        ChannelMixer.CcPassages = ControllerPassages.Read(Song());
        int Played(int value, long tick) => ((ControlChangeEvent)ChannelMixer.Callback(Cc(7, value), tick, TimeSpan.Zero)).ControlValue;
        try
        {
            ChannelMixer.SetController(null, Volume, Channel, 50);
            Assert.False(ChannelMixer.CcWholeTrack[Volume, Channel]);
            Assert.Equal(50, Played(90, 0));
            Assert.Equal(50, Played(95, 0));
            Assert.Equal(80, Played(80, 480));            // the fade of the file
            Assert.Equal(80, ChannelMixer.Value(Volume, Channel));
            Assert.True(ChannelMixer.IsPastFirst(Volume, Channel));

            // Set past the first passage: for the whole track.
            ChannelMixer.SetController(null, Volume, Channel, 40);
            Assert.True(ChannelMixer.CcWholeTrack[Volume, Channel]);
            Assert.Equal(40, Played(70, 960));

            // Back to the first passage only: the file's values again past it.
            ChannelMixer.SetWholeTrack(null, Volume, Channel, false);
            Assert.Equal(70, Played(70, 960));
            ChannelMixer.ResetChannelState(false);          // jump back to the start
            Assert.Equal(40, Played(90, 0));
        }
        finally
        {
            ChannelMixer.ClearController(null, Volume, Channel);
            ChannelMixer.CcPassages = null;
            ChannelMixer.ResetChannelState(false);
        }
    }

    [Fact]
    public void Fixed_again_at_a_loop_restart_in_the_first_passage()
    {
        var device = new Recorder();
        ChannelMixer.ResetChannelState(false);
        ChannelMixer.CcPassages = ControllerPassages.Read(Song());
        try
        {
            ChannelMixer.SetController(null, Volume, Channel, 50);
            ChannelMixer.Callback(Cc(7, 80), 480, TimeSpan.Zero);   // past the first passage
            Assert.Equal(80, ChannelMixer.Value(Volume, Channel));
            // The loop starts again at tick 100, without the events before it: the fixed value is in force again, and sent.
            ChannelMixer.EnterPassagesAt(device, 100);
            Assert.Equal(50, ChannelMixer.Value(Volume, Channel));
            Assert.Contains(device.Sent, e => e is ControlChangeEvent cc && cc.ControlNumber == 7 && cc.ControlValue == 50);
        }
        finally
        {
            ChannelMixer.ClearController(null, Volume, Channel);
            ChannelMixer.CcPassages = null;
            ChannelMixer.ResetChannelState(false);
        }
    }

    sealed class Recorder : Melanchall.DryWetMidi.Multimedia.IOutputDevice
    {
        public readonly List<MidiEvent> Sent = new();
        public event EventHandler<Melanchall.DryWetMidi.Multimedia.MidiEventSentEventArgs> EventSent { add { } remove { } }
        public void PrepareForEventsSending() { }
        public void SendEvent(MidiEvent midiEvent) => Sent.Add(midiEvent);
        public void Dispose() { }
    }

    static List<(long, int)> Volumes(MidiFile file) =>
        file.GetTimedEvents().Where(t => t.Event is ControlChangeEvent cc && cc.ControlNumber == 7 && cc.Channel == Channel)
            .Select(t => (t.Time, (int)((ControlChangeEvent)t.Event).ControlValue)).ToList();

    static void Save(MidiFile file, bool wholeTrack)
    {
        int controllers = ChannelMixer.ControllerNumbers.Length;
        var applyCc = Enumerable.Range(0, controllers).Select(_ => new bool[16]).ToArray();
        var ccValues = Enumerable.Range(0, controllers).Select(_ => new int[16]).ToArray();
        var whole = Enumerable.Range(0, controllers).Select(_ => new bool[16]).ToArray();
        applyCc[Volume][Channel] = true;
        ccValues[Volume][Channel] = 50;
        whole[Volume][Channel] = wholeTrack;
        MidiFileEditor.ApplyChanges(file, Array.Empty<(int, long, int, int, int)>(), applyCc, ccValues, Enumerable.Repeat(-1, 16).ToArray(), true, whole);
    }

    [Fact]
    public void Saved_until_the_first_change()
    {
        var file = Song();
        Save(file, false);
        // The values of the first passages are removed, the fixed one written at the start and after the reset (at the
        // note there: never after it); the fade and the later change stay.
        Assert.Equal(new[] { (0L, 50), (480L, 80), (960L, 70), (1920L, 50), (2400L, 60) }, Volumes(file));

        file = Song();
        Save(file, true);
        Assert.Equal(new[] { (0L, 50), (1920L, 50) }, Volumes(file));
    }

    [Fact]
    public void Blue_in_the_event_list_until_the_first_change()
    {
        var rows = MidiEventList.Build(Song(), (c, p, m, l, r) => "");
        ChannelMixer.ResetChannelState(false);
        ChannelMixer.CcPassages = ControllerPassages.Read(Song());
        try
        {
            ChannelMixer.SetController(null, Volume, Channel, 50);
            var blue = rows.Where(r => MidiEventList.FixedText(r) != null).Select(r => (r.Tick, r.Data)).ToList();
            Assert.Equal(new[] { (0L, "7 = 90"), (0L, "7 = 95"), (1920L, "7 = 100") }, blue);
        }
        finally
        {
            ChannelMixer.ClearController(null, Volume, Channel);
            ChannelMixer.CcPassages = null;
            ChannelMixer.ResetChannelState(false);
        }
    }
}
