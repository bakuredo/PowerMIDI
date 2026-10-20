using System.IO.Compression;
using System.Net;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;
using PowerMIDI.Library;

namespace PowerMIDI.Tests;

// Library database (Library Builder): titles, collections, folders with their counts and display names, entries, search,
// favorites and plays; the files of a collection downloaded as a zip and taken from it on demand.
public class LibraryTests
{
    [Theory]
    [InlineData("Zelda_-_Dungeon.mid", "Zelda - Dungeon")]
    [InlineData("(KoD)_Boss__2.MID", "(KoD) Boss 2")]
    [InlineData("_.mid", "_.mid")]
    public void Title_of_a_file(string name, string title) => Assert.Equal(title, LibraryEntry.TitleOf(name));

    static readonly NormalSysExEvent GsReset = new(new byte[] { 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 });

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

    static List<object[]> Query(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object[]>();
        while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); }
        return rows;
    }

    // A library folder with a collection "sample": its files (folder music\, zipped as source.zip with the root "music"),
    // library.db and user.db.
    sealed class Sample : IDisposable
    {
        public readonly string Folder = Path.Combine(Path.GetTempPath(), "powermidi-tests", "library-" + Guid.NewGuid().ToString("N"));
        public string Files => Path.Combine(Folder, "music");
        public string Zip => Path.Combine(Folder, "source.zip");
        public string Database => Path.Combine(Folder, "library.db");
        public string User => Path.Combine(Folder, "user.db");
        public CollectionInfo Info;
        public BuildReport Report;

        public Sample()
        {
            Song(Path.Combine(Files, "console", "snes", "Zelda_Dungeon.mid"), new SequenceTrackNameEvent("Dungeon\0"), GsReset);
            Song(Path.Combine(Files, "console", "snes", "Copy.mid"), new SequenceTrackNameEvent("Dungeon\0"), GsReset);
            Song(Path.Combine(Files, "console", "snes", "Mario_World.mid"));
            Song(Path.Combine(Files, "console", "nes", "Zelda_Title.mid"));
            Song(Path.Combine(Files, "other", "Song.kar"), new TextEvent("@KMIDI KARAOKE FILE"), new TextEvent("Hél"), new TextEvent("lo"));
            File.WriteAllText(Path.Combine(Files, "other", "Broken.mid"), "not a MIDI file");
            File.WriteAllText(Path.Combine(Files, "other", "readme.txt"), "not listed");
            System.IO.Compression.ZipFile.CreateFromDirectory(Files, Zip, System.IO.Compression.CompressionLevel.Optimal, true);
            using (var stream = File.OpenRead(Zip))
                Info = new CollectionInfo("sample", "Sample", null, "http://localhost/files/source.zip", new FileInfo(Zip).Length,
                                          Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(stream)), "music", "2026-10-20T00:00:00Z", "1.0.0");
            Report = CollectionWriter.Write(Database, Info, Files, new Dictionary<string, string> { ["console/snes"] = "Super Nintendo" });
            LibraryUserData.Open(User).Dispose();
        }

        public CollectionCatalog Catalog(string id = "sample") => CollectionCatalog.Open(Database, id, User);

        public void Dispose() { try { Directory.Delete(Folder, true); } catch { } }
    }

    [Fact]
    public void Collection_written_and_read_back()
    {
        using var sample = new Sample();
        var report = sample.Report;
        Assert.Equal(6, report.Files);
        Assert.Equal(new[] { "other/Broken.mid" }, report.Errors.Select(e => e.Path));
        Assert.Equal(2, report.Standards["GS"]);
        Assert.Equal(2, report.Duplicates.Count);   // Copy.mid = Zelda_Dungeon.mid, Mario_World.mid = Zelda_Title.mid (same notes)

        using var connection = LibraryFormat.OpenLibrary(sample.Database);
        Assert.Equal(new[] { sample.Info }, LibraryFormat.ReadCollections(connection));
        // Folders: parents first, recursive counts, display names.
        var folders = Query(connection, "SELECT path, display_name, file_count, parent_id FROM folders ORDER BY id");
        Assert.Equal(new[] { "", "console", "other", "console/nes", "console/snes" }, folders.Select(f => (string)f[0]));
        Assert.Equal(new long[] { 6, 4, 2, 1, 3 }, folders.Select(f => (long)f[2]));
        Assert.Equal("Super Nintendo", folders[4][1]);
        Assert.Equal(DBNull.Value, folders[0][3]);

        var zelda = Query(connection, "SELECT title, internal_title, standard, channels, has_lyrics, error FROM files WHERE name = 'Zelda_Dungeon.mid'").Single();
        Assert.Equal(new object[] { "Zelda Dungeon", "Dungeon", "GS", 1L << 9, 0L, DBNull.Value }, zelda);
        Assert.Equal(new object[] { 1L, "GM" }, Query(connection, "SELECT has_lyrics, standard FROM files WHERE name = 'Song.kar'").Single());
        var broken = Query(connection, "SELECT standard, error FROM files WHERE name = 'Broken.mid'").Single();
        Assert.Equal(DBNull.Value, broken[0]);
        Assert.NotEqual(DBNull.Value, broken[1]);
    }

    [Fact]
    public void Catalog_lists_folders_files_and_filters()
    {
        using var sample = new Sample();
        using var user = LibraryUserData.Open(sample.User);
        using var catalog = sample.Catalog();
        string Titles(IEnumerable<LibraryFile> files) => string.Join(",", files.Select(f => f.Title));

        Assert.Equal(new[] { "console", "other" }, catalog.Children("").Select(f => f.Name));
        Assert.Equal(new[] { "nes", "Super Nintendo" }, catalog.Children("console").Select(f => f.Shown));
        Assert.Equal("Copy,Mario World,Zelda Dungeon", Titles(catalog.Files("console/snes", false, new LibraryFilter())));
        Assert.Equal(6, catalog.Files("", true, new LibraryFilter()).Count);
        // Standard, search (prefix, display name of a folder, lyrics without accents), counts of the folders with the filter.
        Assert.Equal("Copy,Zelda Dungeon", Titles(catalog.Files("", true, new LibraryFilter("GS"))));
        Assert.Equal("Zelda Dungeon,Zelda Title", Titles(catalog.Files("", true, new LibraryFilter(Search: "zel"))));
        Assert.Equal("Zelda Dungeon", Titles(catalog.Files("", true, new LibraryFilter(Search: "super zelda"))));
        Assert.Equal("Zelda Title", Titles(catalog.Files("console/nes", true, new LibraryFilter(Search: "zelda"))));
        Assert.Equal("Song", Titles(catalog.Files("", true, new LibraryFilter(Search: "hello"))));
        var counts = catalog.Counts(new LibraryFilter("GS"));
        Assert.Equal(2, counts["console"]);
        Assert.False(counts.ContainsKey("console/nes"));

        // Favorites and plays (user database): the files shown by each list, in their order.
        user.SetFavorite("sample", "console/nes/Zelda_Title.mid", true);
        user.RecordPlay("sample", "other/Song.kar");
        user.RecordPlay("sample", "console/snes/Mario_World.mid");
        user.RecordPlay("sample", "console/snes/Mario_World.mid");
        user.RecordPlay("another", "console/snes/Copy.mid");   // another collection: not counted here
        Assert.Equal("Zelda Title", Titles(catalog.Files("", true, new LibraryFilter(Show: LibraryShow.Favorites))));
        Assert.Equal("Mario World,Song", Titles(catalog.Files("", true, new LibraryFilter(Show: LibraryShow.MostPlayed))));
        Assert.Equal(4, catalog.Files("", true, new LibraryFilter(Show: LibraryShow.NotPlayed)).Count);
        Assert.Equal("Song", Titles(catalog.Files("", true, new LibraryFilter(Show: LibraryShow.Lyrics))));
        var mario = catalog.Files("console/snes", false, new LibraryFilter(Search: "mario")).Single();
        Assert.Equal((2, false), (mario.PlayCount, mario.Favorite));
    }

    [Fact]
    public void Collections_share_the_library_database()
    {
        using var sample = new Sample();
        // A second collection, then the first one built again: each keeps its own files and search rows.
        var other = Path.Combine(sample.Folder, "other-files");
        Song(Path.Combine(other, "Zelda_Other.mid"));
        var info = sample.Info with { Id = "second", Name = "Second" };
        CollectionWriter.Write(sample.Database, info, other);
        CollectionWriter.Write(sample.Database, sample.Info, sample.Files, new Dictionary<string, string> { ["console/snes"] = "Super Nintendo" });

        using (var connection = LibraryFormat.OpenLibrary(sample.Database))
        {
            Assert.Equal(new[] { "Sample", "Second" }, LibraryFormat.ReadCollections(connection).Select(c => c.Name));
            Assert.Equal(7L, Query(connection, "SELECT count(*) FROM files").Single()[0]);
            Assert.Equal(7L, Query(connection, "SELECT count(*) FROM files_search").Single()[0]);   // no search row left behind
        }
        using (var first = sample.Catalog())
            Assert.Equal(new[] { "Zelda Dungeon", "Zelda Title" }, first.Files("", true, new LibraryFilter(Search: "zelda")).Select(f => f.Title));
        using (var second = sample.Catalog("second"))
        {
            Assert.Equal(new[] { "Zelda Other" }, second.Files("", true, new LibraryFilter(Search: "zelda")).Select(f => f.Title));
            Assert.Equal(1, second.Folders[""].FileCount);
        }
        Assert.Null(sample.Catalog("missing"));
    }

    // Serves one file over HTTP (localhost), as archive.org would serve a zip.
    sealed class FileServer : IDisposable
    {
        readonly HttpListener listener = new();
        public readonly string Url;

        public FileServer(string file)
        {
            int port = new Random().Next(20000, 60000);
            Url = $"http://localhost:{port}/files/{Path.GetFileName(file)}";
            listener.Prefixes.Add($"http://localhost:{port}/files/");
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); } catch { return; }
                    var bytes = File.ReadAllBytes(file);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        public void Dispose() => listener.Close();
    }

    [Fact]
    public async Task Files_downloaded_then_taken_from_the_zip()
    {
        using var sample = new Sample();
        var source = Path.Combine(sample.Folder, "served", "source.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(source));
        File.Move(sample.Zip, source);
        using var server = new FileServer(source);
        var info = sample.Info with { SourceUrl = server.Url };
        Assert.Equal("source.zip", info.ZipName);

        using var catalog = sample.Catalog();
        using var files = new CollectionFiles(sample.Folder, info);
        var zelda = catalog.Files("console/snes", false, new LibraryFilter(Search: "zelda")).Single();
        Assert.False(files.IsDownloaded);
        Assert.Throws<FileNotFoundException>(() => files.Get(zelda));

        using var http = new HttpClient();
        long last = 0;
        await files.DownloadAsync(http, new Progress<(long Done, long Total)>(p => last = p.Done), CancellationToken.None);
        Assert.True(files.IsDownloaded);
        Assert.Equal(Path.Combine(sample.Folder, "source.zip"), files.ZipPath);
        Assert.False(File.Exists(files.ZipPath + ".part"));

        var local = files.Get(zelda);
        Assert.Equal(Path.Combine(sample.Folder, "cache", "sample", "console", "snes", "Zelda_Dungeon.mid"), local);
        Assert.Equal(File.ReadAllBytes(Path.Combine(sample.Files, "console", "snes", "Zelda_Dungeon.mid")), File.ReadAllBytes(local));
        Assert.Equal("console/snes/Zelda_Dungeon.mid", files.PathOf(local));
        Assert.Null(files.PathOf(Path.Combine(sample.Folder, "elsewhere.mid")));
        Assert.Throws<InvalidDataException>(() => files.Get(zelda with { Sha1 = "0", Size = 1 }));   // not the file of the database

        files.DeleteDownloaded();
        Assert.False(files.IsDownloaded);
        Assert.False(Directory.Exists(files.CacheFolder));

        // Not the expected zip (another SHA-1): refused, nothing kept.
        using var wrong = new CollectionFiles(sample.Folder, info with { SourceSha1 = "0" });
        await Assert.ThrowsAsync<InvalidDataException>(() => wrong.DownloadAsync(http, null, CancellationToken.None));
        Assert.False(wrong.IsDownloaded);
        Assert.False(File.Exists(wrong.ZipPath + ".part"));
    }

    [Fact]
    public async Task Stalled_download_goes_on_from_where_it_stopped()
    {
        using var sample = new Sample();
        var bytes = File.ReadAllBytes(sample.Zip);
        File.Delete(sample.Zip);
        // The first request: half of the zip, then nothing (as a server of archive.org that stops answering); the next one
        // asks for the rest (Range) and gets it.
        int port = new Random().Next(20000, 60000);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/files/");
        listener.Start();
        var ranges = new List<string>();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch { return; }
                var range = context.Request.Headers["Range"];
                lock (ranges) ranges.Add(range ?? "");
                _ = Task.Run(async () =>   // each request apart: the stalled one does not hold the next
                {
                    try
                    {
                        if (range == null)
                        {
                            context.Response.ContentLength64 = bytes.Length;
                            await context.Response.OutputStream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2));
                            await context.Response.OutputStream.FlushAsync();
                            await Task.Delay(TimeSpan.FromSeconds(10));   // stalled
                        }
                        else
                        {
                            long from = long.Parse(range["bytes=".Length..].TrimEnd('-'));
                            context.Response.StatusCode = 206;
                            context.Response.AddHeader("Content-Range", $"bytes {from}-{bytes.Length - 1}/{bytes.Length}");
                            context.Response.ContentLength64 = bytes.Length - from;
                            await context.Response.OutputStream.WriteAsync(bytes.AsMemory((int)from));
                        }
                        context.Response.Close();
                    }
                    catch { }   // the stalled one: cut on purpose
                });
            }
        });

        var info = sample.Info with { SourceUrl = $"http://localhost:{port}/files/source.zip" };
        using var files = new CollectionFiles(sample.Folder, info);
        using var http = new HttpClient();
        var notices = new List<string>();
        await files.DownloadAsync(http, null, CancellationToken.None, notices.Add, TimeSpan.FromSeconds(1));
        Assert.True(files.IsDownloaded);
        Assert.Equal(bytes, File.ReadAllBytes(files.ZipPath));
        Assert.Equal(new[] { "Connecting to localhost…", "No answer from localhost: trying again (2/4)…" }, notices);
        lock (ranges) Assert.Equal(new[] { "", $"bytes={bytes.Length / 2}-" }, ranges);
    }

    [Fact]
    public void Built_from_a_zip_with_names_Windows_refuses()
    {
        using var sample = new Sample();
        // A zip made on demand (no SHA-1), whose folder ends with a space (as in the Sound Canvas collection), without root.
        var song = Path.Combine(sample.Files, "console", "snes", "Mario_World.mid");
        var zipPath = Path.Combine(sample.Folder, "odd.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(song, "Den Somaru n Desu /utsuru.MID");
            zip.CreateEntryFromFile(song, "CON/Nul.mid");
            zip.CreateEntry("readme.txt");
        }
        var info = sample.Info with { Id = "odd", Name = "Odd", SourceUrl = "http://localhost/odd.zip", SourceSize = new FileInfo(zipPath).Length, SourceSha1 = "", Root = "" };
        var report = CollectionWriter.Write(sample.Database, info, zipPath);
        Assert.Equal(2, report.Files);
        Assert.Null(CollectionFiles.CheckZip(zipPath, info));                                     // size only
        Assert.NotNull(CollectionFiles.CheckZip(zipPath, info with { SourceSize = 1 }));

        using var catalog = sample.Catalog("odd");
        Assert.Equal(new[] { "CON", "Den Somaru n Desu " }, catalog.Children("").Select(f => f.Name).OrderBy(n => n));
        using var files = new CollectionFiles(sample.Folder, info);
        foreach (var (path, onDisk) in new[] { ("Den Somaru n Desu /utsuru.MID", Path.Combine("Den Somaru n Desu", "utsuru.MID")),
                                               ("CON/Nul.mid", Path.Combine("CON_", "Nul.mid_")) })
        {
            var file = catalog.Files("", true, new LibraryFilter()).Single(f => f.Path == path);
            var local = files.Get(file);
            Assert.Equal(Path.Combine(sample.Folder, "cache", "odd", onDisk), local);
            Assert.Equal(File.ReadAllBytes(song), File.ReadAllBytes(local));
            Assert.Equal(path, files.PathOf(local));   // the path of the collection, not the one on the disk
        }
    }

    [Fact]
    public void Another_version_is_refused()
    {
        using var sample = new Sample();
        using (var connection = LibraryFormat.OpenOrCreate(sample.Database))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA user_version = {LibraryFormat.Version + 1}";
            command.ExecuteNonQuery();
        }
        Assert.Null(LibraryFormat.OpenLibrary(sample.Database));
        Assert.Throws<InvalidDataException>(() => LibraryFormat.OpenOrCreate(sample.Database));
    }
}
