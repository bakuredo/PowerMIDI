using Microsoft.Data.Sqlite;

namespace PowerMIDI.Library;

// What a build found: the files by standard (null: unreadable), the unreadable ones with their error, the groups of
// identical files (same SHA-1).
public sealed record BuildReport(int Files, int Folders, IReadOnlyDictionary<string, int> Standards, IReadOnlyList<(string Path, string Error)> Errors,
                                 IReadOnlyList<IReadOnlyList<string>> Duplicates);

// Builds the database of a collection from the folder its files are in (see LibraryFormat).
public static class CollectionWriter
{
    static readonly string[] Extensions = { ".mid", ".midi", ".kar" };

    // folderNames: display names by folder path ('/' separators; a folder without one keeps its name).
    // progress: files read so far, out of the total.
    public static BuildReport Write(string databasePath, CollectionInfo info, string root, IReadOnlyDictionary<string, string> folderNames = null,
                                    IProgress<(int Done, int Total)> progress = null)
    {
        root = Path.GetFullPath(root);
        var paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        // The files are read in parallel (the slow part); the database is then written in one transaction.
        var entries = new LibraryEntry[paths.Count];
        int done = 0;
        Parallel.For(0, paths.Count, i =>
        {
            entries[i] = LibraryEntry.Read(root, paths[i]);
            int n = Interlocked.Increment(ref done);
            if (n % 100 == 0 || n == paths.Count) progress?.Report((n, paths.Count));
        });

        // Folders: the root and every folder that holds files, with its parents.
        static string FolderOf(string path) => path.LastIndexOf('/') is int i and >= 0 ? path[..i] : "";
        var counts = new Dictionary<string, int> { [""] = 0 };
        foreach (var path in paths)
        {
            for (var folder = FolderOf(path); ; folder = FolderOf(folder))
            {
                counts[folder] = counts.GetValueOrDefault(folder) + 1;
                if (folder == "") break;
            }
        }
        counts[""] = paths.Count;
        var folders = counts.Keys.OrderBy(p => p.Count(c => c == '/') + (p == "" ? 0 : 1)).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        string DisplayName(string path) => folderNames != null && folderNames.TryGetValue(path, out var name) ? name : null;

        // Written next to it first: the database already there is replaced only once the new one is complete.
        var building = databasePath + ".building";
        using (var connection = LibraryFormat.Create(building))
        {
            using var transaction = connection.BeginTransaction();
            Insert(connection, "INSERT INTO collection VALUES ($id, $name, $description, $url, $size, $sha1, $root, $built, $version)",
                   ("$id", info.Id), ("$name", info.Name), ("$description", info.Description), ("$url", info.SourceUrl), ("$size", info.SourceSize),
                   ("$sha1", info.SourceSha1), ("$root", info.Root), ("$built", info.BuiltAt), ("$version", info.BuilderVersion));

            var folderIds = new Dictionary<string, long>();
            using (var command = Command(connection, "INSERT INTO folders (id, parent_id, name, display_name, path, file_count) VALUES ($id, $parent, $name, $display, $path, $count)"))
                foreach (var path in folders)
                {
                    long id = folderIds.Count + 1;
                    folderIds[path] = id;
                    Run(command, ("$id", id), ("$parent", path == "" ? null : folderIds[FolderOf(path)]),
                        ("$name", path == "" ? info.Name : path[(path.LastIndexOf('/') + 1)..]), ("$display", DisplayName(path)),
                        ("$path", path), ("$count", counts[path]));
                }

            // The text searched in the path of a file: its folders by their names and their display names.
            string SearchPath(string path)
            {
                var words = new List<string> { path };
                for (var folder = FolderOf(path); folder != ""; folder = FolderOf(folder))
                    if (DisplayName(folder) is { } display) words.Add(display);
                return string.Join(' ', words);
            }

            using (var file = Command(connection, """
                INSERT INTO files (id, folder_id, name, title, internal_title, copyright, size, sha1, standard, smf_format, tracks, channels,
                                   duration_ms, has_lyrics, has_loop, error)
                VALUES ($id, $folder, $name, $title, $internal, $copyright, $size, $sha1, $standard, $format, $tracks, $channels,
                        $duration, $lyrics, $loop, $error)
                """))
            using (var search = Command(connection, "INSERT INTO files_search (rowid, title, internal_title, path, lyrics) VALUES ($id, $title, $internal, $path, $lyrics)"))
                for (int i = 0; i < entries.Length; i++)
                {
                    var e = entries[i];
                    long id = i + 1;
                    Run(file, ("$id", id), ("$folder", folderIds[FolderOf(e.Path)]), ("$name", e.Name), ("$title", e.Title), ("$internal", e.InternalTitle),
                        ("$copyright", e.Copyright), ("$size", e.Size), ("$sha1", e.Sha1), ("$standard", e.Standard), ("$format", e.SmfFormat),
                        ("$tracks", e.Tracks), ("$channels", e.Channels), ("$duration", e.DurationMs), ("$lyrics", e.HasLyrics ? 1 : 0),
                        ("$loop", e.HasLoop ? 1 : 0), ("$error", e.Error));
                    Run(search, ("$id", id), ("$title", e.Title), ("$internal", e.InternalTitle), ("$path", SearchPath(e.Path)), ("$lyrics", e.Lyrics));
                }
            transaction.Commit();
            LibraryFormat.Execute(connection, "INSERT INTO files_search (files_search) VALUES ('optimize')");
            LibraryFormat.Execute(connection, "VACUUM");
        }
        File.Move(building, databasePath, true);

        return new BuildReport(entries.Length, folders.Count,
            entries.GroupBy(e => e.Standard ?? "").ToDictionary(g => g.Key, g => g.Count()),
            entries.Where(e => e.Error != null).Select(e => (e.Path, e.Error)).ToList(),
            entries.GroupBy(e => e.Sha1).Where(g => g.Count() > 1).Select(g => (IReadOnlyList<string>)g.Select(e => e.Path).ToList()).ToList());
    }

    static SqliteCommand Command(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    static void Run(SqliteCommand command, params (string Name, object Value)[] values)
    {
        command.Parameters.Clear();
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    static void Insert(SqliteConnection connection, string sql, params (string Name, object Value)[] values)
    {
        using var command = Command(connection, sql);
        Run(command, values);
    }
}
