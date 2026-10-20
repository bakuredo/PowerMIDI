using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace PowerMIDI.Library;

// The files of a collection on the disk: its source zip, downloaded into the library folder (the name of the zip in its
// URL), and the files taken from it on demand, into library\cache\<collection id>\<path>. A file taken from the zip is
// checked against its SHA-1 in the database.
public sealed class CollectionFiles : IDisposable
{
    readonly CollectionInfo info;
    readonly string root;
    ZipArchive zip;   // opened at the first file needed (reading its directory takes a moment)

    public string ZipPath { get; }
    public string CacheFolder { get; }
    public bool IsDownloaded => File.Exists(ZipPath);

    public CollectionFiles(string libraryFolder, CollectionInfo info)
    {
        this.info = info;
        root = info.Root.Trim('/');
        ZipPath = Path.Combine(libraryFolder, info.ZipName);
        CacheFolder = Path.Combine(libraryFolder, "cache", info.Id);
    }

    public void Dispose() => Close();

    void Close()
    {
        zip?.Dispose();
        zip = null;
    }

    // The file on the disk, taken from the zip first if needed; throws if the zip is missing, or the file different from
    // the one of the database.
    public string Get(LibraryFile file)
    {
        var local = OnDisk(file.Path);
        if (File.Exists(local) && new FileInfo(local).Length == file.Size) return local;   // taken before
        if (!IsDownloaded) throw new FileNotFoundException($"{info.Name}: files not downloaded yet");

        zip ??= ZipFile.OpenRead(ZipPath);
        var entry = zip.GetEntry(root.Length > 0 ? root + "/" + file.Path : file.Path)
                    ?? throw new FileNotFoundException($"File not found in {info.ZipName}: {file.Path}");
        byte[] bytes;
        using (var stream = entry.Open())
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }
        if (Convert.ToHexStringLower(SHA1.HashData(bytes)) != file.Sha1)
            throw new InvalidDataException($"File of zip differs from the one of the library database: {file.Path}");
        Directory.CreateDirectory(Path.GetDirectoryName(local));
        File.WriteAllBytes(local, bytes);
        return local;
    }

    // Where a file of the collection goes once taken from the zip.
    public string OnDisk(string path) => Path.Combine(CacheFolder, path.Replace('/', Path.DirectorySeparatorChar));

    // The path of a file of the collection from its place on the disk (a file of the playlist), or null.
    public string PathOf(string local)
    {
        var folder = Path.GetFullPath(CacheFolder) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(local);
        return full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? full[folder.Length..].Replace(Path.DirectorySeparatorChar, '/') : null;
    }

    const int Attempts = 4;

    // Downloads the source zip (to <zip>.part, kept as the zip once its size and SHA-1 are the ones of the database).
    // A server that sends nothing for a while (stall: 30 s by default) is left, and the download goes on with a new
    // request (from where it stopped; archive.org may then send it to another of its servers), up to 4 attempts.
    // progress: bytes received so far, out of the size of the zip; notice: what is happening (connecting, trying again).
    public async Task DownloadAsync(HttpClient http, IProgress<(long Done, long Total)> progress, CancellationToken cancel,
                                    Action<string> notice = null, TimeSpan? stall = null)
    {
        var part = ZipPath + ".part";
        var host = new Uri(info.SourceUrl).Host;
        Directory.CreateDirectory(Path.GetDirectoryName(ZipPath));
        try
        {
            if (File.Exists(part)) File.Delete(part);
            notice?.Invoke($"Connecting to {host}…");
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await Attempt(http, part, progress, cancel, stall ?? TimeSpan.FromSeconds(30));
                    break;
                }
                catch (Exception ex) when (!cancel.IsCancellationRequested && attempt < Attempts &&
                                           ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    notice?.Invoke($"No answer from {host}: trying again ({attempt + 1}/{Attempts})…");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancel);
                }
            }
            var problem = await Task.Run(() => CheckZip(part, info), cancel);
            if (problem != null) throw new InvalidDataException($"Download of {info.ZipName} is not the expected file: {problem}");
            Close();
            File.Move(part, ZipPath, true);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            try { File.Delete(part); } catch { }
            throw new TimeoutException($"{host} does not answer: try again later");
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }

    // One request: from the start, or from the end of the part already received (if the server sends ranges).
    async Task Attempt(HttpClient http, string part, IProgress<(long Done, long Total)> progress, CancellationToken cancel, TimeSpan stall)
    {
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, info.SourceUrl);
        if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancel);   // cancelled when nothing comes for a while
        watchdog.CancelAfter(stall);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token);
        response.EnsureSuccessStatusCode();
        bool resumed = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        using var source = await response.Content.ReadAsStreamAsync(watchdog.Token);
        using var target = new FileStream(part, resumed ? FileMode.Append : FileMode.Create);
        long done = resumed ? have : 0;
        progress?.Report((done, info.SourceSize));
        var buffer = new byte[1 << 16];
        while (true)
        {
            watchdog.CancelAfter(stall);
            int n = await source.ReadAsync(buffer, watchdog.Token);
            if (n == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, n), cancel);
            done += n;
            progress?.Report((done, info.SourceSize));
        }
        if (done < info.SourceSize) throw new IOException($"Download stopped at {done} of {info.SourceSize} bytes");
    }
    // Deletes the zip and the files taken from it (downloaded again when needed).
    public void DeleteDownloaded()
    {
        Close();
        if (File.Exists(ZipPath)) File.Delete(ZipPath);
        if (Directory.Exists(CacheFolder)) Directory.Delete(CacheFolder, true);
    }

    // A zip is the source of a collection: same size and SHA-1 (null), or why not.
    public static string CheckZip(string zipPath, CollectionInfo info)
    {
        var size = new FileInfo(zipPath).Length;
        if (size != info.SourceSize) return $"size {size} bytes instead of {info.SourceSize}";
        using var stream = File.OpenRead(zipPath);
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(stream));
        return sha1 == info.SourceSha1 ? null : $"SHA-1 {sha1} instead of {info.SourceSha1}";
    }
}
