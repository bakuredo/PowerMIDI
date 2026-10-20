using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;
using PowerMIDI.Library;

namespace PowerMIDI.Tests;

// Database of a collection (Library Builder): titles, folders with their counts and display names, entries, search.
public class LibraryTests
{
    [Theory]
    [InlineData("Zelda_-_Dungeon.mid", "Zelda - Dungeon")]
    [InlineData("(KoD)_Boss__2.MID", "(KoD) Boss 2")]
    [InlineData("_.mid", "_.mid")]
    public void Title_of_a_file(string name, string title) => Assert.Equal(title, LibraryEntry.TitleOf(name));

    static void Song(string path, params MidiEvent[] events)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var all = events.Concat(new MidiEvent[]
        {
            new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { Channel = (FourBitNumber)9 },
            new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { Channel = (FourBitNumber)9, DeltaTime = 960 },
        });
        MidiTools.WriteFile(new MidiFile(new TrackChunk(all.ToArray())) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) }, path);
    }

    [Fact]
    public void Collection_written_and_read_back()
    {
        var root = Path.Combine(Path.GetTempPath(), "powermidi-tests", "library-" + Guid.NewGuid().ToString("N"));
        var database = root + ".library.db";
        try
        {
            var gsReset = new NormalSysExEvent(new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 });
            Song(Path.Combine(root, "console", "nintendo", "snes", "Zelda_-_Dungeon.mid"), new SequenceTrackNameEvent("Dungeon\0"), gsReset);
            Song(Path.Combine(root, "console", "nintendo", "snes", "Copy.mid"), new SequenceTrackNameEvent("Dungeon\0"), gsReset);
            Song(Path.Combine(root, "other", "Song.kar"), new TextEvent("@KMIDI KARAOKE FILE"), new TextEvent("Hél"), new TextEvent("lo"));
            File.WriteAllText(Path.Combine(root, "other", "Broken.mid"), "not a MIDI file");
            File.WriteAllText(Path.Combine(root, "other", "readme.txt"), "not listed");

            var info = new CollectionInfo("test", "Test collection", null, "https://example.org/test.zip", 123, "abc", "music", "2026-10-20T00:00:00Z", "1.0.0");
            var names = new Dictionary<string, string> { ["console/nintendo/snes"] = "Super Nintendo" };
            var report = CollectionWriter.Write(database, info, root, names);

            Assert.Equal(4, report.Files);
            Assert.Equal(new[] { "other/Broken.mid" }, report.Errors.Select(e => e.Path));
            Assert.Equal(2, report.Standards["GS"]);
            Assert.Single(report.Duplicates);   // Copy.mid = Zelda_-_Dungeon.mid

            using var connection = LibraryFormat.OpenCollection(database);
            Assert.NotNull(connection);
            Assert.Equal(info, LibraryFormat.ReadInfo(connection));
            List<object[]> Query(string sql)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                using var reader = command.ExecuteReader();
                var rows = new List<object[]>();
                while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); }
                return rows;
            }
            // Folders: parents first, recursive counts, display names.
            var folders = Query("SELECT path, display_name, file_count, parent_id FROM folders ORDER BY id");
            Assert.Equal(new[] { "", "console", "other", "console/nintendo", "console/nintendo/snes" }, folders.Select(f => (string)f[0]));
            Assert.Equal(new long[] { 4, 2, 2, 2, 2 }, folders.Select(f => (long)f[2]));
            Assert.Equal("Super Nintendo", folders[4][1]);
            Assert.Equal(DBNull.Value, folders[0][3]);

            var zelda = Query("SELECT title, internal_title, standard, channels, has_lyrics, error FROM files WHERE name = 'Zelda_-_Dungeon.mid'").Single();
            Assert.Equal(new object[] { "Zelda - Dungeon", "Dungeon", "GS", 1L << 9, 0L, DBNull.Value }, zelda);
            var karaoke = Query("SELECT has_lyrics, standard FROM files WHERE name = 'Song.kar'").Single();
            Assert.Equal(new object[] { 1L, "GM" }, karaoke);
            var broken = Query("SELECT standard, error FROM files WHERE name = 'Broken.mid'").Single();
            Assert.Equal(DBNull.Value, broken[0]);
            Assert.NotEqual(DBNull.Value, broken[1]);

            // Search: words of the title, display names of the folders, lyrics (accents ignored).
            string Found(string query) => string.Join(",", Query($"SELECT f.name FROM files_search s JOIN files f ON f.id = s.rowid WHERE files_search MATCH '{query}' ORDER BY f.name").Select(r => (string)r[0]));
            Assert.Equal("Copy.mid,Zelda_-_Dungeon.mid", Found("dungeon"));
            Assert.Equal("Zelda_-_Dungeon.mid", Found("zel*"));
            Assert.Equal("Copy.mid,Zelda_-_Dungeon.mid", Found("super nintendo"));
            Assert.Equal("Song.kar", Found("hello"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (File.Exists(database)) File.Delete(database);
        }
    }

    [Fact]
    public void Another_version_is_refused()
    {
        var database = Path.Combine(Path.GetTempPath(), "powermidi-tests", $"version-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database));
        try
        {
            using (var connection = LibraryFormat.Create(database))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA user_version = {LibraryFormat.Version + 1}";
                command.ExecuteNonQuery();
            }
            Assert.Null(LibraryFormat.OpenCollection(database));
        }
        finally { File.Delete(database); }
    }
}
