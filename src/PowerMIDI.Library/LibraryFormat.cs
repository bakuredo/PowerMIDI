using Microsoft.Data.Sqlite;

namespace PowerMIDI.Library;

// What a collection is and where its files come from (table collection of its database). Root: the folder inside the
// source zip the paths of the database start from ('/' separators).
public sealed record CollectionInfo(string Id, string Name, string Description, string SourceUrl, long SourceSize, string SourceSha1,
                                    string Root, string BuiltAt, string BuilderVersion);

// Format of the database of a collection (SQLite, one file per collection, read only for PowerMIDI): the collection,
// its folder tree and its files, with a full-text index. The version is the user_version of the database: a reader
// refuses another one.
public static class LibraryFormat
{
    public const int Version = 1;

    // Paths: relative to the root of the collection, with '/' separators ('' for the root folder).
    // Folders: file_count counts the files in the folder and below. Files: title is the name shown (cleaned file name,
    // see LibraryEntry.TitleOf), standard is null for a file that could not be read (error says why), channels is a bit
    // mask of the channels with notes (bit 0 = channel 1).
    // Search: one row per file (rowid = files.id), contentless: title, internal title, path with the display names of
    // its folders, lyrics. Accents are ignored; '_', '-' and the other punctuation separate words.
    public const string Schema = """
        CREATE TABLE collection (
          id              TEXT NOT NULL,
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
          id           INTEGER PRIMARY KEY,
          parent_id    INTEGER REFERENCES folders(id),
          name         TEXT NOT NULL,
          display_name TEXT,
          path         TEXT NOT NULL UNIQUE,
          file_count   INTEGER NOT NULL
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
          content = '', tokenize = 'unicode61 remove_diacritics 2'
        );
        """;

    // A new database at a path (an existing file is replaced), with the schema and its version.
    public static SqliteConnection Create(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        Execute(connection, Schema);
        Execute(connection, $"PRAGMA user_version = {Version}");
        return connection;
    }

    // An existing database, read only; null if it is not a collection database of this version.
    public static SqliteConnection OpenCollection(string path)
    {
        var connection = Open(path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(command.ExecuteScalar()) == Version) return connection;
        connection.Dispose();
        return null;
    }

    public static CollectionInfo ReadInfo(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, description, source_url, source_size, source_sha1, root, built_at, builder_version FROM collection";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        string Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        return new CollectionInfo(Text(0), Text(1), Text(2), Text(3), reader.GetInt64(4), Text(5), Text(6), Text(7), Text(8));
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
