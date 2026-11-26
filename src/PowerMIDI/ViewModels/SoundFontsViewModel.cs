using System.Diagnostics;
using System.Windows.Input;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// The SoundFonts: those of the soundfonts folder (next to the program, or the one of PowerMIDI.ini, see
// SoundFontFinder.FolderOf), the one played by the SoundFont engine (MIDI OUT "SoundFont engine (FluidSynth)") and what
// it says about itself, the one chosen for the files without their own (remembered in PowerMIDI.ini); the settings of the
// engine and of the SF_BANK tag (SoundFonts tab). Their list is shown in place of the mode while the engine is the port.
public sealed class SoundFontsViewModel : ObservableObject
{
    readonly Action<string> chosen;   // a file chosen in the list (its path): the engine plays it if it is the port
    readonly Action<bool> engineChanged;   // a setting of the engine changed: restart (true) or taken at once (false)
    string folderSetting = "";   // the folder as PowerMIDI.ini gives it ("": the default one), written back as it is
    bool updating;            // list refreshed or settings read: not a choice by hand

    public SoundFontsViewModel(Action<string> chosen, Action<bool> engineChanged)
    {
        Folder = SoundFontFinder.FolderOf("", AppContext.BaseDirectory);
        this.chosen = chosen;
        this.engineChanged = engineChanged;
        RefreshCommand = new RelayCommand(Refresh);
        RandomCommand = new RelayCommand(ChooseRandom);
        FavoriteCommand = new RelayCommand(ToggleFavorite);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        Refresh();
    }

    public ICommand RandomCommand { get; }

    // A SoundFont of the folder at random (another one than the one shown), chosen as in the list: played at once.
    void ChooseRandom()
    {
        var others = items.Where(i => i.InFolder && i != selectedItem).ToList();
        if (others.Count > 0) SelectedItem = others[Random.Shared.Next(others.Count)];
    }

    // ---------- Favorites ----------
    // Files of the folder (by name) shown first in the list, with a star; the one shown added or removed by the Favorite
    // button. Kept in PowerMIDI.ini (Favorites, the names separated by |), also those gone from the folder (they may come
    // back: another drive plugged in…).
    readonly HashSet<string> favorites = new(StringComparer.OrdinalIgnoreCase);
    public ICommand FavoriteCommand { get; }

    // The one shown is a favorite; only a file of the folder can be one (not the one found next to the MIDI file).
    public bool IsFavorite => selectedItem?.IsFavorite == true;
    public bool CanFavorite => selectedItem?.InFolder == true;
    public string FavoriteToolTip => !CanFavorite ? "Favorites: SoundFonts of folder only"
        : IsFavorite ? "Remove from favorites" : "Add to favorites (shown at top of list)";

    void ToggleFavorite()
    {
        if (selectedItem is not { InFolder: true } item) return;
        var name = Path.GetFileName(item.Path);
        if (!favorites.Remove(name)) favorites.Add(name);
        Rebuild();
    }

    // The selection changed (by hand or by Rebuild): what depends on it.
    void SelectionShown()
    {
        OnPropertyChanged(nameof(SelectedToolTip));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(CanFavorite));
        OnPropertyChanged(nameof(FavoriteToolTip));
    }

    string folder;
    public string Folder { get => folder; private set { if (Set(ref folder, value)) OnPropertyChanged(nameof(FolderToolTip)); } }
    public string FolderToolTip => $"Open soundfonts folder (place for .sf2 files): {folder}";
    public ICommand RefreshCommand { get; }
    public ICommand OpenFolderCommand { get; }

    // ---------- The list ----------
    // A SoundFont of the list: its path, its text, whether it is a file of the soundfonts folder (otherwise the one
    // played, found elsewhere: next to the MIDI file, by its tag…), and whether it is a favorite (a star before it).
    public sealed record Item(string Path, string Text, bool InFolder, bool IsFavorite = false);

    List<string> folderFiles = new();   // the .sf2 files of the folder, by name
    string defaultFile;                 // the one chosen for the files without their own (a file of the folder)
    string usedPath, usedFrom;          // the one the engine plays (or loads), and how it was found (null: none)

    IReadOnlyList<Item> items = Array.Empty<Item>();
    public IReadOnlyList<Item> Items { get => items; private set { if (Set(ref items, value)) OnPropertyChanged(nameof(HasFiles)); } }
    public bool HasFiles => items.Count > 0;

    // Shown: the SoundFont played; without one, the one chosen. A file of the folder chosen by hand: the one chosen from
    // now on, played at once. A list given new items may give back null for a moment: ignored.
    Item selectedItem;
    public Item SelectedItem
    {
        get => selectedItem;
        set
        {
            if (value == null || !Set(ref selectedItem, value)) return;
            ReadInfo();
            SelectionShown();
            if (updating || !value.InFolder) return;
            defaultFile = Path.GetFileName(value.Path);
            chosen(value.Path);
        }
    }

    public string SelectedToolTip => selectedItem == null ? "SoundFont"
        : $"SoundFont: {selectedItem.Path}" + (usedPath != null && SamePath(selectedItem.Path, usedPath) ? $"\nPlayed ({usedFrom})" : "\nChosen in list");

    // Full path of the SoundFont chosen for the files without their own (null: none).
    public string DefaultPath => defaultFile == null ? null : Path.Combine(Folder, defaultFile);

    // The SoundFont the engine plays (or loads), and how it was found (null: none, the engine stopped).
    public void SetUsed(string path, string from)
    {
        (usedPath, usedFrom) = (path, from);
        Rebuild();
    }

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // The files of the folder (the favorites first, each part by name), and the one played if it is not one of them
    // (found by the MIDI file: "auto", how in its tooltip); the one played shown (otherwise the one chosen), not taken
    // for a choice.
    void Rebuild()
    {
        var list = folderFiles.Select(n => new Item(Path.Combine(Folder, n), n, true, favorites.Contains(n)))
                              .OrderBy(i => i.IsFavorite ? 0 : 1).ToList();   // (stable: by name within each part)
        if (usedPath != null && !list.Any(i => SamePath(i.Path, usedPath)))
            list.Add(new Item(usedPath, $"{Path.GetFileName(usedPath)} (auto)", false));
        var shown = (usedPath != null ? list.FirstOrDefault(i => SamePath(i.Path, usedPath)) : null)
                    ?? list.FirstOrDefault(i => i.InFolder && string.Equals(Path.GetFileName(i.Path), defaultFile, StringComparison.OrdinalIgnoreCase));
        updating = true;
        try
        {
            Items = list;
            selectedItem = shown;
            OnPropertyChanged(nameof(SelectedItem));
            SelectionShown();
            ReadInfo();   // also the same file again (it may have been replaced)
        }
        finally { updating = false; }
    }

    // The SF_BANK tag of the MIDI files (see SoundFontFinder): read when a file is played (its SoundFont), written by
    // Save MIDI file in SF2 mode (the SoundFont played). Both on by default.
    bool readTag = true, writeTag = true;
    public bool ReadTag { get => readTag; set => Set(ref readTag, value); }
    public bool WriteTag { get => writeTag; set => Set(ref writeTag, value); }

    // The SoundFont engine is the MIDI OUT (otherwise the tab shows only a message).
    bool isEngine;
    public bool IsEngine { get => isEngine; set => Set(ref isEngine, value); }

    // State of the engine, set by the main view model: while it runs, the number of voices playing ("—" otherwise); its
    // tooltip (stopped, loading; running: SoundFont, how it was found, FluidSynth, audio output); and its delay while
    // it runs, in the tooltip of Buffer.
    string engineState = "—";
    public string EngineState { get => engineState; set => Set(ref engineState, value); }
    string engineToolTip = "Engine stopped";
    public string EngineToolTip { get => engineToolTip; set => Set(ref engineToolTip, value); }
    string delayText;   // e.g. "~42 ms"; null: the engine does not run
    public string DelayText { get => delayText; set { if (Set(ref delayText, value)) OnPropertyChanged(nameof(BufferToolTip)); } }
    public string BufferToolTip => "Smaller: less delay, but crackles if computer is too busy (change restarts engine)" +
                                   (delayText != null ? $"\nDelay measured now (event to sound): {delayText}" : "");

    // ---------- Settings of the engine (SoundFontEngineOptions) ----------
    // A list item: its text and its value (string, int or bool).
    public sealed record Choice(string Text, object Value);

    SoundFontEngineOptions options = new();
    public SoundFontEngineOptions Options => options;

    public IReadOnlyList<Choice> ReverbEngines { get; } = SoundFontEngineOptions.ReverbEngines.Select(x => new Choice(x.Text, x.Value)).ToList();
    public IReadOnlyList<Choice> Interpolations { get; } = SoundFontEngineOptions.Interpolations.Select(x => new Choice(x.Text, x.Value)).ToList();
    public IReadOnlyList<Choice> Polyphonies { get; } = SoundFontEngineOptions.Polyphonies.Select(n => new Choice($"{n} voices", n)).ToList();
    public IReadOnlyList<Choice> SampleLoadings { get; } = new[] { new Choice("Preload all", false), new Choice("On demand", true) };
    public IReadOnlyList<Choice> Drivers { get; } = SoundFontEngineOptions.Drivers.Select(x => new Choice(x.Text, x.Value)).ToList();
    public IReadOnlyList<Choice> SampleRates { get; } =
        SoundFontEngineOptions.SampleRates.Select(r => new Choice($"{r / 1000.0:0.#} kHz".Replace(',', '.'), r)).ToList();

    // A list given new items may give back null for a moment: ignored (the setting stays).
    public string ReverbEngine { get => options.ReverbEngine; set { if (value != null) Change(options with { ReverbEngine = value }); } }
    public int Interpolation { get => options.Interpolation; set => Change(options with { Interpolation = value }); }
    public int Polyphony { get => options.Polyphony; set => Change(options with { Polyphony = value }); }
    public bool LoadOnDemand { get => options.LoadOnDemand; set => Change(options with { LoadOnDemand = value }); }
    public int SampleRate { get => options.SampleRate; set => Change(options with { SampleRate = value }); }
    public int Buffer { get => options.Buffer; set => Change(options with { Buffer = value }); }
    public string Device { get => options.Device; set { if (value != null) Change(options with { Device = value }); } }

    // Gain: slider 0-2, in steps of 0.05.
    public double Gain { get => options.Gain; set => Change(options with { Gain = Math.Clamp(Math.Round(value * 20) / 20, 0, 2) }); }
    public string GainText => options.Gain.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    // Another driver: its devices; the device chosen if it has it, otherwise the default one.
    public string Driver
    {
        get => options.Driver;
        set
        {
            if (value == null || value == options.Driver) return;
            var names = DeviceNames(value);
            Change(options with { Driver = value, Device = names.Contains(options.Device) ? options.Device : "default" });
        }
    }

    // Devices of the driver chosen (asked to FluidSynth when the list is first shown: its library is only loaded then).
    // A device chosen earlier and gone (unplugged) stays in the list, marked missing.
    public IReadOnlyList<Choice> Devices
    {
        get
        {
            var names = DeviceNames(options.Driver);
            var list = names.Select(n => new Choice(n == "default" ? "Default" : n, n)).ToList();
            if (!names.Contains(options.Device)) list.Add(new Choice($"{options.Device} (missing)", options.Device));
            return list;
        }
    }

    readonly Dictionary<string, IReadOnlyList<string>> deviceNames = new();

    IReadOnlyList<string> DeviceNames(string driver)
    {
        if (deviceNames.TryGetValue(driver, out var names)) return names;
        try { names = SoundFontOutput.IsAvailable ? SoundFontOutput.Devices(driver) : null; }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException) { names = null; }
        if (names == null || names.Count == 0) names = new[] { "default" };
        return deviceNames[driver] = names;
    }

    // Buffer sizes, with their length for the driver and sample rate chosen.
    public IReadOnlyList<Choice> Buffers =>
        SoundFontEngineOptions.Buffers.Select(n => new Choice($"{n} periods (~{SoundFontEngineOptions.BufferMs(options.Driver, n, options.SampleRate):0} ms)", n)).ToList();

    void Change(SoundFontEngineOptions next)
    {
        if (next == options) return;
        bool restart = next.NeedsRestart(options);
        var before = options;
        options = next;
        // The lists that depend on the driver (devices, buffer lengths) or the sample rate (buffer lengths) first.
        if (next.Driver != before.Driver) OnPropertyChanged(nameof(Devices));
        if (next.Driver != before.Driver || next.SampleRate != before.SampleRate) OnPropertyChanged(nameof(Buffers));
        foreach (var name in new[] { nameof(ReverbEngine), nameof(Interpolation), nameof(Polyphony), nameof(LoadOnDemand), nameof(SampleRate),
                                     nameof(Buffer), nameof(Gain), nameof(GainText), nameof(Driver), nameof(Device) })
            OnPropertyChanged(name);
        if (!updating) engineChanged(restart);
    }

    // ---------- What the file shown says about itself ----------
    string infoName, infoAuthor, infoCopyright, infoDetails, infoComment;
    public string InfoName { get => infoName; private set => Set(ref infoName, value); }
    public string InfoAuthor { get => infoAuthor; private set => Set(ref infoAuthor, value); }
    public string InfoCopyright { get => infoCopyright; private set => Set(ref infoCopyright, value); }
    public string InfoDetails { get => infoDetails; private set => Set(ref infoDetails, value); }
    public string InfoComment { get => infoComment; private set => Set(ref infoComment, value); }

    void ReadInfo()
    {
        InfoName = InfoAuthor = InfoCopyright = InfoDetails = InfoComment = null;
        if (selectedItem?.Path is not { } path) return;
        try
        {
            var info = SoundFontInfo.Read(path);
            long size = new FileInfo(path).Length;
            InfoName = info.Name ?? Path.GetFileNameWithoutExtension(path);
            InfoAuthor = info.Author ?? "—";
            InfoCopyright = info.Copyright ?? "—";
            var details = new List<string> { $"{size / 1048576.0:0.0} MB", $"{info.Presets} presets" };
            if (info.Version != null) details.Add($"SoundFont {info.Version}");
            if (info.Date != null) details.Add(info.Date);
            if (info.Tool != null) details.Add(info.Tool);
            InfoDetails = string.Join(" · ", details);
            InfoComment = info.Comment;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            InfoName = Path.GetFileName(path);
            InfoDetails = $"Unreadable: {ex.Message}";
        }
    }

    // ---------- Files of the folder ----------
    // The .sf2 files of the folder, by name. The one chosen stays chosen if still there, otherwise the first (the next
    // tracks without their own play it; the engine keeps the one it plays).
    void Refresh()
    {
        folderFiles = Directory.Exists(Folder)
            ? Directory.GetFiles(Folder, "*.sf2").Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        defaultFile = folderFiles.FirstOrDefault(n => string.Equals(n, defaultFile, StringComparison.OrdinalIgnoreCase)) ?? folderFiles.FirstOrDefault();
        Rebuild();
    }

    // The folder in the Explorer (created if missing: the place to put .sf2 files).
    void OpenFolder()
    {
        Directory.CreateDirectory(Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = true });
    }

    // ---------- Settings (PowerMIDI.ini) ----------
    // The folder of the SoundFonts (Folder, first in the section: the file chosen is one of it; empty: the soundfonts
    // folder next to the program), the file chosen, the favorites (Favorites: names separated by |, a character no file
    // name has), the SF_BANK tag (ReadTag, WriteTag: 1 / 0), the settings of the engine, and its reverb and chorus
    // (SoundFontEffects, set in the Effects tab).
    public void LoadSettings(IniFile ini)
    {
        folderSetting = ini.Get("SoundFont", "Folder") ?? "";
        Folder = SoundFontFinder.FolderOf(folderSetting, AppContext.BaseDirectory);
        favorites.Clear();
        favorites.UnionWith((ini.Get("SoundFont", "Favorites") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        ReadTag = ini.GetInt("SoundFont", "ReadTag") != 0;
        WriteTag = ini.GetInt("SoundFont", "WriteTag") != 0;
        SoundFontEffects.Read(ini);
        updating = true;
        try { Change(SoundFontEngineOptions.Read(ini)); }
        finally { updating = false; }
        defaultFile = ini.Get("SoundFont", "File");   // kept if the folder has it (see Refresh)
        Refresh();
    }

    public void WriteSettings(IniFile ini)
    {
        ini.Set("SoundFont", "Folder", folderSetting);   // first: the INI is written anew, its keys in this order
        ini.Set("SoundFont", "File", defaultFile ?? "");
        ini.Set("SoundFont", "Favorites", string.Join("|", favorites.Order(StringComparer.OrdinalIgnoreCase)));
        ini.Set("SoundFont", "ReadTag", readTag ? 1 : 0);
        ini.Set("SoundFont", "WriteTag", writeTag ? 1 : 0);
        options.Write(ini);
        SoundFontEffects.Write(ini);
    }
}
