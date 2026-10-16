using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.Tests;

// Changes of the Event Editor (MidiEventEdits, MidiEventForm): an event found by its track and place, the others keep
// their position; the fields of the edit panel give back an event, or say what is wrong.
public class MidiEventEditsTests
{
    static NoteOnEvent On(int note, long delta = 0) => new((SevenBitNumber)note, (SevenBitNumber)90) { DeltaTime = delta };
    static NoteOffEvent Off(int note, long delta) => new((SevenBitNumber)note, (SevenBitNumber)0) { DeltaTime = delta };

    // C3 0-480, volume at 240, E3 480-960; a second track with a tempo.
    static MidiFile File() => new(
        new TrackChunk(On(60), new ControlChangeEvent((SevenBitNumber)7, (SevenBitNumber)100) { DeltaTime = 240 }, Off(60, 240), On(64), Off(64, 480)),
        new TrackChunk(new SetTempoEvent(500000)))
    { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };

    static List<MidiEventRow> Rows(MidiFile f) => MidiEventList.Build(f, (c, p, m, l, r) => "");

    static string Track1(MidiFile f)
    {
        long t = 0;
        return string.Join(" ", f.GetTrackChunks().First().Events.Select(e => $"{t += e.DeltaTime}:{e switch {
            NoteOnEvent on => $"on{on.NoteNumber}/{on.Velocity}", NoteOffEvent off => $"off{off.NoteNumber}",
            ControlChangeEvent cc => $"cc{cc.ControlNumber}={cc.ControlValue}", _ => e.EventType.ToString() }}"));
    }

    [Fact]
    public void Rows_give_track_and_place_of_events_and_notes()
    {
        var rows = Rows(File());
        var c3 = rows.First(r => r.Type == "Note");
        Assert.Equal((1, 0, 2, 480L), (c3.Track, c3.Index, c3.OffIndex, c3.Length));
        Assert.Equal((1, 1), (rows.First(r => r.Type == "Control Change").Track, rows.First(r => r.Type == "Control Change").Index));
        Assert.Equal((2, 0), (rows.First(r => r.Type == "Tempo").Track, rows.First(r => r.Type == "Tempo").Index));
    }

    [Fact]
    public void Replace_set_note_delete_and_add()
    {
        var f = File();
        MidiEventEdits.Replace(f, 1, 1, MidiTools.Cc(0, 7, 50));
        Assert.Equal("0:on60/90 240:cc7=50 480:off60 480:on64/90 960:off64", Track1(f));

        MidiEventEdits.SetNote(f, 1, 0, 2, 0, 62, 70, 720);   // C3 -> D3, longer: its Note Off after the E3 Note On
        Assert.Equal("0:on62/70 240:cc7=50 480:on64/90 720:off62 960:off64", Track1(f));

        var e3 = Rows(f).Single(r => r.Data.StartsWith("E3"));
        MidiEventEdits.Delete(f, new[] { (e3.Track, e3.Index), (e3.Track, e3.OffIndex) });
        Assert.Equal("0:on62/70 240:cc7=50 720:off62", Track1(f));

        int i = MidiEventEdits.Add(f, 1, 240, MidiTools.Cc(0, 10, 30));   // after the volume at the same position
        Assert.Equal(2, i);
        int n = MidiEventEdits.AddNote(f, 1, 0, 0, 72, 100, 240);
        Assert.Equal("0:on62/70 0:on72/100 240:off72 240:cc7=50 240:cc10=30 720:off62", Track1(f));
        Assert.Equal(1, n);
        Assert.Single(f.GetTrackChunks().ElementAt(1).Events);   // other track untouched
    }

    [Fact]
    public void Form_of_events_and_back()
    {
        var rows = Rows(File());
        var note = MidiEventForm.Of(rows.First(r => r.Type == "Note"));
        Assert.Equal("Note", note.Kind);
        Assert.Equal(new[] { "60", "90", "480" }, note.Values);
        var result = MidiEventForm.Read(note, 3, new[] { "61", "80", "240" }, null);
        Assert.Equal((3, 61, 80, 240L), result.Note);

        var cc = MidiEventForm.Of(rows.First(r => r.Type == "Control Change"));
        Assert.Equal("Controller: 0 to 127", MidiEventForm.Read(cc, 0, new[] { "200", "1" }, null).Error);
        Assert.IsType<ControlChangeEvent>(MidiEventForm.Read(cc, 0, new[] { "10", "1" }, null).Event);

        var tempo = MidiEventForm.Of(rows.First(r => r.Type == "Tempo"));
        Assert.Equal("120", tempo.Values[0]);
        Assert.Equal(400000, ((SetTempoEvent)MidiEventForm.Read(tempo, 0, new[] { "150" }, null).Event).MicrosecondsPerQuarterNote);
    }

    [Fact]
    public void SysEx_and_text_fields()
    {
        var sysEx = MidiEventForm.New("SysEx", 0, 480);
        var gs = (SysExEvent)MidiEventForm.Read(sysEx, 0, Array.Empty<string>(), "F0 41 10 42 12 40 01 30 04 00").Event;
        Assert.Equal("41 10 42 12 40 01 30 04 0B", MidiEventList.Hex(gs.Data));   // F7 added, Roland checksum worked out
        Assert.Equal("Not a byte: ZZ", MidiEventForm.Read(sysEx, 0, Array.Empty<string>(), "41 ZZ").Error);

        var marker = (MarkerEvent)MidiEventForm.Read(MidiEventForm.New("Marker", 0, 480), 0, Array.Empty<string>(), "Boucle é").Event;
        Assert.Equal("Boucle é", MidiTools.DecodeText(marker.Text));   // UTF-8 in the file, read back
        Assert.NotNull(MidiEventForm.Of(new MidiEventRow(0, 0, 0, EventKind.Controller, "Control Change", "", "", MidiTools.Cc(0, 7, 1))).NotEditable);
    }
}
