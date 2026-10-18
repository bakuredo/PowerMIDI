using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// Append / Replace of the Event Editor. First click: the settings in blue so far (blue values, forced mode) are written
// in the whole track (WriteSettingsInTrack), so that nothing is blue. The settings are then changed in the Mixer and
// Effects tabs (heard right away). Second click: those settings (Mixer: drum parts, instruments, controllers; Effects
// tab: every settings module) are written as events at the event selected (Append: after it; Replace: instead of the
// selected events). They go into the copy of the track the Event Editor changes (MainViewModel.Events.cs: undo, saved by
// "Save MIDI file…"); the settings written become free again: the file's values, now set by these events.
public sealed partial class MainViewModel
{
    TempoMap tempoMap;   // tempo map of the current track

    long CurrentTick() => playback != null ? ((MidiTimeSpan)playback.GetCurrentTime<MidiTimeSpan>()).TimeSpan : 0;

    // The track to play: the file, with the changes of the Event Editor (and the insertions) if it is the one they
    // belong to.
    MidiFile ReadTrackFile(string path) =>
        path == settingsFile && editedFile != null ? editedFile.Clone() : MidiTools.ReadFile(path);

    // Writes the settings in blue as events of a track at a position: after the event at index (-1: before the events
    // there), or, with replaced, instead of those events (at the place of the event at index, one of them). Returns
    // what was written (null: nothing in blue). Playback stays where it is.
    public string WriteFixed(int track, int index, long tick, IReadOnlyList<(int Track, int Index)> replaced)
    {
        if (EditBlocked != null) return null;
        var (events, description) = BuildInsertion();
        if (events.Count == 0) return null;
        long position = CurrentTick();
        bool wasPlaying = state == "playing";
        Edit(file =>
        {
            int place = MidiEventEdits.Insert(file, track, index, tick, events, replaced);
            return (track, place + events.Count - 1);   // the last event written: selected
        }, position, false);
        // The file's values as they are now at the position (with the events written), sent as the settings are freed.
        RebuildFileState(editedFile, position, CurrentStandard);
        ClearWrittenSettings();
        Mixer.Refresh();
        Effects.Refresh();
        if (wasPlaying) { playback.Start(); SetState("playing"); UpdateNumbers(); }
        return description;
    }

    // The settings of the whole track (blue values, forced mode) become events of its copy, as the save writes them
    // (Event Editor: one change, undone with Ctrl+Z), at the same position, playing or not; they are cleared without
    // sending anything: the module already has them, and the track now contains them.
    public void WriteSettingsInTrack()
    {
        if (EditBlocked != null || !HasGlobalSettings()) return;
        long tick = CurrentTick();
        bool wasPlaying = state == "playing";
        var (channels, modules, forceMode) = SettingsToWrite();
        Edit(file => { WriteSettings(file, channels, modules, forceMode); return null; }, tick, false);
        var muted = (bool[])ChannelMixer.Muted.Clone();   // mutes are a listening aid, not settings: kept
        ClearManualSettings(edits: false);
        for (int c = 0; c < 16; c++) if (muted[c]) Mixer.SetMute(c, true, false);
        var std = MidiTools.GetStandard(editedFile);
        ChannelMixer.Mt32Mode = std == "MT-32";
        ChannelMixer.Gm2Mode = std == "GM2";
        ChannelMixer.XgMode = std == "XG";
        ChannelMixer.GsMode = std == "GS";
        Mixer.Standard = std;
        Effects.Standard = std;
        CurrentStandard = std;
        RebuildFileState(editedFile, tick, std);
        Mixer.Refresh();
        Effects.Refresh();
        if (wasPlaying) { playback.Start(); SetState("playing"); UpdateNumbers(); }
    }

    // Append / Replace cancelled: the settings in blue go back to the file's values (sent).
    public void DropFixed()
    {
        ClearWrittenSettings();
        Mixer.Refresh();
        Effects.Refresh();
    }

    // The settings written (see BuildInsertion) go back to the file's values, sent to the module.
    void ClearWrittenSettings()
    {
        for (int c = 0; c < 16; c++)
        {
            if (ChannelMixer.RhythmOverride[c] >= 0) ChannelMixer.ClearRhythmOverride(output, c);
            if (ChannelMixer.Override[c]) ChannelMixer.ClearOverride(output, c);
            for (int k = 0; k < MixerViewModel.ControllerCount; k++)
                if (ChannelMixer.IsFixed(k, c)) ChannelMixer.ClearController(output, k, c);
        }
        foreach (var m in ModuleSettings.All)
            if (m.HasFixed) m.ClearAll(m.IsActive() ? output : null);
    }

    // The saved file (track + its settings, now written in it) replaces the current track in the playlist and in
    // the playback, paused at the same position. The manual settings are cleared without sending anything: the
    // module already has them, and the file now contains them.
    void SwitchToSavedFile(string target)
    {
        long tick = CurrentTick();
        if (playback != null) { playback.Stop(); ReleaseNotes(); playback.Dispose(); playback = null; }

        var midi = MidiTools.ReadFile(target);
        var item = new PlaylistItem
        {
            Name = Path.GetFileName(target), Path = Path.GetFullPath(target),
            Standard = MidiTools.GetStandard(midi), Duration = MidiTools.FormatTime(MidiTools.GetDuration(midi)),
        };
        int index = current != null ? Playlist.IndexOf(current) : -1;
        if (index >= 0) Playlist[index] = item; else Playlist.Add(item);
        current = item;

        var muted = (bool[])ChannelMixer.Muted.Clone();   // mutes are a listening aid, not saved: kept
        ClearManualSettings();   // forced mode included: back to Auto, the saved file starts with that mode's reset
        for (int c = 0; c < 16; c++) if (muted[c]) Mixer.SetMute(c, true, false);
        savedSignature = "";
        lastSavedTarget = item.Path;   // a file saved by PowerMIDI: the next saves go to it without asking
        settingsFile = item.Path;

        var std = item.Standard;
        ChannelMixer.ForceMode = false;
        ChannelMixer.Mt32Mode = std == "MT-32";
        ChannelMixer.Gm2Mode = std == "GM2";
        ChannelMixer.XgMode = std == "XG";
        ChannelMixer.GsMode = std == "GS";
        ChannelMixer.DropManufacturers = Array.Empty<int>();
        Mixer.Standard = std;
        Effects.Standard = std;
        ShowTrackInfo(midi, item, std);
        RebuildFileState(midi, tick, std);

        CreatePlayback(midi);
        var at = TickToTime(tick);
        playback.MoveToTime(new MetricTimeSpan(at));
        ApplyMixerState();   // e.g. the volume of channels that were muted
        SetPosition(at.TotalSeconds);
        TimeText = $"{MidiTools.FormatTime(at)} / {durationText}";
        SetState("paused");
        SelectedTrack = item;
        UpdateNumbers();
        Mixer.Refresh();
        Effects.Refresh();
    }

    // Values "from the file" as they are at a position: the file's events before it go through the mixer, without
    // sending anything (the module is already in that state).
    static void RebuildFileState(MidiFile midi, long tick, string std)
    {
        var device = ChannelMixer.Device;
        ChannelMixer.Device = null;   // nothing resent after the resets of the file
        try
        {
            ChannelMixer.ResetChannelState(std == "XG");
            foreach (var t in midi.GetTimedEvents())
            {
                if (t.Time >= tick) break;   // the events at the position are played when playback resumes
                if (t.Event is NoteOnEvent on) ChannelMixer.Used[on.Channel] = true;
                else if (t.Event is not NoteOffEvent) ChannelMixer.Callback(t.Event, t.Time, TimeSpan.Zero);
            }
        }
        finally { ChannelMixer.Device = device; }
    }


    // Rebuilds the playback (the track, with its changes) at a tick, playing or not.
    void ReloadPlayback(long tick, bool play)
    {
        if (current == null) return;
        if (playback != null) { playback.Stop(); ReleaseNotes(); playback.Dispose(); playback = null; }
        CreatePlayback(ReadTrackFile(current.Path));
        var at = TickToTime(tick);
        playback.MoveToTime(new MetricTimeSpan(at));
        ChaseState(tick);   // the channels as the changed track has them there
        SetPosition(at.TotalSeconds);
        TimeText = $"{MidiTools.FormatTime(at)} / {durationText}";
        if (play) { playback.Start(); SetState("playing"); }
        else SetState("paused");
        UpdateNumbers();
    }

    TimeSpan TickToTime(long tick) => TimeConverter.ConvertTo<MetricTimeSpan>(tick, tempoMap);


    // Events of the settings in blue, with their description. Order: per channel the drum part, the instrument, the
    // controllers; then the effects, module by module (effect types before their parameters, part modes before drum
    // setups).
    (List<MidiEvent> Events, string Description) BuildInsertion()
    {
        var events = new List<MidiEvent>();
        var texts = new List<string>();
        for (int c = 0; c < 16; c++)
        {
            var parts = new List<string>();
            int rhythm = ChannelMixer.RhythmOverride[c];
            if (rhythm >= 0)
            {
                events.Add(ChannelMixer.RhythmPartEvent(c, rhythm));
                parts.Add(rhythm == 0 ? "normal part" : $"drum part {rhythm}");
            }
            if (rhythm >= 0 || ChannelMixer.Override[c])
            {
                // After a drum part change, the module reads the program again: the instrument is always written.
                var (msb, lsb, program) = ChannelMixer.Override[c]
                    ? (ChannelMixer.OverrideMsb[c], ChannelMixer.OverrideLsb[c], ChannelMixer.OverrideProgram[c])
                    : (ChannelMixer.BankMsb[c], ChannelMixer.BankLsb[c], ChannelMixer.Program[c]);
                events.Add(new ControlChangeEvent((SevenBitNumber)0, (SevenBitNumber)msb) { Channel = (FourBitNumber)c });
                events.Add(new ControlChangeEvent((SevenBitNumber)32, (SevenBitNumber)lsb) { Channel = (FourBitNumber)c });
                events.Add(new ProgramChangeEvent((SevenBitNumber)program) { Channel = (FourBitNumber)c });
                if (ChannelMixer.Override[c]) parts.Add(Mixer.InstrumentName(c, msb, lsb, program));
            }
            for (int k = 0; k < MixerViewModel.ControllerCount; k++)
            {
                if (!ChannelMixer.IsFixed(k, c)) continue;
                int v = ChannelMixer.FixedValue(k, c);
                events.Add(new ControlChangeEvent((SevenBitNumber)ChannelMixer.ControllerNumbers[k], (SevenBitNumber)v) { Channel = (FourBitNumber)c });
                if (k == ChannelMixer.PortamentoTime)   // heard only with the portamento on (off at 0)
                    events.Add(new ControlChangeEvent((SevenBitNumber)ChannelMixer.PortamentoSwitch, (SevenBitNumber)ChannelMixer.PortamentoSwitchFor(v)) { Channel = (FourBitNumber)c });
                parts.Add($"{MixerViewModel.ControllerNames[k]} {MixerViewModel.FormatControllerValue(k, v)}");
            }
            if (parts.Count > 0) texts.Add($"ch {c + 1}: {string.Join(", ", parts)}");
        }
        foreach (var m in ModuleSettings.All)
            foreach (var (key, v) in m.Fixed())
            {
                events.AddRange(m.Events(key, v));
                texts.Add(m.Describe(key, v));
            }
        return (events, string.Join(" · ", texts));
    }
}
