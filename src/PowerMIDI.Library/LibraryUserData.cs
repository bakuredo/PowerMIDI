using Microsoft.Data.Sqlite;

namespace PowerMIDI.Library;

// What the user keeps of the library (library\user.db; never distributed, so that a new library.db leaves it as it is):
// for the files of each collection, the favorites and the plays. A file is named by its collection and its path in it,
// which a new build of the collection keeps.
public sealed class LibraryUserData : IDisposable
{
    const int Version = 1;
    const string Schema = """
        CREATE TABLE IF NOT EXISTS usage (
          collection_id TEXT NOT NULL,
          path          TEXT NOT NULL,
          favorite      INTEGER NOT NULL DEFAULT 0,
          play_count    INTEGER NOT NULL DEFAULT 0,
          last_played   TEXT,
          PRIMARY KEY (collection_id, path)
        );
        """;

    readonly SqliteConnection connection;

    LibraryUserData(SqliteConnection connection) => this.connection = connection;

    // Opens the file, created if missing.
    public static LibraryUserData Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        LibraryFormat.Execute(connection, Schema);
        LibraryFormat.Execute(connection, $"PRAGMA user_version = {Version}");
        return new LibraryUserData(connection);
    }

    public void Dispose() => connection.Dispose();

    public void SetFavorite(string id, string path, bool favorite) =>
        Run("INSERT INTO usage (collection_id, path, favorite) VALUES ($id, $path, $fav) ON CONFLICT (collection_id, path) DO UPDATE SET favorite = $fav",
            ("$id", id), ("$path", path), ("$fav", favorite ? 1 : 0));

    public void RecordPlay(string id, string path) =>
        Run("""
            INSERT INTO usage (collection_id, path, play_count, last_played) VALUES ($id, $path, 1, $at)
            ON CONFLICT (collection_id, path) DO UPDATE SET play_count = play_count + 1, last_played = $at
            """, ("$id", id), ("$path", path), ("$at", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")));

    void Run(string sql, params (string Name, object Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
