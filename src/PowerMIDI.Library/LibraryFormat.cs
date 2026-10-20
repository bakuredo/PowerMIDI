using Microsoft.Data.Sqlite;

namespace PowerMIDI.Library;

// What a collection is and where its files come from (a row of the table collection). Root: the folder inside the source
// zip the paths of the collection start from ('/' separators).
public sealed record CollectionInfo(string Id, string Name, string Description, string SourceUrl, long SourceSize, string SourceSha1,
                                    string Root, string BuiltAt, string BuilderVersion)
{
    // Name of the source zip (the last part of its URL), as it is downloaded into the library folder.
    public string ZipName => Uri.UnescapeDataString(new Uri(SourceUrl).Segments.Last());
}

// Format of the library database (library\library.db, SQLite, read only for PowerMIDI): the collections, the folder
// tree and the files of each one, with a full-text index. Made by the Library Builder, one collection at a time. The
// version is the user_version of the database: a reader refuses another one.
public static class LibraryFormat
{
    public const int Version = 1;

    // Paths: relative to the root of their collection, with '/' separators ('' for the root folder).
    // Folders: file_count counts the files in the folder and below. Files: title is the name shown (cleaned file name,
    // see LibraryEntry.TitleOf), standard is null for a file that could not be read (error says why), channels is a bit
    // mask of the channels with notes (bit 0 = channel 1).
    // Search: one row per file (rowid = files.id), contentless (rows deletable, for a collection built again): title,
    // internal title, path with the display names of its folders, lyrics. Accents are ignored; '_', '-' and the other
    // punctuation separate words.
    public const string Schema = """
        CREATE TABLE collection (
          id              TEXT PRIMARY KEY,
          name            TEXT NOT NULL,
          description     TEXT,
          source_url      TEXT NOT NULL,
          source_size     INTEGER NOT NULL,
          source_sha1     TEXT NOT NULL,
          root            TEXT NOT NULL,
          built_at        TEXT NOT NULL,
          builder_version TEXT NOT NULL
        );
        CREATE TABLE folders (
          id            INTEGER PRIMARY KEY,
          collection_id TEXT NOT NULL REFERENCES collection(id),
          parent_id     INTEGER REFERENCES folders(id),
          name          TEXT NOT NULL,
          display_name  TEXT,
          path          TEXT NOT NULL,
          file_count    INTEGER NOT NULL,
          UNIQUE (collection_id, path)
        );
        CREATE INDEX folders_parent ON folders(parent_id);
        CREATE TABLE files (
          id             INTEGER PRIMARY KEY,
          folder_id      INTEGER NOT NULL REFERENCES folders(id),
          name           TEXT NOT NULL,
          title          TEXT NOT NULL,
          internal_title TEXT,
          copyright      TEXT,
          size           INTEGER NOT NULL,
          sha1           TEXT NOT NULL,
          standard       TEXT,
          smf_format     INTEGER,
          tracks         INTEGER,
          channels       INTEGER,
          duration_ms    INTEGER,
          has_lyrics     INTEGER NOT NULL DEFAULT 0,
          has_loop       INTEGER NOT NULL DEFAULT 0,
          error          TEXT
        );
        CREATE INDEX files_folder   ON files(folder_id);
        CREATE INDEX files_standard ON files(standard);
        CREATE INDEX files_sha1     ON files(sha1);
        CREATE VIRTUAL TABLE files_search USING fts5(
          title, internal_title, path, lyrics,
          content = '', contentless_delete = 1, tokenize = 'unicode61 remove_diacritics 2'
        );
        """;

    // A library database at a path: opened, or created with the schema and its version.
    public static SqliteConnection OpenOrCreate(string path)
    {
        bool exists = File.Exists(path);
        var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        if (!exists)
        {
            Execute(connection, Schema);
            Execute(connection, $"PRAGMA user_version = {Version}");
        }
        else if (VersionOf(connection) != Version)
        {
            connection.Dispose();
            throw new InvalidDataException($"{path}: not a library database of version {Version}");
        }
        return connection;
    }

    // An existing library database, read only; null if it is not one of this version.
    public static SqliteConnection OpenLibrary(string path)
    {
        var connection = Open(path, SqliteOpenMode.ReadOnly);
        if (VersionOf(connection) == Version) return connection;
        connection.Dispose();
        return null;
    }

    static int VersionOf(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    // The collections of a library database, in the order of their names.
    public static List<CollectionInfo> ReadCollections(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, description, source_url, source_size, source_sha1, root, built_at, builder_version FROM collection ORDER BY name";
        using var reader = command.ExecuteReader();
        var list = new List<CollectionInfo>();
        string Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        while (reader.Read()) list.Add(new CollectionInfo(Text(0), Text(1), Text(2), Text(3), reader.GetInt64(4), Text(5), Text(6), Text(7), Text(8)));
        return list;
    }

    static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        // No pooling: the file is released as soon as the connection is closed (replaced, deleted, zipped).
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
