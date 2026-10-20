using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PowerMIDI.Library;

// A folder of a collection: Path with '/' separators ('' for the root), FileCount for the files in it and below.
public sealed record LibraryFolder(long Id, long? ParentId, string Name, string DisplayName, string Path, int FileCount)
{
    // Shown: the display name, otherwise the name with '_' read as spaces (as the titles of the files).
    public string Shown => DisplayName ?? Name.Replace('_', ' ');
}

// A file of a collection, with what the user keeps of it (favorite, plays). Path: in the collection; Standard: null if
// unreadable (Error says why).
public sealed record LibraryFile(string Path, string FolderPath, string Title, string InternalTitle, string Copyright, long Size, string Sha1,
                                 string Standard, long? DurationMs, int? Tracks, int? Channels, bool HasLyrics, bool HasLoop, string Error,
                                 bool Favorite, int PlayCount, string LastPlayed);

// Which files are shown: all, the favorites, those played (the last ones first, or the most played first), those
// never played, those with lyrics (karaoke).
public enum LibraryShow { All, Favorites, RecentlyPlayed, MostPlayed, NotPlayed, Lyrics }

// Standard: GM, GS, XG, GM2 or MT-32 (null: any, the unreadable files included). Search: words of the title, the internal
// title, the folders or the lyrics, each one the start of a word ("zel dun" finds "Zelda - Dungeon").
public sealed record LibraryFilter(string Standard = null, LibraryShow Show = LibraryShow.All, string Search = null)
{
    public bool IsEmpty => Standard == null && Show == LibraryShow.All && SearchQuery(Search) == null;

    // FTS5 query of a search: each word as a quoted prefix; null if it has no word.
    internal static string SearchQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var words = Regex.Matches(text, @"[\p{L}\p{N}]+").Select(m => "\"" + m.Value + "\"*").ToList();
        return words.Count > 0 ? string.Join(' ', words) : null;
    }
}

// A collection of the library database opened for the Library tab (read only), with the favorites and plays of the user
// (the user database, attached).
public sealed class CollectionCatalog : IDisposable
{
    readonly SqliteConnection connection;
    readonly Dictionary<long, LibraryFolder> foldersById;

    public CollectionInfo Info { get; }
    public IReadOnlyDictionary<string, LibraryFolder> Folders { get; }

    CollectionCatalog(SqliteConnection connection, CollectionInfo info, string userDatabase)
    {
        this.connection = connection;
        Info = info;
        using (var attach = connection.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $path AS user";
            attach.Parameters.AddWithValue("$path", userDatabase);
            attach.ExecuteNonQuery();
        }
        var folders = new List<LibraryFolder>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, parent_id, name, display_name, path, file_count FROM folders WHERE collection_id = $collection ORDER BY id";
            command.Parameters.AddWithValue("$collection", info.Id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                folders.Add(new LibraryFolder(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2),
                                              reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetInt32(5)));
        }
        foldersById = folders.ToDictionary(f => f.Id);
        Folders = folders.ToDictionary(f => f.Path);
    }

    // A collection of a library database (null: not a library database of this version, or no such collection). The user
    // database must exist (LibraryUserData creates it).
    public static CollectionCatalog Open(string database, string collectionId, string userDatabase)
    {
        var connection = LibraryFormat.OpenLibrary(database);
        if (connection == null) return null;
        var info = LibraryFormat.ReadCollections(connection).FirstOrDefault(c => c.Id == collectionId);
        if (info != null) return new CollectionCatalog(connection, info, userDatabase);
        connection.Dispose();
        return null;
    }

    public void Dispose() => connection.Dispose();

    // Folders in a folder, in alphabetical order of the names shown.
    public List<LibraryFolder> Children(string path)
    {
        var parent = Folders[path];
        return Folders.Values.Where(f => f.ParentId == parent.Id).OrderBy(f => f.Shown, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Files in a folder (below: and in its subfolders) shown by a filter, at most limit: in alphabetical order of the
    // titles, the last played or the most played first.
    public List<LibraryFile> Files(string folder, bool below, LibraryFilter filter, int limit = int.MaxValue)
    {
        using var command = connection.CreateCommand();
        var where = Where(command, filter);
        command.Parameters.AddWithValue("$folder", folder);
        where.Add("fo.collection_id = $collection");
        where.Add(below ? "($folder = '' OR fo.path = $folder OR fo.path LIKE $prefix ESCAPE '\\')" : "fo.path = $folder");
        if (below) command.Parameters.AddWithValue("$prefix", EscapeLike(folder) + "/%");
        var order = filter.Show switch
        {
            LibraryShow.RecentlyPlayed => "u.last_played DESC",
            LibraryShow.MostPlayed => "u.play_count DESC, f.title COLLATE NOCASE",
            _ => "f.title COLLATE NOCASE, fo.path",
        };
        command.CommandText = $"""
            SELECT CASE fo.path WHEN '' THEN f.name ELSE fo.path || '/' || f.name END, fo.path, f.title, f.internal_title, f.copyright, f.size,
                   f.sha1, f.standard, f.duration_ms, f.tracks, f.channels, f.has_lyrics, f.has_loop, f.error,
                   coalesce(u.favorite, 0), coalesce(u.play_count, 0), u.last_played
            FROM files f JOIN folders fo ON fo.id = f.folder_id
            {UsageJoin}
            WHERE {string.Join(" AND ", where)}
            ORDER BY {order}
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var files = new List<LibraryFile>();
        string Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        int? Int(int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
        while (reader.Read())
            files.Add(new LibraryFile(reader.GetString(0), reader.GetString(1), reader.GetString(2), Text(3), Text(4), reader.GetInt64(5), reader.GetString(6),
                                      Text(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), Int(9), Int(10), reader.GetInt32(11) != 0,
                                      reader.GetInt32(12) != 0, Text(13), reader.GetInt32(14) != 0, reader.GetInt32(15), Text(16)));
        return files;
    }

    // Number of files shown by a filter in each folder, its subfolders included (folders without any: left out).
    public Dictionary<string, int> Counts(LibraryFilter filter)
    {
        using var command = connection.CreateCommand();
        var where = Where(command, filter);
        where.Add("fo.collection_id = $collection");
        command.CommandText = $"SELECT f.folder_id, count(*) FROM files f JOIN folders fo ON fo.id = f.folder_id {UsageJoin} WHERE {string.Join(" AND ", where)} GROUP BY f.folder_id";
        var counts = new Dictionary<string, int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            int n = reader.GetInt32(1);
            for (long? id = reader.GetInt64(0); id is long folder; id = foldersById[folder].ParentId)
                counts[foldersById[folder].Path] = counts.GetValueOrDefault(foldersById[folder].Path) + n;
        }
        return counts;
    }

    // Favorites and plays of the user for the file of a row (f: files, fo: its folder).
    string UsageJoin => """
        LEFT JOIN user.usage u ON u.collection_id = $collection AND u.path = CASE fo.path WHEN '' THEN f.name ELSE fo.path || '/' || f.name END
        """;

    List<string> Where(SqliteCommand command, LibraryFilter filter)
    {
        command.Parameters.AddWithValue("$collection", Info.Id);
        var where = new List<string>();
        if (filter.Standard != null)
        {
            where.Add("f.standard = $standard");
            command.Parameters.AddWithValue("$standard", filter.Standard);
        }
        where.Add(filter.Show switch
        {
            LibraryShow.Favorites => "u.favorite = 1",
            LibraryShow.RecentlyPlayed or LibraryShow.MostPlayed => "u.play_count > 0",
            LibraryShow.NotPlayed => "coalesce(u.play_count, 0) = 0",
            LibraryShow.Lyrics => "f.has_lyrics = 1",
            _ => "1",
        });
        if (LibraryFilter.SearchQuery(filter.Search) is { } query)
        {
            where.Add("f.id IN (SELECT rowid FROM files_search WHERE files_search MATCH $search)");
            command.Parameters.AddWithValue("$search", query);
        }
        return where;
    }

    static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
