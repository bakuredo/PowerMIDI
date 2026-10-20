using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using PowerMIDI.Library;

namespace PowerMIDI.LibraryBuilder;

// Library Builder: builds a collection of MIDI files into the library database (see LibraryFormat; added, or in place of
// the one of the same id) from its source zip (or the folder where its files are extracted), and the description of the
// collection (Collections\<id>.json: source zip, names of the folders).
//   PowerMIDI-LibraryBuilder <collection> <zip or folder> [<database>]
// collection: the id of a known collection (vgmusic-2018) or the path of a description file; database: library.db in the
// current folder by default.
static class Program
{
    // A description file (Collections\<id>.json).
    sealed record Source(string Url, long Size, string Sha1, string Root);
    sealed record CollectionFile(string Id, string Name, string Description, Source Source, Dictionary<string, string> Folders);

    static int Main(string[] args)
    {
        if (args.Length is < 2 or > 3)
        {
            Console.WriteLine("Usage: PowerMIDI-LibraryBuilder <collection> <zip or folder> [<database>]");
            Console.WriteLine("  collection: id of known collection (" + string.Join(", ", KnownCollections()) + ") or path of description file (.json)");
            Console.WriteLine("  zip or folder: source zip of collection, or folder where its files are extracted (root of collection)");
            Console.WriteLine("  database: library database to write it into (default: library.db in current folder)");
            return 1;
        }
        try
        {
            var description = ReadDescription(args[0]);
            var folder = args[1];
            if (!Directory.Exists(folder) && !File.Exists(folder)) throw new FileNotFoundException($"Zip or folder not found: {folder}");
            var output = args.Length == 3 ? args[2] : "library.db";
            var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";
            var info = new CollectionInfo(description.Id, description.Name, description.Description, description.Source.Url, description.Source.Size,
                                          description.Source.Sha1, description.Source.Root, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), version);

            Console.WriteLine($"PowerMIDI Library Builder {version}");
            Console.WriteLine($"Collection: {info.Name} ({info.Id})");
            Console.WriteLine($"Files from: {Path.GetFullPath(folder)}");
            var watch = Stopwatch.StartNew();
            var report = CollectionWriter.Write(output, info, folder, description.Folders, new ConsoleProgress());
            Console.WriteLine();

            Console.WriteLine($"Written: {Path.GetFullPath(output)} ({new FileInfo(output).Length / 1024.0 / 1024:0.0} MB) in {watch.Elapsed.TotalSeconds:0} s");
            Console.WriteLine($"{report.Files} files in {report.Folders} folders");
            Console.WriteLine("Standards: " + string.Join(", ", report.Standards.Where(s => s.Key != "").OrderByDescending(s => s.Value).Select(s => $"{s.Key} {s.Value}")));
            Console.WriteLine($"Unreadable: {report.Errors.Count}");
            foreach (var (path, error) in report.Errors.Take(10)) Console.WriteLine($"  {path}: {error}");
            if (report.Errors.Count > 10) Console.WriteLine($"  … {report.Errors.Count - 10} more");
            Console.WriteLine($"Identical files: {report.Duplicates.Count} groups ({report.Duplicates.Sum(d => d.Count - 1)} extra copies)");
            foreach (var group in report.Duplicates.Take(5)) Console.WriteLine("  " + string.Join("  =  ", group));
            if (report.Duplicates.Count > 5) Console.WriteLine($"  … {report.Duplicates.Count - 5} more");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    static string CollectionsFolder => Path.Combine(AppContext.BaseDirectory, "Collections");

    static IEnumerable<string> KnownCollections() =>
        Directory.Exists(CollectionsFolder) ? Directory.GetFiles(CollectionsFolder, "*.json").Select(Path.GetFileNameWithoutExtension) : [];

    // A description file, by its path or by the id of a known collection.
    static CollectionFile ReadDescription(string collection)
    {
        var path = File.Exists(collection) ? collection : Path.Combine(CollectionsFolder, collection + ".json");
        if (!File.Exists(path)) throw new FileNotFoundException($"Unknown collection: {collection}");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
        var description = JsonSerializer.Deserialize<CollectionFile>(File.ReadAllText(path), options);
        if (description?.Id == null || description.Name == null || description.Source?.Url == null || description.Source.Sha1 == null || description.Source.Root == null)
            throw new InvalidDataException($"{path}: id, name and source (url, size, sha1, root) are required");
        return description;
    }

    // Progress on one line, from the threads that read the files (the highest count so far).
    sealed class ConsoleProgress : IProgress<(int Done, int Total)>
    {
        readonly object gate = new();
        int shown;
        public void Report((int Done, int Total) value)
        {
            lock (gate)
            {
                if (value.Done <= shown) return;
                shown = value.Done;
                Console.Write($"\rReading files: {value.Done} / {value.Total} ({100.0 * value.Done / Math.Max(1, value.Total):0}%)   ");
            }
        }
    }
}
