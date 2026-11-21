using System.Diagnostics;
using System.Windows.Input;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// SoundFonts tab: the .sf2 files of the soundfonts folder (next to the program, or the one of PowerMIDI.ini, see
// SoundFontFinder.FolderOf), the one played by the SoundFont engine (MIDI OUT "SoundFont engine (FluidSynth)";
// remembered in PowerMIDI.ini) and what it says about itself.
public sealed class SoundFontsViewModel : ObservableObject
{
    readonly Action chosen;   // a file chosen by hand: the engine loads it if it runs
    readonly Action<bool> engineChanged;   // a setting of the engine changed: restart (true) or taken at once (false)
    string savedFile;         // the file remembered in PowerMIDI.ini
    string folderSetting = "";   // the folder as PowerMIDI.ini gives it ("": the default one), written back as it is
    bool updating;            // list refreshed or settings read: not a choice by hand

    public SoundFontsViewModel(Action chosen, Action<bool> engineChanged)
    {
        Folder = SoundFontFinder.FolderOf("", AppContext.BaseDirectory);
        this.chosen = chosen;
        this.engineChanged = engineChanged;
        RefreshCommand = new RelayCommand(() => Refresh(notify: true));
        OpenFolderCommand = new RelayCommand(OpenFolder);
        Refresh(notify: false);
    }

    string folder;
    public string Folder { get => folder; private set { if (Set(ref folder, value)) OnPropertyChanged(nameof(FolderToolTip)); } }
    public string FolderToolTip => $"Open soundfonts folder (place for .sf2 files): {folder}";
    public ICommand RefreshCommand { get; }
    public ICommand OpenFolderCommand { get; }

    IReadOnlyList<string> files = Array.Empty<string>();
    public IReadOnlyList<string> Files { get => files; private set { if (Set(ref files, value)) OnPropertyChanged(nameof(HasFiles)); } }
    public bool HasFiles => files.Count > 0;

    string selectedFile;
    public string SelectedFile
    {
        get => selectedFile;
        set
        {
            if (!Set(ref selectedFile, value)) return;
            ReadInfo();
            if (updating || value == null) return;
            savedFile = value;
            chosen();
        }
    }

    // Full path of the chosen file (null: none).
    public string SelectedPath => selectedFile == null ? null : Path.Combine(Folder, selectedFile);

    // The SF_BANK tag of the MIDI files (see SoundFontFinder): read when a file is played (its SoundFont), written by
    // Save MIDI file in SF2 mode (the SoundFont played). Both on by default.
    bool readTag = true, writeTag = true;
    public bool ReadTag { get => readTag; set => Set(ref readTag, value); }
    public bool WriteTag { get => writeTag; set => Set(ref writeTag, value); }

    // State of the engine, set by the main view model: stopped, loading, running; and its delay while it runs.
    string engineState = "Stopped";
    public string EngineState { get => engineState; set => Set(ref engineState, value); }
    string delayText;
    public string DelayText { get => delayText; set => Set(ref delayText, value); }

    // ---------- Settings of the engine (SoundFontEngineOptions) ----------
    // A list item: its text and its value (string, int or bool).
    public sealed record Choice(string Text, object Value);

    SoundFontEngineOptions options = new();
    public SoundFontEngineOptions Options => options;

    public IReadOnlyList<Choice> ReverbEngines { get; } = SoundFontEngineOptions.ReverbEngines.Select(x => new Choice(x.Text, x.Value)).ToList();
    public IReadOnlyList<Choice> Interpolations { get; } = SoundFontEngineOptions.Interpolations.Select(x => new Choice(x.Text, x.Value)).ToList();
    public IReadOnlyList<Choice> Polyphonies { get; } = SoundFontEngineOptions.Polyphonies.Select(n => new Choice($"{n} voices", n)).ToList();
    public IReadOnlyList<Choice> SampleLoadings { get; } = new[] { new Choice("All at load", false), new Choice("On demand", true) };
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

    // ---------- What the chosen file says about itself ----------
    string infoName, infoAuthor, infoCopyright, infoDetails, infoComment;
    public string InfoName { get => infoName; private set => Set(ref infoName, value); }
    public string InfoAuthor { get => infoAuthor; private set => Set(ref infoAuthor, value); }
    public string InfoCopyright { get => infoCopyright; private set => Set(ref infoCopyright, value); }
    public string InfoDetails { get => infoDetails; private set => Set(ref infoDetails, value); }
    public string InfoComment { get => infoComment; private set => Set(ref infoComment, value); }

    void ReadInfo()
    {
        InfoName = InfoAuthor = InfoCopyright = InfoDetails = InfoComment = null;
        if (SelectedPath is not { } path) return;
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

    // ---------- List of files ----------
    // The .sf2 files of the folder, by name. The chosen one stays chosen if still there, otherwise the remembered one,
    // otherwise the first. notify: a refresh by hand that changes the file is a choice (the engine loads it).
    void Refresh(bool notify)
    {
        var names = Directory.Exists(Folder)
            ? Directory.GetFiles(Folder, "*.sf2").Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        var before = selectedFile;
        var keep = selectedFile ?? savedFile;
        var choice = names.FirstOrDefault(n => string.Equals(n, keep, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
        updating = true;
        try
        {
            Files = names;
            SelectedFile = choice;
            if (choice == before) ReadInfo();   // same file: its information read again (it may have been replaced)
        }
        finally { updating = false; }
        if (notify && choice != before && choice != null) chosen();
    }

    // The folder in the Explorer (created if missing: the place to put .sf2 files).
    void OpenFolder()
    {
        Directory.CreateDirectory(Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = true });
    }

    // ---------- Settings (PowerMIDI.ini) ----------
    // The folder of the SoundFonts (Folder, first in the section: the file chosen is one of it; empty: the soundfonts
    // folder next to the program), the file chosen, the SF_BANK tag (ReadTag, WriteTag: 1 / 0), the settings of the
    // engine, and its reverb and chorus (SoundFontEffects, set in the Effects tab).
    public void LoadSettings(IniFile ini)
    {
        folderSetting = ini.Get("SoundFont", "Folder") ?? "";
        Folder = SoundFontFinder.FolderOf(folderSetting, AppContext.BaseDirectory);
        ReadTag = ini.GetInt("SoundFont", "ReadTag") != 0;
        WriteTag = ini.GetInt("SoundFont", "WriteTag") != 0;
        SoundFontEffects.Read(ini);
        updating = true;
        try { Change(SoundFontEngineOptions.Read(ini)); }
        finally { updating = false; }
        savedFile = ini.Get("SoundFont", "File");
        if (savedFile == "") savedFile = null;
        selectedFile = null;   // the remembered file chosen by the refresh
        Refresh(notify: false);
    }

    public void WriteSettings(IniFile ini)
    {
        ini.Set("SoundFont", "Folder", folderSetting);   // first: the INI is written anew, its keys in this order
        ini.Set("SoundFont", "File", selectedFile ?? savedFile ?? "");
        ini.Set("SoundFont", "ReadTag", readTag ? 1 : 0);
        ini.Set("SoundFont", "WriteTag", writeTag ? 1 : 0);
        options.Write(ini);
        SoundFontEffects.Write(ini);
    }
}
