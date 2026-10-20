using System.Collections;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Input;
using System.Windows.Threading;
using PowerMIDI.Core;
using PowerMIDI.Library;

namespace PowerMIDI.ViewModels;

// A file of the playlist that comes from a collection of the Library tab: its plays are counted there.
public sealed record LibraryKey(string CollectionId, string Path);

// A row of the Library list: a folder (with its number of files), or a file.
public sealed class LibraryRow : ObservableObject
{
    public LibraryFolder Folder { get; init; }
    public LibraryFile File { get; init; }
    public bool IsFolder => Folder != null;
    public string Icon => IsFolder ? "\uF12B" : "\uEC4F";   // Segoe MDL2 Assets: folder, music note
    public string Title { get; init; }
    public string FolderText { get; init; }   // the folder of a file (search and played lists)
    public string Format { get; init; }
    public string Time { get; init; }
    public string ToolTip { get; init; }
    public bool Unreadable => File?.Error != null;

    bool favorite;
    public bool Favorite { get => favorite; set { if (Set(ref favorite, value)) OnPropertyChanged(nameof(Star)); } }
    public string Star => IsFolder ? "" : favorite ? "★" : "☆";
}

// A folder of the breadcrumb (Prefix: the separator before it, none before the collection).
public sealed record LibraryCrumb(string Text, string Path, string Prefix);

// Library tab: the collections of MIDI files of the library database (library\library.db, next to the program), browsed
// by folder or searched, filtered by standard and by what was played. The files of a collection come from its source zip,
// downloaded into the library folder when the user asks for it; a file opened (double click, Enter) is taken from the zip,
// added to the playlist and played.
public sealed class LibraryViewModel : ObservableObject
{
    public static readonly string[] FormatNames = { "All", "GS", "XG", "GM", "GM2", "MT-32" };
    public static readonly string[] ShowNames = { "All", "Favorites", "Recently played", "Most played", "Not played yet" };
    const int FlatLimit = 2000;   // files listed at most by a search or a played list

    // Downloads of the source zips (archive.org asks for a name it can tell).
    static readonly HttpClient http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var version = typeof(LibraryViewModel).Assembly.GetName().Version;
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PowerMIDI/{version.Major}.{version.Minor}.{version.Build} (+https://github.com/bakuredo/PowerMIDI)");
        return client;
    }

    readonly IMainView view;
    readonly Action<string> setStatus;
    readonly Action<string, LibraryKey> play;              // plays a file of a collection (added to the playlist if needed)
    readonly Action<IReadOnlyList<string>> addToPlaylist;
    readonly string libraryFolder;
    readonly DispatcherTimer searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    LibraryUserData userData;
    CollectionCatalog catalog;
    CollectionFiles files;
    string folder = "";
    string savedCollection;   // PowerMIDI.ini, until the collections are read

    public LibraryViewModel(IMainView view, string libraryFolder, Action<string> setStatus, Action<string, LibraryKey> play,
                            Action<IReadOnlyList<string>> addToPlaylist)
    {
        this.view = view;
        this.libraryFolder = libraryFolder;
        this.setStatus = setStatus;
        this.play = play;
        this.addToPlaylist = addToPlaylist;
        searchDelay.Tick += (_, _) => { searchDelay.Stop(); Run(Refresh); };
        DownloadCommand = new RelayCommand(() => Download(null));
        CancelDownloadCommand = new RelayCommand(() => downloadCancel?.Cancel());
        DeleteDownloadedCommand = new RelayCommand(() => Run(DeleteDownloaded));
        UpCommand = new RelayCommand(() => Run(Up));
        NavigateCommand = new RelayCommand(p => Run(() => { if (p is string path) Navigate(path); }));
        OpenCommand = new RelayCommand(p => Run(() => Open(p as LibraryRow)));
        PlayCommand = new RelayCommand(p => Run(() => Open(Selected(p).FirstOrDefault(r => !r.IsFolder))));
        AddToPlaylistCommand = new RelayCommand(p => Run(() => AddToPlaylist(Selected(p))));
        FavoriteCommand = new RelayCommand(p => Run(() => ToggleFavorite(p as LibraryRow ?? Selected(p).FirstOrDefault())));
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
    }

    public ICommand DownloadCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand DeleteDownloadedCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand NavigateCommand { get; }        // parameter: folder path (breadcrumb)
    public ICommand OpenCommand { get; }            // parameter: row (double click, Enter): folder entered, file played
    public ICommand PlayCommand { get; }            // parameters: selected rows
    public ICommand AddToPlaylistCommand { get; }
    public ICommand FavoriteCommand { get; }
    public ICommand ClearSearchCommand { get; }

    void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { view.ShowError(ex); }
    }

    static IReadOnlyList<LibraryRow> Selected(object parameter) => parameter switch
    {
        LibraryRow row => new[] { row },
        IList list => list.OfType<LibraryRow>().ToList(),
        _ => Array.Empty<LibraryRow>(),
    };

    // ---------- Collections ----------

    string DatabasePath => Path.Combine(libraryFolder, "library.db");
    string UserDatabasePath => Path.Combine(libraryFolder, "user.db");

    public ObservableCollection<CollectionInfo> Collections { get; } = new();

    CollectionInfo selectedCollection;
    public CollectionInfo SelectedCollection
    {
        get => selectedCollection;
        set { if (Set(ref selectedCollection, value)) Run(() => OpenCollection(value, "")); }
    }

    public bool HasCollection => catalog != null;
    public bool HasNoLibrary => Collections.Count == 0;

    // At startup: the collections of the library database, the one of the last time selected.
    public void Load()
    {
        Collections.Clear();
        if (File.Exists(DatabasePath))
        {
            using var connection = LibraryFormat.OpenLibrary(DatabasePath)
                                   ?? throw new InvalidDataException($"{DatabasePath}: library database of another version");
            foreach (var c in LibraryFormat.ReadCollections(connection)) Collections.Add(c);
        }
        OnPropertyChanged(nameof(HasNoLibrary));
        var choice = Collections.FirstOrDefault(c => c.Id == savedCollection) ?? Collections.FirstOrDefault();
        selectedCollection = choice;
        OnPropertyChanged(nameof(SelectedCollection));
        OpenCollection(choice, "");   // from its root at each start
    }

    void OpenCollection(CollectionInfo collection, string startFolder)
    {
        if (IsDownloading) downloadCancel?.Cancel();
        files?.Dispose();
        catalog?.Dispose();
        files = null;
        catalog = null;
        if (collection != null)
        {
            UserDatabase();   // created first: the catalog attaches it
            catalog = CollectionCatalog.Open(DatabasePath, collection.Id, UserDatabasePath)
                      ?? throw new InvalidDataException($"Collection {collection.Id} missing from {DatabasePath}");
            files = new CollectionFiles(libraryFolder, collection);
        }
        OnPropertyChanged(nameof(HasCollection));
        UpdateDownload();
        folder = catalog != null && startFolder != null && catalog.Folders.ContainsKey(startFolder) ? startFolder : "";
        Refresh();
    }

    LibraryUserData UserDatabase() => userData ??= LibraryUserData.Open(UserDatabasePath);

    // ---------- Download of the files ----------

    // The source zip of the collection shown is in the library folder (otherwise its files cannot be played).
    public bool IsDownloaded => files?.IsDownloaded == true;
    public bool NeedsDownload => files != null && !files.IsDownloaded && !IsDownloading;

    bool downloading;
    public bool IsDownloading { get => downloading; private set { if (Set(ref downloading, value)) UpdateDownload(); } }

    double downloadProgress;
    public double DownloadProgress { get => downloadProgress; private set => Set(ref downloadProgress, value); }

    string downloadText = "";
    public string DownloadText { get => downloadText; private set { if (Set(ref downloadText, value)) OnPropertyChanged(nameof(ShowDownloadBar)); } }
    public bool ShowDownloadBar => downloadText != "";

    CancellationTokenSource downloadCancel;

    static string Megabytes(long bytes) => $"{bytes / 1024.0 / 1024:0} MB";

    void UpdateDownload()
    {
        if (files != null && !downloading)
            DownloadText = files.IsDownloaded ? "" : $"Files not downloaded yet: {Megabytes(selectedCollection.SourceSize)} from {new Uri(selectedCollection.SourceUrl).Host}";
        OnPropertyChanged(nameof(IsDownloaded));
        OnPropertyChanged(nameof(NeedsDownload));
    }

    // Downloads the zip of the collection shown (with its progress; Cancel stops it), then plays the file asked for, if any.
    async void Download(LibraryRow then)
    {
        if (files == null || downloading || files.IsDownloaded) return;
        var collection = selectedCollection;
        var target = files;
        downloadCancel = new CancellationTokenSource();
        IsDownloading = true;
        DownloadProgress = 0;
        DownloadText = $"Downloading {collection.Name}…";
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            DownloadProgress = p.Total > 0 ? (double)p.Done / p.Total : 0;
            DownloadText = $"Downloading: {Megabytes(p.Done)} / {Megabytes(p.Total)}";
        });
        try
        {
            await target.DownloadAsync(http, progress, downloadCancel.Token);
            setStatus($"{collection.Name}: files downloaded ({collection.ZipName})");
            IsDownloading = false;
            if (then != null && files == target) Open(then);
        }
        catch (OperationCanceledException) { setStatus($"{collection.Name}: download cancelled"); }
        catch (Exception ex) { view.ShowError(new IOException($"Download of {collection.Name} failed: {ex.Message}", ex)); }
        finally
        {
            downloadCancel = null;
            IsDownloading = false;
            UpdateDownload();
        }
    }

    // Deletes the downloaded zip of the collection shown and the files taken from it (the playlist keeps them only once
    // taken again).
    void DeleteDownloaded()
    {
        if (files == null || !files.IsDownloaded && !Directory.Exists(files.CacheFolder)) return;
        if (view.AskYesNoCancel($"Delete downloaded files of {selectedCollection.Name}?\n{files.ZipPath} ({Megabytes(selectedCollection.SourceSize)}) and the files taken from it.\nThey can be downloaded again.") != true) return;
        files.DeleteDownloaded();
        UpdateDownload();
        setStatus($"{selectedCollection.Name}: downloaded files deleted");
    }

    // ---------- Settings (PowerMIDI.ini) ----------

    public void LoadSettings(IniFile ini)
    {
        savedCollection = ini.Get("Library", "Collection");
    }

    public void WriteSettings(IniFile ini)
    {
        ini.Set("Library", "Collection", selectedCollection?.Id ?? "");
    }

    // ---------- Navigation ----------

    IReadOnlyList<LibraryCrumb> crumbs = Array.Empty<LibraryCrumb>();
    public IReadOnlyList<LibraryCrumb> Crumbs { get => crumbs; private set => Set(ref crumbs, value); }

    void Navigate(string path)
    {
        if (catalog == null || !catalog.Folders.ContainsKey(path)) return;
        folder = path;
        Refresh();
    }

    void Up()
    {
        if (folder == "") return;
        int slash = folder.LastIndexOf('/');
        Navigate(slash >= 0 ? folder[..slash] : "");
    }

    // ---------- Filters ----------

    string searchText = "";
    public string SearchText
    {
        get => searchText;
        set
        {
            if (!Set(ref searchText, value ?? "")) return;
            searchDelay.Stop();
            searchDelay.Start();   // listed again once the typing stops
        }
    }

    int formatIndex, showIndex;
    public int FormatIndex { get => formatIndex; set { if (Set(ref formatIndex, Math.Max(0, value))) Run(Refresh); } }
    public int ShowIndex { get => showIndex; set { if (Set(ref showIndex, Math.Max(0, value))) Run(Refresh); } }

    LibraryFilter Filter => new(formatIndex > 0 ? FormatNames[formatIndex] : null, (LibraryShow)showIndex, searchText);

    // Search or played lists: the files of the folder and of its subfolders, with their folder.
    bool flat;
    public bool IsFlat { get => flat; private set { if (Set(ref flat, value)) { OnPropertyChanged(nameof(FolderColumnWidth)); OnPropertyChanged(nameof(TitleColumnWidth)); } } }
    public double FolderColumnWidth => flat ? 110 : 0;
    public double TitleColumnWidth => flat ? 170 : 280;

    // ---------- List ----------

    IReadOnlyList<LibraryRow> rows = Array.Empty<LibraryRow>();
    public IReadOnlyList<LibraryRow> Rows { get => rows; private set => Set(ref rows, value); }

    string countText = "";
    public string CountText { get => countText; private set => Set(ref countText, value); }

    string sortColumn = "Title";
    bool sortDescending;

    // Click on a column header: sorted by it (again: the other way round); the folders stay first.
    public void SortBy(string column)
    {
        if (column is not ("Title" or "Folder" or "Format" or "Time" or "★")) return;
        sortDescending = column == sortColumn && !sortDescending;
        sortColumn = column;
        Rows = Sorted(rows);
    }

    IReadOnlyList<LibraryRow> Sorted(IEnumerable<LibraryRow> list)
    {
        // Played lists: in their own order (last played, most played) until a column is chosen.
        if (sortColumn == "Title" && !sortDescending && (LibraryShow)showIndex is LibraryShow.RecentlyPlayed or LibraryShow.MostPlayed)
            return list.ToList();
        Func<LibraryRow, object> key = sortColumn switch
        {
            "Folder" => r => r.FolderText ?? "",
            "Format" => r => r.Format ?? "",
            "Time" => r => r.IsFolder ? r.Folder.FileCount : r.File.DurationMs ?? -1,
            "★" => r => r.Favorite ? 0 : 1,
            _ => r => r.Title,
        };
        var comparer = Comparer<object>.Create((a, b) => a is string x && b is string y ? StringComparer.CurrentCultureIgnoreCase.Compare(x, y)
                                                                                      : Comparer<object>.Default.Compare(a, b));
        var folders = list.Where(r => r.IsFolder).OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase);
        var filesSorted = sortDescending ? list.Where(r => !r.IsFolder).OrderByDescending(key, comparer) : list.Where(r => !r.IsFolder).OrderBy(key, comparer);
        return folders.Concat(filesSorted.ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)).ToList();
    }

    // Lists the folder again with the filters: its folders (those with files shown) and its files; with a search or a
    // played list, the files of the folder and of its subfolders.
    public void Refresh()
    {
        if (catalog == null)
        {
            Crumbs = Array.Empty<LibraryCrumb>();
            Rows = Array.Empty<LibraryRow>();
            CountText = Collections.Count == 0 ? "No library" : "";
            IsFlat = false;
            return;
        }
        var filter = Filter;
        var crumbList = new List<LibraryCrumb> { new(catalog.Info.Name, "", "") };
        if (folder != "")
        {
            var parts = folder.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                var path = string.Join('/', parts.Take(i + 1));
                crumbList.Add(new LibraryCrumb(catalog.Folders.TryGetValue(path, out var f) ? f.Shown : parts[i], path, " › "));
            }
        }
        Crumbs = crumbList;

        IsFlat = filter.Show != LibraryShow.All || LibraryFilterHasSearch(filter);
        var list = new List<LibraryRow>();
        int folderCount = 0;
        List<LibraryFile> found;
        if (IsFlat)
        {
            found = catalog.Files(folder, true, filter, FlatLimit + 1);
        }
        else
        {
            var counts = filter.IsEmpty ? null : catalog.Counts(filter);
            foreach (var child in catalog.Children(folder))
            {
                int count = counts == null ? child.FileCount : counts.GetValueOrDefault(child.Path);
                if (count == 0) continue;
                list.Add(new LibraryRow
                {
                    Folder = child, Title = child.Shown, Time = count.ToString(),
                    ToolTip = $"{child.Path}\n{count} file{(count > 1 ? "s" : "")}" + (child.DisplayName != null ? $" · folder {child.Name}" : ""),
                });
                folderCount++;
            }
            found = catalog.Files(folder, false, filter);
        }
        bool more = found.Count > FlatLimit && IsFlat;
        foreach (var f in found.Take(IsFlat ? FlatLimit : found.Count)) list.Add(FileRow(f));
        Rows = Sorted(list);
        int fileCount = Math.Min(found.Count, IsFlat ? FlatLimit : found.Count);
        CountText = IsFlat ? (more ? $"First {FlatLimit} files shown" : $"{fileCount} file{(fileCount != 1 ? "s" : "")}")
                           : $"{folderCount} folder{(folderCount != 1 ? "s" : "")} · {fileCount} file{(fileCount != 1 ? "s" : "")}";
    }

    static bool LibraryFilterHasSearch(LibraryFilter filter) => !string.IsNullOrWhiteSpace(filter.Search) && filter.Search.Any(char.IsLetterOrDigit);

    LibraryRow FileRow(LibraryFile f)
    {
        var tip = new List<string> { f.Path };
        if (f.InternalTitle != null && f.InternalTitle != f.Title) tip.Add($"Title in file: {f.InternalTitle}");
        if (f.Copyright != null) tip.Add($"Copyright: {f.Copyright}");
        if (f.Error != null) tip.Add($"Unreadable: {f.Error}");
        else
        {
            var channels = Enumerable.Range(0, 16).Where(c => ((f.Channels ?? 0) >> c & 1) != 0).Select(c => (c + 1).ToString()).ToList();
            tip.Add($"{f.Tracks} track{(f.Tracks != 1 ? "s" : "")} · channel{(channels.Count != 1 ? "s" : "")} {string.Join(", ", channels)}");
            var extras = new List<string>();
            if (f.HasLyrics) extras.Add("Karaoke");
            if (f.HasLoop) extras.Add("Loop");
            if (extras.Count > 0) tip.Add(string.Join(" · ", extras));
        }
        if (f.PlayCount > 0) tip.Add($"Played {f.PlayCount} time{(f.PlayCount > 1 ? "s" : "")}, last {LocalTime(f.LastPlayed)}");
        var folderRow = catalog.Folders.TryGetValue(f.FolderPath, out var fo) ? fo.Shown : f.FolderPath;
        return new LibraryRow
        {
            File = f, Title = f.Title, FolderText = folderRow, Format = f.Standard ?? "?",
            Time = f.DurationMs is long ms ? FormatDuration(ms) : "", ToolTip = string.Join("\n", tip), Favorite = f.Favorite,
        };
    }

    static string FormatDuration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    static string LocalTime(string utc) =>
        DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("g") : utc;

    // ---------- Actions ----------

    // Double click or Enter: a folder is entered; a file is taken from the zip, added to the playlist and played.
    void Open(LibraryRow row)
    {
        if (row == null) return;
        if (row.IsFolder) { searchText = ""; OnPropertyChanged(nameof(SearchText)); Navigate(row.Folder.Path); return; }
        if (row.Unreadable) { setStatus($"Unreadable file: {row.File.Error}"); return; }
        if (!IsAvailable(row)) { OfferDownload(row); return; }
        var local = files.Get(row.File);
        play(local, new LibraryKey(selectedCollection.Id, row.File.Path));
    }

    // A file can be played: taken from the zip before, or the zip is downloaded.
    bool IsAvailable(LibraryRow row) => files.IsDownloaded || File.Exists(files.OnDisk(row.File.Path));

    // Its zip is missing: downloaded if the user wants (the file played then).
    void OfferDownload(LibraryRow then)
    {
        if (downloading) { setStatus("Download in progress: file playable once it ends"); return; }
        if (view.AskYesNoCancel($"Download files of {selectedCollection.Name} ({Megabytes(selectedCollection.SourceSize)}) from {new Uri(selectedCollection.SourceUrl).Host}?") == true)
            Download(then);
    }

    void AddToPlaylist(IReadOnlyList<LibraryRow> selected)
    {
        var chosen = selected.Where(r => !r.IsFolder && !r.Unreadable).ToList();
        if (chosen.Count == 0) return;
        if (chosen.Any(r => !IsAvailable(r))) { OfferDownload(null); return; }
        view.SetBusy(true);
        List<string> paths;
        try { paths = chosen.Select(r => files.Get(r.File)).ToList(); }
        finally { view.SetBusy(false); }
        addToPlaylist(paths);
    }

    // Files dragged to the playlist: taken from the zip first (null: none).
    public IReadOnlyList<string> FilesFor(IList selected)
    {
        var chosen = Selected(selected).Where(r => !r.IsFolder && !r.Unreadable).ToList();
        if (chosen.Count == 0) return null;
        if (chosen.Any(r => !IsAvailable(r))) { OfferDownload(null); return null; }
        try { return chosen.Select(r => files.Get(r.File)).ToList(); }
        catch (Exception ex) { view.ShowError(ex); return null; }
    }

    void ToggleFavorite(LibraryRow row)
    {
        if (row == null || row.IsFolder || selectedCollection == null) return;
        row.Favorite = !row.Favorite;
        UserDatabase().SetFavorite(selectedCollection.Id, row.File.Path, row.Favorite);
        if ((LibraryShow)showIndex == LibraryShow.Favorites) Refresh();
    }

    // ---------- Plays ----------

    // The playlist item of a file: its collection and path if it belongs to the collection shown (taken from its zip).
    public LibraryKey KeyOf(string local) => files?.PathOf(local) is { } path && selectedCollection != null ? new LibraryKey(selectedCollection.Id, path) : null;

    // A file of a collection starts playing: counted, and shown as played.
    public void RecordPlay(LibraryKey key)
    {
        try
        {
            UserDatabase().RecordPlay(key.CollectionId, key.Path);
            if (selectedCollection?.Id == key.CollectionId && (LibraryShow)showIndex is not LibraryShow.All and not LibraryShow.Favorites) Refresh();
        }
        catch { }   // the count is not worth an error
    }
}
