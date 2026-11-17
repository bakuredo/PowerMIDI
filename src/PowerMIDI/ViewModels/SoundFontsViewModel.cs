using System.Diagnostics;
using System.Windows.Input;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// SoundFonts tab: the .sf2 files of the soundfonts folder next to the program, the one played by the SoundFont engine
// (MIDI OUT "SoundFont engine (FluidSynth)"; remembered in PowerMIDI.ini) and what it says about itself.
public sealed class SoundFontsViewModel : ObservableObject
{
    readonly Action chosen;   // a file chosen by hand: the engine loads it if it runs
    string savedFile;         // the file remembered in PowerMIDI.ini
    bool updating;            // list refreshed or settings read: not a choice by hand

    public SoundFontsViewModel(string folder, Action chosen)
    {
        Folder = folder;
        this.chosen = chosen;
        RefreshCommand = new RelayCommand(() => Refresh(notify: true));
        OpenFolderCommand = new RelayCommand(OpenFolder);
        Refresh(notify: false);
    }

    public string Folder { get; }
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

    // State of the engine, set by the main view model: stopped, loading, running.
    string engineState = "Stopped";
    public string EngineState { get => engineState; set => Set(ref engineState, value); }

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
    public void LoadSettings(IniFile ini)
    {
        savedFile = ini.Get("SoundFont", "File");
        if (savedFile == "") savedFile = null;
        selectedFile = null;   // the remembered file chosen by the refresh
        Refresh(notify: false);
    }

    public void WriteSettings(IniFile ini) => ini.Set("SoundFont", "File", selectedFile ?? savedFile ?? "");
}
