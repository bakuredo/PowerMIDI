using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// A .syx file next to a MIDI file (same name): its SysEx are put at the start of the file when it is read
// (MidiTools.AddSyxFile), the song shifted after them.
public class SyxFileTests
{
    // Two MT-32 messages (display text, a timbre byte) with some bytes between them, as some .syx files have, and a
    // message cut short before the second one (skipped, not joined to it).
    static readonly byte[] Syx =
    {
        0xF0, 0x41, 0x10, 0x16, 0x12, 0x20, 0x00, 0x00, 0x41, 0x42, 0x1D, 0xF7,
        0x00,
        0xF0, 0x41, 0x10, 0x16,
        0xF0, 0x41, 0x10, 0x16, 0x12, 0x08, 0x00, 0x00, 0x01, 0x77, 0xF7,
    };

    static MidiFile Song() => new(new TrackChunk(
        new SequenceTrackNameEvent("Song"),
        new MarkerEvent("loopStart"),
        new NoteOnEvent((Melanchall.DryWetMidi.Common.SevenBitNumber)60, (Melanchall.DryWetMidi.Common.SevenBitNumber)100),
        new NoteOffEvent((Melanchall.DryWetMidi.Common.SevenBitNumber)60, (Melanchall.DryWetMidi.Common.SevenBitNumber)0) { DeltaTime = 480 }))
        { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };

    [Fact]
    public void Syx_messages_are_put_before_the_song()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"powermidi-syx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var mid = Path.Combine(folder, "SONG.MID");
            MidiTools.WriteFile(Song(), mid);
            File.WriteAllBytes(Path.Combine(folder, "SONG.SYX"), Syx);
            Assert.Equal("SONG.SYX", Path.GetFileName(MidiTools.SyxFileOf(mid)));

            var midi = MidiTools.ReadFile(mid);
            var timed = midi.GetTimedEvents().ToList();
            var sysEx = timed.Where(t => t.Event is SysExEvent).ToList();
            Assert.Equal(2, sysEx.Count);
            Assert.Equal(new byte[] { 0x41, 0x10, 0x16, 0x12, 0x20, 0x00, 0x00, 0x41, 0x42, 0x1D, 0xF7 }, ((SysExEvent)sysEx[0].Event).Data);
            Assert.Equal(new byte[] { 0x41, 0x10, 0x16, 0x12, 0x08, 0x00, 0x00, 0x01, 0x77, 0xF7 }, ((SysExEvent)sysEx[1].Event).Data);
            Assert.Equal("SONG.SYX", Path.GetFileName(MidiTools.AddedSyxFile(mid)));
            Assert.Equal("MT-32", MidiTools.GetStandard(midi));
            // The timbre byte redefines a sound (status line: custom patches), the display text does not.
            Assert.False(Mt32Patches.IsCustomPatch(((SysExEvent)sysEx[0].Event).Data));
            Assert.True(Mt32Patches.IsCustomPatch(((SysExEvent)sysEx[1].Event).Data));

            // The track name stays at 0; the marker (song time) and the note come after the messages, spaced by the time
            // the module needs (a few tens of ms each).
            Assert.Equal(0, timed.Single(t => t.Event is SequenceTrackNameEvent).Time);
            long note = timed.Single(t => t.Event is NoteOnEvent).Time;
            Assert.True(sysEx[1].Time > 0 && note > sysEx[1].Time);
            Assert.Equal(note, timed.Single(t => t.Event is MarkerEvent).Time);
            Assert.Equal(480, timed.Single(t => t.Event is NoteOffEvent).Time - note);

            // Saved with them, then read again with the .syx still there: not added twice.
            MidiTools.WriteFile(midi, mid);
            Assert.Equal(2, MidiTools.ReadFile(mid).GetTimedEvents().Count(t => t.Event is SysExEvent));
            Assert.Null(MidiTools.AddedSyxFile(mid));   // status line: no .syx named any more

            // A .syx that cannot be read (locked by another program): the file is read without it.
            MidiTools.WriteFile(Song(), mid);
            using (new FileStream(Path.Combine(folder, "SONG.SYX"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Equal(0, MidiTools.ReadFile(mid).GetTimedEvents().Count(t => t.Event is SysExEvent));
                Assert.Null(MidiTools.AddedSyxFile(mid));
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    // A GS .syx (reverb macro, part 1 level) for a file starting with its GS Reset: the reset stays first (otherwise it
    // would clear the settings of the .syx), the messages after it; the file is GS.
    [Fact]
    public void Gs_syx_comes_after_the_reset_of_the_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"powermidi-syx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var song = Song();
            var gsReset = new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 };
            song.GetTrackChunks().First().Events.Insert(0, new NormalSysExEvent(gsReset));
            var mid = Path.Combine(folder, "gs.mid");
            MidiTools.WriteFile(song, mid);
            File.WriteAllBytes(Path.Combine(folder, "gs.syx"), new byte[]
            {
                0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x01, 0x30, 0x04, 0x0B, 0xF7,
                0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x11, 0x19, 0x64, 0x32, 0xF7,
            });

            var midi = MidiTools.ReadFile(mid);
            var sysEx = midi.GetTimedEvents().Where(t => t.Event is SysExEvent).ToList();
            Assert.Equal(3, sysEx.Count);
            Assert.Equal(gsReset, ((SysExEvent)sysEx[0].Event).Data);
            Assert.Equal(0, sysEx[0].Time);
            Assert.True(sysEx[1].Time > 0);   // after the module restarted
            Assert.Equal("GS", MidiTools.GetStandard(midi));
        }
        finally { Directory.Delete(folder, true); }
    }
}
