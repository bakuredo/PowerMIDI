using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// Files repaired when read (MidiTools.ReadFile): SysEx with their F0 status again in their data, a MIDI file inside a
// RIFF file (.rmi) or after other bytes.
public class ReadRepairTests
{
    // One track: a GS Reset written F0 <length> F0 41 10 42 12 40 00 7F 00 41 F7 (as in Terranigma of the Sound Canvas
    // collection), a note, the end of the track.
    static byte[] Song()
    {
        var track = new List<byte>();
        track.AddRange(new byte[] { 0x00, 0xF0, 0x0B, 0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 });
        track.AddRange(new byte[] { 0x00, 0x90, 60, 100, 0x83, 0x60, 0x80, 60, 0, 0x00, 0xFF, 0x2F, 0x00 });
        var file = new List<byte>();
        file.AddRange("MThd"u8.ToArray());
        file.AddRange(new byte[] { 0, 0, 0, 6, 0, 0, 0, 1, 0x01, 0xE0 });
        file.AddRange("MTrk"u8.ToArray());
        file.AddRange(new byte[] { 0, 0, 0, (byte)track.Count });
        file.AddRange(track);
        return file.ToArray();
    }

    static void CheckRepaired(MidiFile midi)
    {
        var sysEx = midi.GetTrackChunks().SelectMany(c => c.Events).OfType<SysExEvent>().Single();
        Assert.Equal(new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 }, sysEx.Data);
        Assert.Equal("GS", MidiTools.GetStandard(midi));
        Assert.Equal(3, midi.GetTimedEvents().Count());   // copied by the playback library: no error any more
    }

    [Fact]
    public void Sysex_with_its_status_again_is_repaired()
    {
        var midi = MidiTools.ReadFile(new MemoryStream(Song()));
        CheckRepaired(midi);

        // Saved: the message without the extra F0.
        var path = Path.Combine(Path.GetTempPath(), $"powermidi-repair-{Guid.NewGuid():N}.mid");
        try
        {
            MidiTools.WriteFile(midi, path);
            var bytes = File.ReadAllBytes(path);
            Assert.Contains("F00A4110421240007F0041F7", Convert.ToHexString(bytes));
            CheckRepaired(MidiTools.ReadFile(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Midi_inside_a_riff_file_is_read()
    {
        var midi = Song();
        var riff = new List<byte>();
        riff.AddRange("RIFF"u8.ToArray());
        riff.AddRange(BitConverter.GetBytes(4 + 8 + midi.Length));
        riff.AddRange("RMIDdata"u8.ToArray());
        riff.AddRange(BitConverter.GetBytes(midi.Length));
        riff.AddRange(midi);
        CheckRepaired(MidiTools.ReadFile(new MemoryStream(riff.ToArray())));

        // Not a MIDI file at all: still refused.
        Assert.ThrowsAny<Exception>(() => MidiTools.ReadFile(new MemoryStream("not a MIDI file"u8.ToArray())));
    }
}
