using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// A Portamento Time fixed in the Mixer turns the portamento on (CC65; off when fixed at 0), during playback and in a
// saved file.
[Collection("ChannelMixer")]   // shared static state: not in parallel with the other tests that use it
public class PortamentoTests
{
    const int Channel = 3;

    // Output port that keeps what it receives.
    sealed class Recorder : IOutputDevice
    {
        public readonly List<MidiEvent> Sent = new();
        public event EventHandler<MidiEventSentEventArgs> EventSent { add { } remove { } }
        public void PrepareForEventsSending() { }
        public void SendEvent(MidiEvent midiEvent) => Sent.Add(midiEvent);
        public void Dispose() { }
    }

    static ControlChangeEvent Cc(int number, int value) => new((SevenBitNumber)number, (SevenBitNumber)value) { Channel = (FourBitNumber)Channel };
    static int Value(MidiEvent e) => ((ControlChangeEvent)e).ControlValue;

    [Fact]
    public void Fixed_time_turns_the_portamento_on_until_it_is_cleared()
    {
        try
        {
            ChannelMixer.ResetChannelState(false);
            var port = new Recorder();
            Assert.Equal(0, Value(ChannelMixer.Callback(Cc(65, 0), 0, TimeSpan.Zero)));      // nothing fixed: the file's

            ChannelMixer.SetController(port, ChannelMixer.PortamentoTime, Channel, 40);
            Assert.Equal(new[] { (5, 40), (65, 127) }, port.Sent.Cast<ControlChangeEvent>().Select(c => ((int)c.ControlNumber, (int)c.ControlValue)));
            Assert.Equal(127, Value(ChannelMixer.Callback(Cc(65, 0), 0, TimeSpan.Zero)));    // the file's "off" is replaced

            port.Sent.Clear();
            ChannelMixer.ClearController(port, ChannelMixer.PortamentoTime, Channel);
            Assert.Equal(new[] { (5, 0), (65, 0) }, port.Sent.Cast<ControlChangeEvent>().Select(c => ((int)c.ControlNumber, (int)c.ControlValue)));
        }
        finally
        {
            ChannelMixer.ClearController(null, ChannelMixer.PortamentoTime, Channel);
            ChannelMixer.ResetChannelState(false);
        }
    }

    [Fact]
    public void Saved_file_has_the_portamento_on()
    {
        var file = new MidiFile(new TrackChunk(
            new NormalSysExEvent(new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 }),
            Cc(65, 0),
            new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { Channel = (FourBitNumber)Channel, DeltaTime = 480 },
            new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { Channel = (FourBitNumber)Channel, DeltaTime = 480 }));
        int controllers = ChannelMixer.ControllerNumbers.Length;
        var applyCc = Enumerable.Range(0, controllers).Select(_ => new bool[16]).ToArray();
        var ccValues = Enumerable.Range(0, controllers).Select(_ => new int[16]).ToArray();
        (applyCc[ChannelMixer.PortamentoTime][Channel], ccValues[ChannelMixer.PortamentoTime][Channel]) = (true, 40);
        MidiFileEditor.ApplyChanges(file, new bool[16], new int[16], new int[16], new int[16], applyCc, ccValues, Enumerable.Repeat(-1, 16).ToArray());

        var ccs = file.GetTrackChunks().SelectMany(c => c.Events).OfType<ControlChangeEvent>().Select(c => ((int)c.ControlNumber, (int)c.ControlValue)).ToList();
        Assert.Contains((5, 40), ccs);
        Assert.Contains((65, 127), ccs);
        Assert.DoesNotContain((65, 0), ccs);                                                  // the file's "off" is removed
    }

    [Fact]
    public void Time_fixed_at_0_turns_the_portamento_off()
    {
        try
        {
            ChannelMixer.ResetChannelState(false);
            var port = new Recorder();
            ChannelMixer.SetController(port, ChannelMixer.PortamentoTime, Channel, 0);
            Assert.Equal(new[] { (5, 0), (65, 0) }, port.Sent.Cast<ControlChangeEvent>().Select(c => ((int)c.ControlNumber, (int)c.ControlValue)));
            Assert.Equal(0, Value(ChannelMixer.Callback(Cc(65, 127), 0, TimeSpan.Zero)));      // the file's "on" is replaced
        }
        finally
        {
            ChannelMixer.ClearController(null, ChannelMixer.PortamentoTime, Channel);
            ChannelMixer.ResetChannelState(false);
        }
    }
}
