using System.Collections;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// Main view model: playlist, playback, MIDI port, mode, master volume and playback settings.
// It owns the mixer and effects view models; saving the MIDI file is in MainViewModel.Save.cs.
// - Playlist (drag and drop, files, folders), autoplay of the first added file, shuffle,
//   repeat (playlist / track), master volume.
// - Mode detected (last reset in the file, Auto) or chosen in the list, which forces it on the file; "Clean" also removes the other modes' SysEx.
public sealed partial class MainViewModel : ObservableObject
{
    const string GlyphPlay = "";
    const string GlyphPause = "";

    readonly IMainView view;
    readonly InstrumentDefinitions defs;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer ledTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    readonly Random random = new();

    OutputDevice output;
    Playback playback;
    PlaylistItem current;            // playlist item currently playing
    string state = "stopped";        // stopped | playing | paused
    string settingsFile;             // file the manual settings apply to
    bool prompting;                  // true while the confirmation prompt is shown
    string savedPort;                // port remembered in PowerMIDI.ini
    string lastFolder;
    bool updatingPorts, updatingPosition;

    public MainViewModel(IMainView view, InstrumentDefinitions defs)
    {
        this.view = view;
        this.defs = defs;
        Mixer = new MixerViewModel(defs, () => output, s => Status = s, view.ShowError);
        Effects = new EffectsViewModel(defs, () => output, view.ShowError);

        AddFilesCommand = Command(AddFilesFromDialog);
        AddFolderCommand = Command(AddFolderFromDialog);
        RemoveCommand = new RelayCommand(p => Run(() => Remove(p as IList)));
        ClearCommand = Command(() => ClearPlaylist());
        SaveCommand = Command(() => SaveModifiedMidi());
        SaveAsCommand = Command(() => SaveModifiedMidi(ask: true));
        RefreshPortsCommand = Command(UpdatePortList);
        PlaySelectedCommand = Command(() => { if (SelectedTrack != null) StartTrack(SelectedTrack); });
        PlayPauseCommand = Command(TogglePlayPause);
        StopCommand = Command(Stop);
        NextCommand = Command(() => MoveTrack(1));
        PreviousCommand = Command(Previous);
        RepeatCommand = Command(() => RepeatMode = (RepeatMode + 1) % 3);
        EventsCommand = Command(OpenEventEditor);

        Mixer.KeyboardToggle = c => Run(() => ToggleKeyboardChannel(c));

        timer.Tick += (_, _) => Run(OnTimer);
        ledTimer.Tick += (_, _) => { try { Mixer.UpdateLeds(state == "playing", KeyboardLedChannel); UpdateDisplayDots(); } catch { } };
    }

    RelayCommand Command(Action action) => new(() => Run(action));

    // Runs an action; an error is shown instead of crashing.
    void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { view.ShowError(ex); }
    }

    public MixerViewModel Mixer { get; }
    public EffectsViewModel Effects { get; }
    public ObservableCollection<PlaylistItem> Playlist { get; } = new();

    public ICommand AddFilesCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveCommand { get; }       // parameter: the selected items
    public ICommand ClearCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }       // right click on the Save button: always asks where to save
    public ICommand RefreshPortsCommand { get; }
    public ICommand PlaySelectedCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand RepeatCommand { get; }
    public ICommand EventsCommand { get; }       // Event Editor window

    string status = "Drop MIDI files or a folder onto the window.";
    public string Status { get => status; set => Set(ref status, value); }

    PlaylistItem selectedTrack;
    public PlaylistItem SelectedTrack { get => selectedTrack; set => Set(ref selectedTrack, value); }

    public void Start()
    {
        timer.Start();
        ledTimer.Start();
    }

    // Closing: the timers stop, playback stops and the port is released.
    public void Shutdown()
    {
        timer.Stop();
        ledTimer.Stop();
        ChannelMixer.Device = null;
        try { StopTrack(); } catch { }
        output?.Dispose();
        output = null;
    }

    // ---------- Settings (PowerMIDI.ini) ----------

    // The mode and the "Sliders" menu are not remembered: PowerMIDI starts in Auto and on Volume (CC7).
    public void LoadSettings(IniFile ini)
    {
        savedPort = ini.Get("MIDI", "Port");
        if (savedPort == "") savedPort = null;
        lastFolder = ini.Get("Playback", "LastFolder");
        if (ini.GetInt("Playback", "MasterVolume") is int volume && volume is >= 0 and <= 127) MasterVolume = volume;
        Shuffle = ini.Get("Playback", "Shuffle") == "1";
        if (ini.GetInt("Playback", "Repeat") is int repeat && repeat is >= 0 and <= 2) RepeatMode = repeat;
        Mixer.LoadVoiceMap(ini.Get("MIDI", "VoiceMap"));
        Mixer.LoadGsDefaultMap(ini.Get("MIDI", "GsDefaultMap"));
    }

    public void WriteSettings(IniFile ini)
    {
        ini.Set("MIDI", "Port", savedPort ?? output?.Name ?? "");
        ini.Set("Playback", "MasterVolume", (int)MasterVolume);
        ini.Set("Playback", "Shuffle", Shuffle ? 1 : 0);
        ini.Set("Playback", "Repeat", RepeatMode);   // 0 = off, 1 = playlist, 2 = track
        ini.Set("Playback", "LastFolder", lastFolder ?? "");
        ini.Set("MIDI", "VoiceMap", Mixer.VoiceMapSetting);
        ini.Set("MIDI", "GsDefaultMap", Mixer.GsDefaultMapSetting);
    }

    // ---------- MIDI port ----------

    IReadOnlyList<string> ports = Array.Empty<string>();
    public IReadOnlyList<string> Ports { get => ports; private set => Set(ref ports, value); }

    string selectedPort;
    public string SelectedPort
    {
        get => selectedPort;
        set
        {
            if (!Set(ref selectedPort, value)) return;
            Run(() =>
            {
                // Chosen by hand: this is the port to remember. (An automatic choice, when the remembered port is
                // missing, does not overwrite it.)
                if (!updatingPorts && value != null)
                {
                    savedPort = value;
                    view.SaveSettings();
                }
                OpenPort(value);
            });
        }
    }

    // Selected port: the one already open, otherwise the last chosen one (PowerMIDI.ini) if connected,
    // otherwise the preferred port (Sound Canvas, then the Windows GS synth).
    public void UpdatePortList()
    {
        var names = MidiTools.GetOutputNames();
        var selected = output != null ? output.Name
                     : savedPort != null && names.Contains(savedPort) ? savedPort
                     : MidiTools.GetPreferredOutput(names);
        updatingPorts = true;
        try
        {
            Ports = names;
            SelectedPort = selected;
        }
        finally { updatingPorts = false; }
        if (!string.IsNullOrEmpty(savedPort) && !names.Contains(savedPort))
            Status = $"Port '{savedPort}' not found (unplugged?): using '{selected}' for now.";
    }

    void OpenPort(string name)
    {
        if (string.IsNullOrEmpty(name) || output?.Name == name) return;
        var wasPlaying = current;
        StopTrack();
        ChannelMixer.Device = null;
        output?.Dispose();
        output = OutputDevice.GetByName(name);
        ChannelMixer.Device = output;   // for resending after a reset sent by the file
        ChannelMixer.SetMasterVolume(output, (int)MasterVolume);
        ApplyMixerState();
        Status = $"Output: {name}";
        if (wasPlaying != null) StartTrack(wasPlaying);
    }

    // Controllers, chosen instruments and fixed effects (start of track, seek, port change).
    void ApplyMixerState()
    {
        ChannelMixer.ApplyAll(output);
        ChannelMixer.ApplyVoiceMap(output);
        ChannelMixer.ApplyGsDefaultMap(output);
        ChannelMixer.ReapplyOverrides(output);
        ChannelMixer.ApplyEffects(output);
    }

    // ---------- Mode ----------

    // Auto, GS, XG, GM, GM2 or MT-32 (the items of the Mode menu).
    string selectedMode = "Auto";
    public string SelectedMode
    {
        get => selectedMode;
        set
        {
            if (!Set(ref selectedMode, value ?? "Auto")) return;
            Run(() =>
            {
                UpdateForceMode();
                if (current == null) { ApplyModeWithoutTrack(); return; }
                StartTrack(current);   // new mode applied right away: replay (same file: the other manual settings are kept)
                Status = isForced ? $"Mode {selectedMode} forced: the file's resets are ignored ('Save MIDI file…' replaces them)"
                                  : $"File mode restored: {CurrentStandard}";
            });
        }
    }

    // No track loaded: the mixer and effects switch to the chosen mode right away (Auto: GM, the default when no file says otherwise, as at startup),
    // and the module receives the mode's reset, so that what is shown matches what it plays.
    void ApplyModeWithoutTrack()
    {
        var std = selectedMode == "Auto" ? MidiTools.DefaultStandard : selectedMode;
        ChannelMixer.Mt32Mode = std == "MT-32";
        ChannelMixer.Gm2Mode = std == "GM2";
        ChannelMixer.XgMode = std == "XG";
        ChannelMixer.GsMode = std == "GS";
        if (output != null) MidiTools.SendReset(output, std);
        ChannelMixer.ResetChannelState(std == "XG");
        Mixer.Standard = std;
        Effects.Standard = std;
        if (output != null)
        {
            ChannelMixer.SetMasterVolume(output, (int)MasterVolume);
            ApplyMixerState();   // settings already chosen in the mixer / effects
        }
        Mixer.Refresh();
        Effects.Refresh();
        Status = output != null ? $"Mode {std}: reset sent to {output.Name}" : $"Mode {std}";
    }

    // Forced mode: any mode chosen in the list other than Auto is forced on the file.
    bool isForced => selectedMode != "Auto";

    // "Clean": also ignores the SysEx specific to the other modes; applied immediately (replay).
    bool isCleaned;
    public bool IsCleaned
    {
        get => isCleaned;
        set
        {
            if (!Set(ref isCleaned, value)) return;
            Run(() =>
            {
                if (current != null) StartTrack(current);
                Status = value ? "SysEx of the other modes ignored ('Save MIDI file…' removes them)" : "SysEx of the other modes kept";
            });
        }
    }

    public bool CleanEnabled => isForced;

    void SetModeQuietly(string mode) { selectedMode = mode; OnPropertyChanged(nameof(SelectedMode)); }
    void SetCleanedQuietly(bool value) { isCleaned = value; OnPropertyChanged(nameof(IsCleaned)); }

    void UpdateForceMode()
    {
        // "Clean" only makes sense with a forced mode.
        if (!isForced && isCleaned) SetCleanedQuietly(false);
        ChannelMixer.ForceMode = isForced;
        OnPropertyChanged(nameof(CleanEnabled));
    }

    // ---------- Playlist ----------

    void UpdateNumbers()
    {
        for (int i = 0; i < Playlist.Count; i++)
        {
            var item = Playlist[i];
            item.Index = i + 1;
            item.Playing = item == current ? (state == "paused" ? "❚❚" : "▶") : "";
        }
    }

    // Adds files and folders (subfolders included) to the playlist. play: the first added file plays right away
    // (file opened from Explorer while PowerMIDI runs); otherwise only if nothing is in progress.
    public void AddFiles(IEnumerable<string> paths, bool play = false)
    {
        PlaylistItem firstAdded = null;
        int added = 0;
        view.SetBusy(true);
        try
        {
            var files = new List<string>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                    files.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
                                            .Where(MidiTools.IsMidiFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                else if (MidiTools.IsMidiFile(p)) files.Add(p);
            }
            foreach (var f in files)
            {
                string std = "?", dur = "unreadable";
                try
                {
                    var midi = MidiTools.ReadFile(f);
                    std = MidiTools.GetStandard(midi);
                    dur = MidiTools.FormatTime(MidiTools.GetDuration(midi));
                }
                catch { }
                var item = new PlaylistItem { Name = Path.GetFileName(f), Path = Path.GetFullPath(f), Standard = std, Duration = dur };
                Playlist.Add(item);
                firstAdded ??= item;
                added++;
            }
            UpdateNumbers();
            Status = $"{added} file(s) added — {Playlist.Count} in the playlist.";
        }
        finally { view.SetBusy(false); }

        // Nothing in progress (no playback, no pause, no track loaded): play the first added file.
        if (firstAdded != null && output != null && (play || (state == "stopped" && current == null))) StartTrack(firstAdded);
    }

    void AddFilesFromDialog()
    {
        var files = view.PickMidiFiles(Directory.Exists(lastFolder) ? lastFolder : null);
        if (files == null || files.Count == 0) return;
        lastFolder = Path.GetDirectoryName(files[0]);
        AddFiles(files);
    }

    void AddFolderFromDialog()
    {
        var folder = view.PickFolder(Directory.Exists(lastFolder) ? lastFolder : null);
        if (folder == null) return;
        lastFolder = folder;
        AddFiles(new[] { folder });
    }

    void Remove(IList selected)
    {
        if (selected == null) return;
        var items = selected.Cast<PlaylistItem>().ToList();
        // The file of the manual settings is removed: they are saved or discarded now.
        bool leaving = items.Any(i => i.Path == settingsFile);
        if (leaving && !ConfirmLeaveSettings()) return;
        foreach (var item in items)
        {
            if (item == current) { StopTrack(); current = null; ResetNowPlaying(); }
            Playlist.Remove(item);
        }
        if (leaving) ForgetSettingsFile();
        UpdateNumbers();
    }

    // Returns false if the user chose to stay on the current track (unsaved settings).
    bool ClearPlaylist()
    {
        if (!ConfirmLeaveSettings()) return false;
        StopTrack();
        current = null;
        Playlist.Clear();
        ResetNowPlaying();
        ForgetSettingsFile();
        Status = "Playlist cleared.";
        return true;
    }

    // Files opened from outside while PowerMIDI runs (a MIDI file opened in Explorer, the command line): they
    // replace the playlist, and the first one plays. Nothing playable among them: the playlist is left as it is.
    public void OpenFiles(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (!list.Any(p => Directory.Exists(p) || MidiTools.IsMidiFile(p))) return;
        if (Playlist.Count > 0 && !ClearPlaylist()) return;
        AddFiles(list, play: true);
    }

    // The manual settings no longer have a file (removed from the playlist): cleared, mode back to Auto,
    // so that the next track starts clean, without asking again.
    void ForgetSettingsFile()
    {
        if (settingsFile == null) return;
        bool wasForced = isForced;
        ClearManualSettings();
        savedSignature = "";
        ResetSpeedAndPitch();
        lastSavedTarget = null;
        settingsFile = null;
        // No track left playing: the mixer and effects follow the Auto mode, as when it is chosen in the list.
        if (wasForced && current == null) ApplyModeWithoutTrack();
        else { Mixer.Refresh(); Effects.Refresh(); }
    }

    // ---------- Now playing ----------

    string title = "No track";
    public string Title { get => title; private set => Set(ref title, value); }

    // Tooltip of the title: file name, copyright and texts of the file.
    string titleToolTip;
    public string TitleToolTip { get => titleToolTip; private set => Set(ref titleToolTip, value); }

    // Standard of the current track ("—" if none); the badge color follows it.
    string currentStandard = "—";
    public string CurrentStandard { get => currentStandard; private set => Set(ref currentStandard, value); }

    // Screen of the module, in the player: the text the file writes on it (MT-32, Sound Canvas, MU; null until the file
    // sends one) and its dot picture (below). Shown when the file sends some, unless hidden with the display button.
    string displayText;
    public string DisplayText { get => displayText; private set { if (Set(ref displayText, value)) NotifyDisplay(); } }
    bool showDisplay = true;
    public bool ShowDisplay { get => showDisplay; set { if (Set(ref showDisplay, value)) NotifyDisplay(); } }
    public bool HasDisplayDots => ModuleDisplay.HasDots;
    // The file has sent a text or a picture: the screen is shown, unless hidden with the display button.
    public bool HasDisplay => displayText != null || HasDisplayDots;
    public bool DisplayShown => showDisplay && HasDisplay;
    // Display button: pressed while the screen is shown (released while the file has sent nothing, and then inactive).
    public bool DisplayButton { get => DisplayShown; set { if (HasDisplay) ShowDisplay = value; OnPropertyChanged(); } }

    void NotifyDisplay()
    {
        OnPropertyChanged(nameof(HasDisplayDots));
        OnPropertyChanged(nameof(HasDisplay));
        OnPropertyChanged(nameof(DisplayShown));
        OnPropertyChanged(nameof(DisplayButton));
    }

    // Dot picture the file draws on the screen of the module (Sound Canvas, MU: 16 × 16), lit dots in the color of the
    // LCD text (all unlit until the file sends one). Updated with the LEDs (30 ms), fast enough for an animation.
    System.Windows.Media.Imaging.WriteableBitmap displayDots;
    int shownDotsVersion = -1;
    public System.Windows.Media.ImageSource DisplayDots => displayDots;

    void UpdateDisplayDots()
    {
        int version = ModuleDisplay.DotsVersion;
        if (version == shownDotsVersion) return;
        shownDotsVersion = version;
        displayDots ??= new System.Windows.Media.Imaging.WriteableBitmap(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
        var dots = ModuleDisplay.Dots();
        var pixels = new int[256];
        for (int i = 0; i < 256; i++) pixels[i] = dots[i] ? 0xA8E05F : 0x2C3A1D;
        displayDots.WritePixels(new System.Windows.Int32Rect(0, 0, 16, 16), pixels, 16 * 4, 0);
        OnPropertyChanged(nameof(DisplayDots));
        NotifyDisplay();
    }

    string timeText = "00:00 / 00:00";
    public string TimeText { get => timeText; private set => Set(ref timeText, value); }

    string durationText = "00:00";
    double duration = 1;
    public double Duration { get => duration; private set => Set(ref duration, value); }

    // Position bar, in seconds. While the user drags it (IsSeeking), only the time is shown;
    // the jump happens when the mouse button is released (EndSeek).
    double position;
    public double Position
    {
        get => position;
        set
        {
            if (!Set(ref position, value) || updatingPosition) return;
            if (IsSeeking) TimeText = $"{MidiTools.FormatTime(TimeSpan.FromSeconds(value))} / {durationText}";
        }
    }

    public bool IsSeeking { get; set; }

    public void EndSeek()
    {
        IsSeeking = false;
        Run(() =>
        {
            CancelInsertMode();
            if (playback == null) return;
            playback.MoveToTime(new MetricTimeSpan(TimeSpan.FromSeconds(position)));
            // The seek resends the file's CC7 and programs: reapply sliders and manual choices.
            ApplyMixerState();
        });
    }

    void SetPosition(double seconds)
    {
        updatingPosition = true;
        try { Position = seconds; }
        finally { updatingPosition = false; }
    }

    // A track is loaded: the position bar is enabled (disabled on "No track").
    bool hasTrack;
    public bool HasTrack { get => hasTrack; private set => Set(ref hasTrack, value); }

    void ResetNowPlaying()
    {
        HasTrack = false;
        ClearLoop();
        Title = "No track";
        TitleToolTip = null;
        CurrentStandard = "—";
        TimeText = "00:00 / 00:00";
        SetPosition(0);
    }

    string playGlyph = GlyphPlay;
    public string PlayGlyph { get => playGlyph; private set => Set(ref playGlyph, value); }

    void SetState(string newState)
    {
        state = newState;
        PlaybackPriority.Set(newState == "playing");   // same timing in the background (timer, priority, no sleep)
        PlayGlyph = newState == "playing" ? GlyphPause : GlyphPlay;
        HasTrack = current != null;
        UpdateInsertEnabled();
    }

    // ---------- Volume, shuffle, repeat ----------

    double masterVolume = 127;
    public double MasterVolume
    {
        get => masterVolume;
        set
        {
            if (!Set(ref masterVolume, value)) return;
            OnPropertyChanged(nameof(MasterVolumeText));
            Run(() => { if (output != null) ChannelMixer.SetMasterVolume(output, (int)value); });
        }
    }

    public string MasterVolumeText => ((int)masterVolume).ToString();

    bool shuffle;
    public bool Shuffle { get => shuffle; set => Set(ref shuffle, value); }

    // Three-state repeat button: off → playlist → track → off.
    int repeatMode;
    public int RepeatMode
    {
        get => repeatMode;
        set
        {
            if (!Set(ref repeatMode, value)) return;
            Run(ApplyLoopMode);   // repeat track: endless loop; otherwise the remaining extra pass
            OnPropertyChanged(nameof(IsRepeatOn));
            OnPropertyChanged(nameof(RepeatGlyph));
            OnPropertyChanged(nameof(RepeatToolTip));
        }
    }

    public bool IsRepeatOn => repeatMode > 0;
    public string RepeatGlyph => repeatMode == 2 ? "" : "";   // 🔂 / 🔁
    public string RepeatToolTip => repeatMode switch
    {
        1 => "Repeat playlist",
        2 => "Repeat track",
        _ => "Repeat: off",
    };

    // ---------- Playback ----------

    void StopTrack()
    {
        CancelInsertMode();
        ReleaseKeyboardNotes();
        if (playback != null)
        {
            playback.Stop();
            playback.Dispose();
            playback = null;
        }
        if (output != null) MidiTools.SendPanic(output);
        SetState("stopped");
    }

    void StartTrack(PlaylistItem item)
    {
        CancelInsertMode();
        // Another file with unsaved settings: ask before losing them.
        if (settingsFile != null && settingsFile != item.Path && !ConfirmLeaveSettings()) return;
        StopTrack();
        if (output == null) throw new InvalidOperationException("No MIDI port selected.");

        // Another file: the manual settings of the previous track (instruments, volume, pan, effects,
        // muted channels, forced mode) are cleared. They are kept if the same track is played again.
        bool clearedSettings = false;
        if (settingsFile != null && settingsFile != item.Path)
        {
            clearedSettings = ClearManualSettings();
            savedSignature = "";
            ResetSpeedAndPitch();
            lastSavedTarget = null;
        }
        settingsFile = item.Path;

        var midi = ReadTrackFile(item.Path);   // with its insertions (Insert button)
        var std = selectedMode == "Auto" ? MidiTools.GetStandard(midi) : selectedMode;
        ChannelMixer.ForceMode = isForced;   // file resets ignored
        ChannelMixer.Mt32Mode = std == "MT-32";   // MT-32 reverb SysEx follow the Effects tab
        ChannelMixer.Gm2Mode = std == "GM2";      // GM2 reverb / chorus SysEx too
        ChannelMixer.XgMode = std == "XG";        // XG voice map chosen in the Mixer tab
        ChannelMixer.GsMode = std == "GS";
        ChannelMixer.DropManufacturers = isCleaned ? MidiTools.GetOtherModeManufacturers(std) : Array.Empty<int>();

        MidiTools.SendReset(output, std);
        ChannelMixer.ResetChannelState(std == "XG");
        ChannelMixer.SetMasterVolume(output, (int)MasterVolume);
        Mixer.Standard = std;
        Effects.Standard = std;
        ApplyMixerState();   // same track played again: the chosen instruments are resent

        loopsDone = 0;
        var pb = CreatePlayback(midi);
        RefreshMarkers();
        SetPosition(0);
        TimeText = $"00:00 / {durationText}";
        ShowTrackInfo(midi, item, std);

        current = item;
        pb.Start();
        SetState("playing");
        SelectedTrack = item;
        UpdateNumbers();
        var text = $"Playing on {output.Name} — reset {std}{LoopStatus}";
        if (clearedSettings) text += " — manual settings of the previous track cleared";
        Status = text;
    }

    // "Now playing": title of the file (name of its first track), otherwise the file name; tooltip; standard.
    void ShowTrackInfo(MidiFile midi, PlaylistItem item, string std)
    {
        var (fileTitle, copyright, texts) = MidiTools.GetInfo(midi);
        Title = fileTitle ?? item.Name;
        var tip = new List<string> { $"File: {item.Name}" };
        if (copyright != null) tip.Add($"Copyright: {copyright}");
        tip.AddRange(texts);
        TitleToolTip = string.Join("\n", tip);
        CurrentStandard = std;
    }

    // Playback of a track (file + insertions) on the output, its tempo map and duration.
    Playback CreatePlayback(MidiFile midi)
    {
        var pb = midi.GetPlayback(output);
        pb.InterruptNotesOnStop = true;
        // Every Note On and Note Off of the file is played, as the file has them. By default the playback library keeps
        // one active note per key: it drops the Note On of a key already held, and the Note Off of a key it does not
        // hold. Both are needed: a note started again on a held key (two Note On, two Note Off, e.g. sttng.mid at
        // 0:29) keeps one voice per Note On in the module; a note whose Note Off was skipped at a loop end (see
        // MainViewModel.Loop.cs) stays active for the library, which would not play it again.
        pb.SendNoteOnEventsForActiveNotes = true;
        pb.SendNoteOffEventsForNonActiveNotes = true;
        // After a seek, resends the up-to-date programs, controllers and pitch bend.
        pb.TrackProgram = true;
        pb.TrackControlValue = true;
        pb.TrackPitchValue = true;
        // Passes each event through the mixer (fixed values, LEDs); while looping, the events starting at the loop end
        // are skipped (see MainViewModel.Loop.cs).
        pb.EventCallback = (e, raw, time) => IsLoopSeam(raw) ? null : ChannelMixer.Callback(e, raw, time);
        pb.NoteCallback = (note, raw, length, time) => IsLoopSeam(raw) ? null : TransposeNote(ChannelMixer.NoteCallback(note, raw, length, time));
        pb.Speed = speed / 100;   // speed and pitch sliders (MainViewModel.Speed.cs)
        playback = pb;
        tempoMap = midi.GetTempoMap();
        eventMidi = midi;
        eventsPending = true;   // the Event Editor lists it at the next timer tick (after the start)
        TimeSpan length = pb.GetDuration<MetricTimeSpan>();
        durationText = MidiTools.FormatTime(length);
        Duration = Math.Max(1, length.TotalSeconds);
        SetUpLoop(midi, pb);   // video game loop (MainViewModel.Loop.cs)
        return pb;
    }

    // Clears the manual settings (mixer, forced mode, effects) without sending anything to the module
    // (a reset follows at the start of the track). Returns true if there were any.
    bool ClearManualSettings()
    {
        bool had = Mixer.ClearManualSettings();
        if (isForced)
        {
            // Back to the mode detected in the file.
            SetModeQuietly("Auto");
            SetCleanedQuietly(false);
            ChannelMixer.ForceMode = false;
            ChannelMixer.DropManufacturers = Array.Empty<int>();
            OnPropertyChanged(nameof(CleanEnabled));
            had = true;
        }
        had |= Effects.ClearManualSettings();
        had |= ClearInsertions();
        return had;
    }

    void MoveTrack(int direction)
    {
        int count = Playlist.Count;
        if (count == 0) return;
        int index = current != null ? Playlist.IndexOf(current) : -1;
        int next;
        if (shuffle && count > 1)
        {
            do { next = random.Next(count); } while (next == index);
        }
        else
        {
            next = index + direction;
            if (next >= count)
            {
                if (repeatMode >= 1) next = 0;
                else { StopTrack(); UpdateNumbers(); Status = "End of playlist."; return; }
            }
            if (next < 0) next = 0;
        }
        StartTrack(Playlist[next]);
    }

    void TogglePlayPause()
    {
        // Insert mode: releasing Insert inserts the settings (playback stays paused); Play does nothing meanwhile.
        if (insertMode) { Status = "Insert mode: release Insert to insert the settings (Stop: cancel)."; return; }
        switch (state)
        {
            case "playing": playback.Stop(); SetState("paused"); break;
            case "paused": playback.Start(); SetState("playing"); break;
            default:
                var item = SelectedTrack ?? current ?? Playlist.FirstOrDefault();
                if (item != null) StartTrack(item);
                break;
        }
        UpdateNumbers();
    }

    void Stop()
    {
        StopTrack();
        if (current != null) { SetPosition(0); TimeText = $"00:00 / {durationText}"; }
        UpdateNumbers();
    }

    // Like most players: after 3 s, "previous" goes back to the start of the track.
    void Previous()
    {
        if (playback != null && ((TimeSpan)playback.GetCurrentTime<MetricTimeSpan>()).TotalSeconds > 3)
        {
            CancelInsertMode();
            playback.MoveToTime(new MidiTimeSpan(0));   // not MoveToStart: with a loop, that would be the loop start
            ApplyMixerState();
        }
        else MoveTrack(-1);
    }

    // Main timer (200 ms): mixer and effects display, position, end of track.
    void OnTimer()
    {
        if (prompting) return;   // confirmation prompt open: do not move on
        Mixer.Refresh();
        Effects.Refresh();
        DisplayText = ModuleDisplay.Text;
        UpdateInsertEnabled();
        LoadEvents();
        RefreshEventsFixed();
        if (playback == null || state != "playing") return;
        UpdateLoop();
        if (!playback.IsRunning)   // end of track
        {
            if (repeatMode == 2) StartTrack(current); else MoveTrack(1);
            return;
        }
        if (IsSeeking) return;
        TimeSpan now = playback.GetCurrentTime<MetricTimeSpan>();
        SetPosition(now.TotalSeconds);
        TimeText = $"{MidiTools.FormatTime(now)} / {durationText}";
        eventEditor?.UpdatePosition(now.TotalSeconds);
    }

    // ---------- Event Editor ----------

    // The window shows the events of the track being played (with its insertions); it follows the playback.
    EventEditorViewModel eventEditor;   // null while the window is closed
    MidiFile eventMidi;                 // file of the current playback
    bool eventsPending;                 // not listed yet

    void OpenEventEditor()
    {
        if (eventEditor == null)
        {
            eventEditor = new EventEditorViewModel(defs, Effects, SeekTo);
            eventsPending = true;
        }
        LoadEvents();
        view.ShowEventEditor(eventEditor, () => eventEditor = null);
    }

    // Lists the events of the current playback (a long file takes a moment: not before the playback starts).
    void LoadEvents()
    {
        if (!eventsPending || eventEditor == null || eventMidi == null) return;
        eventsPending = false;
        eventEditor.Load(eventMidi, Mixer.Standard, Path.GetFileName(settingsFile));
    }

    // Values fixed in the Mixer or Effects tab (blue): the Event Editor shows the events they replace in blue too.
    string eventsFixed = "";

    void RefreshEventsFixed()
    {
        if (eventEditor == null) return;
        var signature = GetSettingsSignature(false);
        if (signature == eventsFixed) return;
        eventsFixed = signature;
        eventEditor.RefreshFixed();
    }

    // Playback from a time (seconds), as when the position bar is released there.
    void SeekTo(double seconds)
    {
        if (playback == null) return;
        SetPosition(seconds);
        EndSeek();
        TimeText = $"{MidiTools.FormatTime(TimeSpan.FromSeconds(seconds))} / {durationText}";
    }
}
