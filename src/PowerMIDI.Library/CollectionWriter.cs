using Microsoft.Data.Sqlite;

namespace PowerMIDI.Library;

// What a build found: the files by standard (null: unreadable), the unreadable ones with their error, the groups of
// identical files (same SHA-1), the display names given for folders that the collection does not have (a mistake in the
// description of the collection).
public sealed record BuildReport(int Files, int Folders, IReadOnlyDictionary<string, int> Standards, IReadOnlyList<(string Path, string Error)> Errors,
                                 IReadOnlyList<IReadOnlyList<string>> Duplicates, IReadOnlyList<string> UnknownFolders);

// Builds a collection of the library database from the folder its files are in (see LibraryFormat): added to the
// database (created if missing), or in place of the one of the same id; the other collections are kept.
public static class CollectionWriter
{
    static readonly string[] Extensions = { ".mid", ".midi", ".kar" };

    static bool IsMidi(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // source: the folder of the root of the collection (its files extracted), or its source zip (the files under its root,
    // read from it: names that Windows cannot have as files, such as a folder ending with a space, are kept).
    // folderNames: display names by folder path ('/' separators; a folder without one keeps its name).
    // progress: files read so far, out of the total.
    public static BuildReport Write(string databasePath, CollectionInfo info, string source, IReadOnlyDictionary<string, string> folderNames = null,
                                    IProgress<(int Done, int Total)> progress = null)
    {
        LibraryEntry[] entries;
        List<string> paths;
        int done = 0;
        void Count(int total) { int n = Interlocked.Increment(ref done); if (n % 100 == 0 || n == total) progress?.Report((n, total)); }
        if (File.Exists(source))
        {
            // The zip: its files decompressed by batches (one at a time: a zip is read by one thread), each batch analysed
            // in parallel.
            using var zip = System.IO.Compression.ZipFile.OpenRead(source);
            var prefix = info.Root.Trim('/');
            if (prefix.Length > 0) prefix += "/";
            var found = zip.Entries.Select(e => (Path: e.FullName.Replace('\\', '/'), Entry: e))
                .Where(x => x.Path.StartsWith(prefix, StringComparison.Ordinal) && !x.Path.EndsWith('/') && IsMidi(x.Path))
                .Select(x => (Path: x.Path[prefix.Length..], x.Entry))
                .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
            paths = found.Select(x => x.Path).ToList();
            entries = new LibraryEntry[found.Count];
            for (int start = 0; start < found.Count; start += 2000)
            {
                var batch = found.Skip(start).Take(2000).Select(x =>
                {
                    using var stream = x.Entry.Open();
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }).ToList();
                int first = start;
                Parallel.For(0, batch.Count, i => { entries[first + i] = LibraryEntry.Read(found[first + i].Path, batch[i]); Count(found.Count); });
            }
        }
        else
        {
            var root = Path.GetFullPath(source);
            paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(IsMidi)
                .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            // The files are read in parallel (the slow part); the database is then written in one transaction.
            entries = new LibraryEntry[paths.Count];
            var all = paths;
            Parallel.For(0, all.Count, i => { entries[i] = LibraryEntry.Read(root, all[i]); Count(all.Count); });
        }

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

        // Written in a copy first: the database already there is replaced only once the new one is complete.
        var building = databasePath + ".building";
        if (File.Exists(building)) File.Delete(building);
        if (File.Exists(databasePath)) File.Copy(databasePath, building);
        using (var connection = LibraryFormat.OpenOrCreate(building))
        {
            using var transaction = connection.BeginTransaction();
            // The collection built before, if any: removed (its search rows first).
            foreach (var sql in new[]
            {
                "DELETE FROM files_search WHERE rowid IN (SELECT f.id FROM files f JOIN folders fo ON fo.id = f.folder_id WHERE fo.collection_id = $id)",
                "DELETE FROM files WHERE folder_id IN (SELECT id FROM folders WHERE collection_id = $id)",
                "DELETE FROM folders WHERE collection_id = $id",
                "DELETE FROM collection WHERE id = $id",
            })
                Insert(connection, sql, ("$id", info.Id));
            Insert(connection, "INSERT INTO collection VALUES ($id, $name, $url, $size, $sha1, $root, $built, $version)",
                   ("$id", info.Id), ("$name", info.Name), ("$url", info.SourceUrl), ("$size", info.SourceSize),
                   ("$sha1", info.SourceSha1), ("$root", info.Root), ("$built", info.BuiltAt), ("$version", info.BuilderVersion));

            var folderIds = new Dictionary<string, long>();
            using (var command = Command(connection, "INSERT INTO folders (collection_id, parent_id, name, display_name, path, file_count) " +
                                                     "VALUES ($collection, $parent, $name, $display, $path, $count) RETURNING id"))
                foreach (var path in folders)
                    folderIds[path] = Scalar(command, ("$collection", info.Id), ("$parent", path == "" ? null : folderIds[FolderOf(path)]),
                                             ("$name", path == "" ? info.Name : path[(path.LastIndexOf('/') + 1)..]), ("$display", DisplayName(path)),
                                             ("$path", path), ("$count", counts[path]));

            // The text searched in the path of a file: its folders by their names and their display names.
            string SearchPath(string path)
            {
                var words = new List<string> { path };
                for (var folder = FolderOf(path); folder != ""; folder = FolderOf(folder))
                    if (DisplayName(folder) is { } display) words.Add(display);
                return string.Join(' ', words);
            }

            using (var file = Command(connection, """
                INSERT INTO files (folder_id, name, title, internal_title, copyright, size, sha1, standard, tracks, channels,
                                   duration_ms, has_lyrics, has_loop, error)
                VALUES ($folder, $name, $title, $internal, $copyright, $size, $sha1, $standard, $tracks, $channels,
                        $duration, $lyrics, $loop, $error) RETURNING id
                """))
            using (var search = Command(connection, "INSERT INTO files_search (rowid, title, internal_title, path, lyrics) VALUES ($id, $title, $internal, $path, $lyrics)"))
                foreach (var e in entries)
                {
                    long id = Scalar(file, ("$folder", folderIds[FolderOf(e.Path)]), ("$name", e.Name), ("$title", e.Title), ("$internal", e.InternalTitle),
                                     ("$copyright", e.Copyright), ("$size", e.Size), ("$sha1", e.Sha1), ("$standard", e.Standard),
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
            entries.GroupBy(e => e.Sha1).Where(g => g.Count() > 1).Select(g => (IReadOnlyList<string>)g.Select(e => e.Path).ToList()).ToList(),
            (folderNames?.Keys ?? []).Where(path => !counts.ContainsKey(path)).Order(StringComparer.OrdinalIgnoreCase).ToList());
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

    // Runs an INSERT … RETURNING id: the id of the row.
    static long Scalar(SqliteCommand command, params (string Name, object Value)[] values)
    {
        command.Parameters.Clear();
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static void Insert(SqliteConnection connection, string sql, params (string Name, object Value)[] values)
    {
        using var command = Command(connection, sql);
        Run(command, values);
    }
}
