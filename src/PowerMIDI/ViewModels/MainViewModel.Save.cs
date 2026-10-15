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
    // quiet: no report shown afterwards (save imposed by the Insert mode).
    // reload: saved over the track being played, the file is loaded again (its settings are now in it); false when
    // the track is being left anyway.
    bool SaveModifiedMidi(bool quiet = false, bool ask = false, bool reload = true)
    {
        if (insertMode) { Status = "Insert mode: release Insert (or Stop) before saving."; return false; }
        var source = settingsFile ?? current?.Path;
        if (source == null) { Status = "No current track to save."; return false; }
        int controllers = MixerViewModel.ControllerCount, effectCount = EffectsViewModel.EffectCount;
        bool hasEffects = Enumerable.Range(0, effectCount).Any(GsXgEffects.IsFixed);
        var channels = Enumerable.Range(0, 16)
            .Where(c => ChannelMixer.Override[c] || ChannelMixer.RhythmOverride[c] >= 0 ||
                        Enumerable.Range(0, controllers).Any(k => ChannelMixer.IsFixed(k, c))).ToList();
        var forceMode = isForced ? selectedMode : null;
        bool mt32 = Mixer.Standard == "MT-32" && Mt32Effects.HasFixed;   // MT-32 reverb values fixed in the Effects tab
        bool gm2 = Mixer.Standard == "GM2" && Gm2Effects.HasFixed;       // GM2 reverb / chorus values fixed there
        bool drums = Mixer.Standard == "XG" && (XgDrumSetup.HasFixed || XgDrumSetup.HasPartModes);   // XG drum setups and part modes
        if (channels.Count == 0 && !hasEffects && forceMode == null && !mt32 && !gm2 && !drums && insertions.Count == 0)
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

        // Re-read from disk: the original file is never modified in memory or during playback.
        var midi = MidiTools.ReadFile(source);
        var report = new List<string>();
        // Insertions (Insert button) first: the settings fixed for the whole track then replace them like the file's.
        if (insertions.Count > 0)
            report.Add($"insertions: {insertions.Count} position(s), {MidiInsertions.Apply(midi, insertions)} event(s) written");
        report.Add(MidiFileEditor.ApplyChanges(midi, apply, msb, lsb, program, applyCc, ccValues, rhythm));
        // Effects fixed in the Effects tab.
        var applyFx = new bool[effectCount];
        var fxValues = new int[effectCount];
        for (int fx = 0; fx < effectCount; fx++) { applyFx[fx] = GsXgEffects.IsFixed(fx); fxValues[fx] = GsXgEffects.FixedValue(fx); }
        if (hasEffects) report.Add(MidiFileEditor.ApplyEffects(midi, applyFx, fxValues));
        if (mt32) report.Add(MidiFileEditor.WriteMt32Reverb(midi));
        if (gm2) report.Add(MidiFileEditor.WriteGm2Effects(midi));
        if (drums) report.Add(MidiFileEditor.WriteXgDrumSetup(midi));
        // Forced mode: last, so that the new reset comes before all the settings inserted above.
        if (forceMode != null)
        {
            var reset = MidiTools.GetModeEvents(forceMode);
            // GM / GM2 have no GS or XG parameters: their SysEx are always removed, otherwise the file would still
            // be detected (and played) as GS / XG ("GM On followed by GS / XG SysEx").
            bool clean = isCleaned || forceMode is "GM" or "GM2";
            var drop = clean ? MidiTools.GetOtherModeManufacturers(forceMode) : Array.Empty<int>();
            report.Add($"{MidiFileEditor.ForceReset(midi, reset, drop)} (mode {forceMode})");
        }
        MidiTools.WriteFile(midi, target);

        savedSignature = GetSettingsSignature();
        lastSavedTarget = target;
        // Saved over the track being played: its settings and insertions are now in the file. It is loaded again at
        // the same position, with nothing pending (otherwise the next save would write the insertions twice).
        if (reload && current != null && SamePath(target, current.Path) && SamePath(source, current.Path))
        {
            bool wasPlaying = state == "playing";
            SwitchToSavedFile(target);
            if (wasPlaying) { playback.Start(); SetState("playing"); UpdateNumbers(); }
        }
        Status = $"Saved: {target}";
        if (!quiet) view.ShowMessage($"File saved:\n{target}\n\n{string.Join("\n", report.Where(s => !string.IsNullOrEmpty(s)))}");
        return true;
    }

    // Settings applied to the whole track (fixed values in blue, forced mode), as opposed to the insertions.
    bool HasGlobalSettings() => GetSettingsSignature(false) != "";

    // Fingerprint of the savable settings (chosen instruments, fixed values, forced mode, and the insertions unless
    // withInsertions is false); empty if there are none.
    string GetSettingsSignature(bool withInsertions = true)
    {
        var parts = new List<string>();
        for (int c = 0; c < 16; c++)
        {
            if (ChannelMixer.Override[c]) parts.Add($"i{c}={ChannelMixer.OverrideMsb[c]}/{ChannelMixer.OverrideLsb[c]}/{ChannelMixer.OverrideProgram[c]}");
            if (ChannelMixer.RhythmOverride[c] >= 0) parts.Add($"r{c}={ChannelMixer.RhythmOverride[c]}");
            for (int k = 0; k < MixerViewModel.ControllerCount; k++)
                if (ChannelMixer.IsFixed(k, c)) parts.Add($"c{k}/{c}={ChannelMixer.FixedValue(k, c)}");
        }
        for (int fx = 0; fx < EffectsViewModel.EffectCount; fx++)
            if (GsXgEffects.IsFixed(fx)) parts.Add($"e{fx}={GsXgEffects.FixedValue(fx)}");
        for (int p = 0; p < Mt32Effects.Count; p++)
            if (Mt32Effects.IsFixed(p)) parts.Add($"m{p}={Mt32Effects.FixedValue(p)}");
        for (int p = 0; p < Gm2Effects.Count; p++)
            if (Gm2Effects.IsFixed(p)) parts.Add($"g{p}={Gm2Effects.FixedValue(p)}");
        foreach (var (s, n, p, v) in XgDrumSetup.Fixed()) parts.Add($"d{s}/{n}/{p}={v}");
        for (int c = 0; c < 16; c++)
            if (XgDrumSetup.PartModeOverride[c] >= 0) parts.Add($"pm{c}={XgDrumSetup.PartModeOverride[c]}");
        if (isForced) parts.Add($"mode={selectedMode}/{isCleaned}");
        if (withInsertions) foreach (var insertion in insertions) parts.Add($"ins{insertion.Tick}={insertion.Description}");
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
        CancelInsertMode();   // closing the window in Insert mode: the changes in progress are dropped first
        // Empty playlist, or no track loaded: there is no file to save the settings to.
        if (Playlist.Count == 0 || settingsFile == null) return true;
        if (!HasUnsavedSettings()) return true;

        // Playback state: playing, paused, or finished (end of track reached).
        bool running = playback != null && playback.IsRunning;
        bool finished = playback != null && state == "playing" && !running;
        if (running) { playback.Stop(); SetState("paused"); UpdateNumbers(); }

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
