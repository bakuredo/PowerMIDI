using Melanchall.DryWetMidi.Core;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// Saving the current track with the manual settings, and the prompt before losing them.
public sealed partial class MainViewModel
{
    string savedSignature = "";   // fingerprint of the settings at the last save (see GetSettingsSignature)
    // File PowerMIDI already saved the current track to (or the track itself, when it is such a file: see
    // SwitchToSavedFile): the next saves go there without asking. Null: none yet, the save asks where.
    string lastSavedTarget;

    static bool SamePath(string a, string b) =>
        a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // Saves the current track with the chosen instruments, fixed controllers and effects, and the forced mode.
    // On each modified channel, every bank select / Program Change (instrument) or every event of a fixed
    // controller in the file is replaced by the choice.
    // Returns true if the file was written. The source file is the one the settings apply to
    // (even if it was removed from the playlist in the meantime).
    // The file is chosen the first time; afterwards it is saved again to the same file, unless ask is true
    // (right click on the button: the dialog again).
    // quiet: no report shown afterwards.
    // reload: saved over the track being played, the file is loaded again (its settings are now in it); false when
    // the track is being left anyway.
    bool SaveModifiedMidi(bool quiet = false, bool ask = false, bool reload = true)
    {
        var source = settingsFile ?? current?.Path;
        if (source == null) { Status = "No current track to save."; return false; }
        var (channels, modules, forceMode) = SettingsToWrite();
        if (channels.Count == 0 && forceMode == null && modules.Count == 0 && editedFile == null)
        {
            view.ShowMessage("No instrument, setting or effect has been changed.");
            return false;
        }

        // Suggested: the folder and name of the last save, otherwise of the track itself (as a .mid; the dialog asks
        // before replacing an existing file).
        var suggested = lastSavedTarget ?? source;
        var target = !ask && lastSavedTarget != null ? lastSavedTarget
                   : view.PickSaveFile(Path.GetDirectoryName(suggested), Path.GetFileNameWithoutExtension(suggested) + ".mid");
        if (target == null) return false;

        // Re-read from disk, or the copy changed in the Event Editor: the original file is never modified in memory or
        // during playback.
        var midi = editedFile?.Clone() ?? MidiTools.ReadFile(source);
        var report = new List<string>();
        if (editedFile != null) report.Add("events changed in Event Editor (and written with Append / Replace)");
        report.AddRange(WriteSettings(midi, channels, modules, forceMode));
        MidiTools.WriteFile(midi, target);

        savedSignature = GetSettingsSignature();
        lastSavedTarget = target;
        bool wasPlaying = state == "playing";
        // Saved over the track being played: its settings and changed events are now in the file. It is loaded again,
        // with nothing pending (otherwise the next save would write the settings twice).
        if (reload && current != null && SamePath(target, current.Path) && SamePath(source, current.Path)) SwitchToSavedFile(target);
        Status = $"Saved: {target}";
        if (!quiet) view.ShowMessage($"File saved:\n{target}\n\n{string.Join("\n", report.Where(s => !string.IsNullOrEmpty(s)))}");
        // Playing during the save: the track starts again from its beginning (once the report is closed), as it is now
        // (not when the track is being left: reload false).
        if (reload && wasPlaying && current != null) StartTrack(current);
        return true;
    }

    // Writes the settings in a file: on each modified channel, every bank select / Program Change (instrument) or every
    // event of a fixed controller is replaced by the choice; the modules of the Effects tab; the forced mode. Returns the
    // report.
    List<string> WriteSettings(MidiFile midi, List<int> channels, List<SettingsModule> modules, string forceMode)
    {
        int controllers = MixerViewModel.ControllerCount;
        var apply = new bool[16];
        int[] msb = new int[16], lsb = new int[16], program = new int[16];
        var rhythm = Enumerable.Range(0, 16).Select(c => ChannelMixer.RhythmOverride[c]).ToArray();   // -1 = the file's
        // Fixed values per controller: arrays [controller][channel].
        var applyCc = new bool[controllers][];
        var ccValues = new int[controllers][];
        for (int k = 0; k < controllers; k++) { applyCc[k] = new bool[16]; ccValues[k] = new int[16]; }
        foreach (int c in channels)
        {
            apply[c] = ChannelMixer.Override[c];
            msb[c] = ChannelMixer.OverrideMsb[c];
            lsb[c] = ChannelMixer.OverrideLsb[c];
            program[c] = ChannelMixer.OverrideProgram[c];
            for (int k = 0; k < controllers; k++)
            {
                applyCc[k][c] = ChannelMixer.IsFixed(k, c);
                ccValues[k][c] = ChannelMixer.FixedValue(k, c);
            }
        }

        var report = new List<string>();
        report.Add(MidiFileEditor.ApplyChanges(midi, apply, msb, lsb, program, applyCc, ccValues, rhythm));
        // Effects tab: effects, Part and Drums pages (the XG part modes and drum setups share one writer).
        foreach (var write in modules.Select(m => m.Write).Distinct()) report.Add(write(midi));
        // Forced mode: last, so that the new reset comes before all the settings inserted above.
        if (forceMode != null)
        {
            var reset = MidiTools.GetModeEvents(forceMode);
            // The SysEx of the other modes are removed (ignored during playback too): for GM / GM2, otherwise the
            // file would still be detected (and played) as GS / XG ("GM On followed by GS / XG SysEx").
            var drop = MidiTools.GetOtherModeManufacturers(forceMode);
            report.Add($"{MidiFileEditor.ForceReset(midi, reset, drop)} (mode {forceMode})");
        }
        return report;
    }

    // What the settings change: the channels with a choice in the Mixer tab, the modules of the mode of the track (Effects
    // tab: effects, Part and Drums pages) with fixed values, the forced mode (null: none).
    (List<int> Channels, List<SettingsModule> Modules, string ForceMode) SettingsToWrite()
    {
        var channels = Enumerable.Range(0, 16)
            .Where(c => ChannelMixer.Override[c] || ChannelMixer.RhythmOverride[c] >= 0 ||
                        Enumerable.Range(0, MixerViewModel.ControllerCount).Any(k => ChannelMixer.IsFixed(k, c))).ToList();
        var modules = ModuleSettings.All.Where(m => (m.Standard == null || m.Standard == Mixer.Standard) && m.HasFixed).ToList();
        return (channels, modules, isForced ? selectedMode : null);
    }

    // Settings applied to the whole track (fixed values in blue, forced mode), as opposed to the changes of the events.
    bool HasGlobalSettings() => GetSettingsSignature(false) != "";

    // Fingerprint of the savable settings (chosen instruments, fixed values, forced mode, and the changes of the events
    // unless withEdits is false); empty if there are none.
    string GetSettingsSignature(bool withEdits = true)
    {
        var parts = new List<string>();
        for (int c = 0; c < 16; c++)
        {
            if (ChannelMixer.Override[c]) parts.Add($"i{c}={ChannelMixer.OverrideMsb[c]}/{ChannelMixer.OverrideLsb[c]}/{ChannelMixer.OverrideProgram[c]}");
            if (ChannelMixer.RhythmOverride[c] >= 0) parts.Add($"r{c}={ChannelMixer.RhythmOverride[c]}");
            for (int k = 0; k < MixerViewModel.ControllerCount; k++)
                if (ChannelMixer.IsFixed(k, c)) parts.Add($"c{k}/{c}={ChannelMixer.FixedValue(k, c)}");
        }
        foreach (var m in ModuleSettings.All)
            foreach (var (key, v) in m.Fixed()) parts.Add($"{m.Name}{key.A}/{key.B}/{key.C}={v}");
        if (isForced) parts.Add($"mode={selectedMode}");
        if (withEdits && editedFile != null) parts.Add($"edits={editVersion}");   // Event Editor (changes, Record)
        return string.Join(";", parts);
    }

    // Settings changed since the last save? (muted channels do not count: they are not saved in the file)
    bool HasUnsavedSettings()
    {
        var signature = GetSettingsSignature();
        return signature != "" && signature != savedSignature;
    }

    // Before leaving the current track's settings (another file, closing): if they have not been
    // saved, pauses playback and asks. Returns true to continue, false to stay.
    public bool ConfirmLeaveSettings()
    {
        // Empty playlist, or no track loaded: there is no file to save the settings to.
        if (Playlist.Count == 0 || settingsFile == null) return true;
        if (!HasUnsavedSettings()) return true;

        // Playback state: playing, paused, or finished (end of track reached).
        bool running = playback != null && playback.IsRunning;
        bool finished = playback != null && state == "playing" && !running;
        if (running) { PausePlayback(); UpdateNumbers(); }

        bool proceed;
        prompting = true;
        try
        {
            var answer = view.AskYesNoCancel(
                "Settings have not been saved. Do you want to save?");
            proceed = answer switch
            {
                true => SaveModifiedMidi(reload: false),
                false => true,
                null => false,
            };
        }
        finally { prompting = false; }

        if (!proceed)
        {
            // Staying: resume if playback was running; clean stop if the track had ended.
            if (running) { playback.Start(); SetState("playing"); UpdateNumbers(); }
            else if (finished) { StopTrack(); UpdateNumbers(); }
            Status = "Settings kept on this track.";
        }
        return proceed;
    }
}
