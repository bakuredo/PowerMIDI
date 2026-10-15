using System.Collections.ObjectModel;
using System.Windows.Input;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// "Insert" mode: writes Mixer / Effects settings at a position of the track instead of the whole track.
// - Insert pressed (Mixer or Effects tab; stopped: at the start of the track): playback pauses. Settings applied
//   to the whole track (blue values, forced mode) must be saved first: the saved file then replaces the track at
//   the same position (cancelled save: no Insert mode). The position is snapped to the start of the beat; the
//   settings are remembered.
// - The user changes settings in the Mixer and Effects tabs (heard right away).
// - Insert released: the settings changed since then are inserted at that position (in memory, written by
//   "Save MIDI file…"), become free again (or go back to their previous fixed value); playback stays paused there.
// - Stop, a seek or another track: cancelled, the changed settings go back to how they were.
// Each insertion shows as a marker above the position bar; right click on it removes it.
public sealed partial class MainViewModel
{
    readonly List<MidiInsertion> insertions = new();   // insertions of the current track (settingsFile)
    TempoMap tempoMap;                                   // tempo map of the current track
    SettingsSnapshot snapshot;                           // settings when the Insert mode started
    long insertTick;

    public ObservableCollection<InsertMarker> InsertMarkers { get; } = new();

    bool insertMode;
    public bool IsInsertMode
    {
        get => insertMode;
        set
        {
            if (value != insertMode) Run(() => { if (value) BeginInsert(); else CommitInsert(); });
            OnPropertyChanged();   // the button follows the real state (e.g. not available: stays off)
        }
    }

    // Insert button available: in Insert mode (to release it), or when a track is loaded (stopped: at its start).
    bool insertEnabled;
    public bool InsertEnabled { get => insertEnabled; private set => Set(ref insertEnabled, value); }

    // Called by the main timer and after each playback change.
    void UpdateInsertEnabled() => InsertEnabled = insertMode || current != null;

    long CurrentTick() => playback != null ? ((MidiTimeSpan)playback.GetCurrentTime<MidiTimeSpan>()).TimeSpan : 0;

    // The track to play: the file, with the insertions if it is the one they belong to.
    MidiFile ReadTrackFile(string path)
    {
        var midi = MidiTools.ReadFile(path);
        if (path == settingsFile && insertions.Count > 0) MidiInsertions.Apply(midi, insertions);
        return midi;
    }

    void BeginInsert()
    {
        UpdateInsertEnabled();
        if (current == null || tempoMap == null) { Status = "Insert: play a track first."; return; }
        bool wasPlaying = state == "playing";
        if (wasPlaying) { playback.Stop(); SetState("paused"); UpdateNumbers(); }

        // Settings applied to the whole track (blue values, forced mode) must be saved first: the saved file then
        // replaces the track, at the same position. Nothing is blue any more, so that every change made from here
        // is an insertion (the two kinds of changes were too easy to mix up).
        if (HasGlobalSettings())
        {
            // Already saved and unchanged since: that file is used as it is. Otherwise saved now (to the file of the
            // previous save if there is one, else the dialog asks where).
            bool confirmed;
            prompting = true;
            try { confirmed = view.Confirm("Settings in blue must be saved before using Insert. File will be saved, then reloaded."); }
            finally { prompting = false; }
            bool saved = confirmed &&
                ((!HasUnsavedSettings() && lastSavedTarget != null && File.Exists(lastSavedTarget)) || SaveModifiedMidi(quiet: true));
            if (!saved)
            {
                if (wasPlaying) { playback.Start(); SetState("playing"); UpdateNumbers(); }
                Status = "Insert: the settings of the whole track (in blue) must be saved first — cancelled.";
                return;
            }
            // (Saved over the track itself: the save has already loaded it again.)
            if (HasGlobalSettings() || !SamePath(current.Path, lastSavedTarget)) SwitchToSavedFile(lastSavedTarget);
        }

        insertTick = MidiInsertions.SnapToBeat(CurrentTick(), tempoMap);
        var at = TickToTime(insertTick);
        SetPosition(at.TotalSeconds);
        TimeText = $"{MidiTools.FormatTime(at)} / {durationText}";
        snapshot = SettingsSnapshot.Take();
        insertMode = true;
        Status = $"Insert at {MidiTools.FormatTime(at)} ({BarBeat(insertTick)}): change the Mixer / Effects settings, " +
                 "then release Insert to insert them · Stop: cancel";
    }

    // The saved file (track + its settings, now written in it) replaces the current track in the playlist and in
    // the playback, paused at the same position. The manual settings are cleared without sending anything: the
    // module already has them, and the file now contains them.
    void SwitchToSavedFile(string target)
    {
        long tick = CurrentTick();
        if (playback != null) { playback.Stop(); playback.Dispose(); playback = null; }

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
        RefreshMarkers();
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

    // Cancels the Insert mode: the settings changed since it started go back to how they were (sent to the module).
    void CancelInsertMode()
    {
        if (!insertMode) return;
        insertMode = false;
        OnPropertyChanged(nameof(IsInsertMode));
        snapshot.Restore(output);
        snapshot = null;
        Mixer.Refresh();
        Effects.Refresh();
        UpdateInsertEnabled();
        Status = "Insert cancelled.";
    }

    // Insert released: inserts the changed settings; playback stays paused at the insertion point.
    void CommitInsert()
    {
        if (!insertMode) return;
        insertMode = false;
        OnPropertyChanged(nameof(IsInsertMode));
        var (events, description) = BuildInsertion(snapshot);
        if (events.Count > 0)
        {
            // Inserted settings become free again (or go back to their previous fixed value): the inserted events,
            // played from the insertion point, now set them. Nothing is sent: the module already has these values.
            snapshot.Restore(null);
            insertions.Add(new MidiInsertion { Tick = insertTick, Events = events, Description = description });
        }
        snapshot = null;
        ReloadPlayback(insertTick, false);
        Status = events.Count > 0 ? $"Inserted at {MidiTools.FormatTime(TickToTime(insertTick))} ({BarBeat(insertTick)}): {description}"
                                  : "Nothing changed: no insertion.";
    }

    // Rebuilds the playback (file + insertions) at a tick, playing or not.
    void ReloadPlayback(long tick, bool play)
    {
        if (current == null) return;
        if (playback != null) { playback.Stop(); playback.Dispose(); playback = null; }
        CreatePlayback(ReadTrackFile(current.Path));
        var at = TickToTime(tick);
        playback.MoveToTime(new MetricTimeSpan(at));
        ApplyMixerState();
        SetPosition(at.TotalSeconds);
        TimeText = $"{MidiTools.FormatTime(at)} / {durationText}";
        if (play) { playback.Start(); SetState("playing"); }
        else SetState("paused");
        UpdateNumbers();
        RefreshMarkers();
        UpdateInsertEnabled();
    }

    void RemoveInsertion(MidiInsertion insertion)
    {
        if (insertMode) return;
        insertions.Remove(insertion);
        if (playback != null) ReloadPlayback(CurrentTick(), state == "playing");
        else RefreshMarkers();
        Status = $"Insertion removed: {insertion.Description}";
    }

    // Clears the insertions (another track, or the track removed from the playlist). Returns true if there were any.
    bool ClearInsertions()
    {
        bool had = insertions.Count > 0;
        insertions.Clear();
        RefreshMarkers();
        return had;
    }

    void RefreshMarkers()
    {
        InsertMarkers.Clear();
        if (tempoMap == null) return;
        foreach (var insertion in insertions.OrderBy(i => i.Tick))
        {
            var at = TickToTime(insertion.Tick);
            var ins = insertion;
            InsertMarkers.Add(new InsertMarker
            {
                Fraction = Math.Min(1, at.TotalSeconds / Duration),
                ToolTip = $"Insertion at {MidiTools.FormatTime(at)}: {insertion.Description} (right click: remove)",
                RemoveCommand = new RelayCommand(() => Run(() => RemoveInsertion(ins))),
            });
        }
    }

    TimeSpan TickToTime(long tick) => TimeConverter.ConvertTo<MetricTimeSpan>(tick, tempoMap);

    string BarBeat(long tick)
    {
        var bbt = TimeConverter.ConvertTo<BarBeatTicksTimeSpan>(tick, tempoMap);
        return $"bar {bbt.Bars + 1}, beat {bbt.Beats + 1}";
    }

    // Events of the settings changed since the snapshot (now fixed with another value), with their description.
    // Order: per channel the drum part, the instrument, the controllers; then the effects, types first.
    (List<(int Channel, MidiEvent Event)> Events, string Description) BuildInsertion(SettingsSnapshot s)
    {
        var events = new List<(int, MidiEvent)>();
        var texts = new List<string>();
        for (int c = 0; c < 16; c++)
        {
            var parts = new List<string>();
            int rhythm = ChannelMixer.RhythmOverride[c];
            bool rhythmChanged = rhythm >= 0 && rhythm != s.Rhythm[c];
            bool instrumentChanged = ChannelMixer.Override[c] &&
                (!s.Override[c] || (ChannelMixer.OverrideMsb[c], ChannelMixer.OverrideLsb[c], ChannelMixer.OverrideProgram[c]) != (s.Msb[c], s.Lsb[c], s.Program[c]));
            if (rhythmChanged)
            {
                events.Add((c, ChannelMixer.RhythmPartEvent(c, rhythm)));
                parts.Add(rhythm == 0 ? "normal part" : $"drum part {rhythm}");
            }
            if (rhythmChanged || instrumentChanged)
            {
                // After a drum part change, the module reads the program again: the instrument is always resent.
                var (msb, lsb, program) = ChannelMixer.Override[c]
                    ? (ChannelMixer.OverrideMsb[c], ChannelMixer.OverrideLsb[c], ChannelMixer.OverrideProgram[c])
                    : (ChannelMixer.BankMsb[c], ChannelMixer.BankLsb[c], ChannelMixer.Program[c]);
                events.Add((c, new ControlChangeEvent((SevenBitNumber)0, (SevenBitNumber)msb) { Channel = (FourBitNumber)c }));
                events.Add((c, new ControlChangeEvent((SevenBitNumber)32, (SevenBitNumber)lsb) { Channel = (FourBitNumber)c }));
                events.Add((c, new ProgramChangeEvent((SevenBitNumber)program) { Channel = (FourBitNumber)c }));
                if (instrumentChanged) parts.Add(Mixer.InstrumentName(c, msb, lsb, program));
            }
            for (int k = 0; k < MixerViewModel.ControllerCount; k++)
            {
                if (!ChannelMixer.IsFixed(k, c)) continue;
                int v = ChannelMixer.FixedValue(k, c);
                if (s.CcFixed[k, c] && s.CcValue[k, c] == v) continue;
                events.Add((c, new ControlChangeEvent((SevenBitNumber)ChannelMixer.ControllerNumbers[k], (SevenBitNumber)v) { Channel = (FourBitNumber)c }));
                if (k == ChannelMixer.PortamentoTime)   // heard only with the portamento on (off at 0)
                    events.Add((c, new ControlChangeEvent((SevenBitNumber)ChannelMixer.PortamentoSwitch, (SevenBitNumber)ChannelMixer.PortamentoSwitchFor(v)) { Channel = (FourBitNumber)c }));
                parts.Add($"{MixerViewModel.ControllerNames[k]} {MixerViewModel.FormatControllerValue(k, v)}");
            }
            if (parts.Count > 0) texts.Add($"ch {c + 1}: {string.Join(", ", parts)}");
        }

        foreach (bool macros in new[] { true, false })
            for (int fx = 0; fx < EffectsViewModel.EffectCount; fx++)
            {
                if (GsXgEffects.IsType(fx) != macros || !GsXgEffects.IsFixed(fx)) continue;
                int v = GsXgEffects.FixedValue(fx);
                if (s.FxFixed[fx] && s.FxValue[fx] == v) continue;
                events.Add((-1, GsXgEffects.ParamEvent(fx, v)));
                texts.Add(Effects.DescribeEffect(fx, v));
            }
        for (int p = 0; p < Mt32Effects.Count; p++)
        {
            if (!Mt32Effects.IsFixed(p)) continue;
            int v = Mt32Effects.FixedValue(p);
            if (s.Mt32Fixed[p] && s.Mt32Value[p] == v) continue;
            // Reverb of a part: in the track of its channel when it has one.
            int part = p - Mt32Effects.PartFirst;
            int channel = part >= 0 && Mt32Effects.PartChannel[part] <= 15 ? Mt32Effects.PartChannel[part] : -1;
            foreach (var e in Mt32Effects.ParamEvents(p, v)) events.Add((channel, e));
            texts.Add(Effects.DescribeMt32(p, v));
        }
        foreach (bool types in new[] { true, false })
            for (int p = 0; p < Gm2Effects.Count; p++)
            {
                if (Gm2Effects.IsType(p) != types || !Gm2Effects.IsFixed(p)) continue;
                int v = Gm2Effects.FixedValue(p);
                if (s.Gm2Fixed[p] && s.Gm2Value[p] == v) continue;
                events.Add((-1, Gm2Effects.ParamEvent(p, v)));
                texts.Add(Effects.DescribeGm2(p, v));
            }
        foreach (var (c, p, v) in Gm2PartParams.Fixed())
        {
            if (s.Gm2PartFixed.TryGetValue((c, p), out int before) && before == v) continue;
            foreach (var e in Gm2PartParams.ParamEvents(c, p, v)) events.Add((c, e));
            texts.Add(Effects.DescribeGm2Part(c, p, v));
        }
        foreach (var (c, n, p, v) in Gm2DrumParams.Fixed())
        {
            if (s.Gm2DrumFixed.TryGetValue((c, n, p), out int before) && before == v) continue;
            events.Add((c, Gm2DrumParams.ParamEvent(c, n, p, v)));
            texts.Add(Effects.DescribeGm2Drum(c, n, p, v));
        }
        foreach (var (c, p, v) in GsPartParams.Fixed())
        {
            if (s.GsPartFixed.TryGetValue((c, p), out int before) && before == v) continue;
            events.Add((c, GsPartParams.ParamEvent(c, p, v)));
            texts.Add(Effects.DescribeGsPart(c, p, v));
        }
        foreach (var (m, n, p, v) in GsDrumMap.Fixed())
        {
            if (s.GsDrumFixed.TryGetValue((m, n, p), out int before) && before == v) continue;
            // In the track of the first channel of the drum part when it has one.
            int channel = Enumerable.Range(0, 16).FirstOrDefault(c => GsDrumMap.MapOf(c) == m, -1);
            events.Add((channel, GsDrumMap.ParamEvent(m, n, p, v)));
            texts.Add(Effects.DescribeGsDrum(m, n, p, v));
        }
        foreach (var (c, p, v) in XgPartParams.Fixed())
        {
            if (s.PartFixed.TryGetValue((c, p), out int before) && before == v) continue;
            events.Add((c, XgPartParams.ParamEvent(c, p, v)));
            texts.Add(Effects.DescribePart(c, p, v));
        }
        for (int c = 0; c < 16; c++)
        {
            int mode = XgDrumSetup.PartModeOverride[c];
            if (mode < 0 || mode == s.PartMode[c]) continue;
            events.Add((c, XgDrumSetup.PartModeEvent(c, mode)));
            texts.Add(Effects.DescribePartMode(c, mode));
        }
        foreach (var (ds, n, p, v) in XgDrumSetup.Fixed())
        {
            if (s.DrumFixed.TryGetValue((ds, n, p), out int before) && before == v) continue;
            // In the track of the first channel of the setup when it has one.
            int channel = Enumerable.Range(0, 16).FirstOrDefault(c => XgDrumSetup.SetupOf(c) == ds, -1);
            events.Add((channel, XgDrumSetup.ParamEvent(ds, n, p, v)));
            texts.Add(Effects.DescribeDrum(ds, n, p, v));
        }
        return (events, string.Join(" · ", texts));
    }

    // Manual settings at a given moment: instruments, drum parts, fixed controllers and effects.
    sealed class SettingsSnapshot
    {
        public readonly bool[] Override = new bool[16];
        public readonly int[] Msb = new int[16], Lsb = new int[16], Program = new int[16], Rhythm = new int[16];
        public readonly bool[,] CcFixed = new bool[MixerViewModel.ControllerCount, 16];
        public readonly int[,] CcValue = new int[MixerViewModel.ControllerCount, 16];
        public readonly bool[] FxFixed = new bool[EffectsViewModel.EffectCount];
        public readonly int[] FxValue = new int[EffectsViewModel.EffectCount];
        public readonly bool[] Mt32Fixed = new bool[Mt32Effects.Count];
        public readonly int[] Mt32Value = new int[Mt32Effects.Count];
        public readonly bool[] Gm2Fixed = new bool[Gm2Effects.Count];
        public readonly int[] Gm2Value = new int[Gm2Effects.Count];
        public readonly Dictionary<(int Setup, int Note, int Param), int> DrumFixed = new();
        public readonly int[] PartMode = new int[16];
        public readonly Dictionary<(int Channel, int Param), int> PartFixed = new();
        public readonly Dictionary<(int Map, int Note, int Param), int> GsDrumFixed = new();
        public readonly Dictionary<(int Channel, int Param), int> GsPartFixed = new();
        public readonly Dictionary<(int Channel, int Note, int Param), int> Gm2DrumFixed = new();
        public readonly Dictionary<(int Channel, int Param), int> Gm2PartFixed = new();

        public static SettingsSnapshot Take()
        {
            var s = new SettingsSnapshot();
            for (int c = 0; c < 16; c++)
            {
                s.Override[c] = ChannelMixer.Override[c];
                (s.Msb[c], s.Lsb[c], s.Program[c]) = (ChannelMixer.OverrideMsb[c], ChannelMixer.OverrideLsb[c], ChannelMixer.OverrideProgram[c]);
                s.Rhythm[c] = ChannelMixer.RhythmOverride[c];
                for (int k = 0; k < MixerViewModel.ControllerCount; k++)
                    (s.CcFixed[k, c], s.CcValue[k, c]) = (ChannelMixer.IsFixed(k, c), ChannelMixer.FixedValue(k, c));
            }
            for (int fx = 0; fx < EffectsViewModel.EffectCount; fx++)
                (s.FxFixed[fx], s.FxValue[fx]) = (GsXgEffects.IsFixed(fx), GsXgEffects.FixedValue(fx));
            for (int p = 0; p < Mt32Effects.Count; p++) (s.Mt32Fixed[p], s.Mt32Value[p]) = (Mt32Effects.IsFixed(p), Mt32Effects.FixedValue(p));
            for (int p = 0; p < Gm2Effects.Count; p++) (s.Gm2Fixed[p], s.Gm2Value[p]) = (Gm2Effects.IsFixed(p), Gm2Effects.FixedValue(p));
            foreach (var (ds, n, p, v) in XgDrumSetup.Fixed()) s.DrumFixed[(ds, n, p)] = v;
            Array.Copy(XgDrumSetup.PartModeOverride, s.PartMode, 16);
            foreach (var (c, p, v) in XgPartParams.Fixed()) s.PartFixed[(c, p)] = v;
            foreach (var (m, n, p, v) in GsDrumMap.Fixed()) s.GsDrumFixed[(m, n, p)] = v;
            foreach (var (c, p, v) in GsPartParams.Fixed()) s.GsPartFixed[(c, p)] = v;
            foreach (var (c, n, p, v) in Gm2DrumParams.Fixed()) s.Gm2DrumFixed[(c, n, p)] = v;
            foreach (var (c, p, v) in Gm2PartParams.Fixed()) s.Gm2PartFixed[(c, p)] = v;
            return s;
        }

        // Puts back the settings that changed since the snapshot. device: where to send them (null: nothing sent).
        public void Restore(Melanchall.DryWetMidi.Multimedia.IOutputDevice device)
        {
            for (int c = 0; c < 16; c++)
            {
                if (ChannelMixer.RhythmOverride[c] != Rhythm[c])
                {
                    if (Rhythm[c] >= 0) ChannelMixer.SetRhythmOverride(device, c, Rhythm[c]);
                    else ChannelMixer.ClearRhythmOverride(device, c);
                }
                if (ChannelMixer.Override[c] != Override[c] ||
                    (Override[c] && (ChannelMixer.OverrideMsb[c], ChannelMixer.OverrideLsb[c], ChannelMixer.OverrideProgram[c]) != (Msb[c], Lsb[c], Program[c])))
                {
                    if (Override[c]) ChannelMixer.SetOverride(device, c, Msb[c], Lsb[c], Program[c]);
                    else ChannelMixer.ClearOverride(device, c);
                }
                for (int k = 0; k < MixerViewModel.ControllerCount; k++)
                {
                    if (ChannelMixer.IsFixed(k, c) == CcFixed[k, c] && (!CcFixed[k, c] || ChannelMixer.FixedValue(k, c) == CcValue[k, c])) continue;
                    if (CcFixed[k, c]) ChannelMixer.SetController(device, k, c, CcValue[k, c]);
                    else ChannelMixer.ClearController(device, k, c);
                }
            }
            foreach (bool macros in new[] { true, false })
                for (int fx = 0; fx < EffectsViewModel.EffectCount; fx++)
                {
                    if (GsXgEffects.IsType(fx) != macros) continue;
                    if (GsXgEffects.IsFixed(fx) == FxFixed[fx] && (!FxFixed[fx] || GsXgEffects.FixedValue(fx) == FxValue[fx])) continue;
                    if (FxFixed[fx]) GsXgEffects.Set(device, fx, FxValue[fx]);
                    else GsXgEffects.Clear(device, fx);
                }
            for (int p = 0; p < Mt32Effects.Count; p++)
            {
                if (Mt32Effects.IsFixed(p) == Mt32Fixed[p] && (!Mt32Fixed[p] || Mt32Effects.FixedValue(p) == Mt32Value[p])) continue;
                var target = ChannelMixer.Mt32Mode ? device : null;
                if (Mt32Fixed[p]) Mt32Effects.Set(target, p, Mt32Value[p]);
                else Mt32Effects.Clear(target, p);
            }
            foreach (bool types in new[] { true, false })
                for (int p = 0; p < Gm2Effects.Count; p++)
                {
                    if (Gm2Effects.IsType(p) != types) continue;
                    if (Gm2Effects.IsFixed(p) == Gm2Fixed[p] && (!Gm2Fixed[p] || Gm2Effects.FixedValue(p) == Gm2Value[p])) continue;
                    var target = ChannelMixer.Gm2Mode ? device : null;
                    if (Gm2Fixed[p]) Gm2Effects.Set(target, p, Gm2Value[p]);
                    else Gm2Effects.Clear(target, p);
                }
            var gm2Target = ChannelMixer.Gm2Mode ? device : null;
            foreach (var (c, p, _) in Gm2PartParams.Fixed().ToList())
                if (!Gm2PartFixed.ContainsKey((c, p))) Gm2PartParams.Clear(gm2Target, c, p);
            foreach (var ((c, p), v) in Gm2PartFixed)
                if (!Gm2PartParams.IsFixed(c, p) || Gm2PartParams.FixedValue(c, p) != v) Gm2PartParams.Set(gm2Target, c, p, v);
            foreach (var (c, n, p, _) in Gm2DrumParams.Fixed().ToList())
                if (!Gm2DrumFixed.ContainsKey((c, n, p))) Gm2DrumParams.Clear(gm2Target, c, n, p);
            foreach (var ((c, n, p), v) in Gm2DrumFixed)
                if (!Gm2DrumParams.IsFixed(c, n, p) || Gm2DrumParams.FixedValue(c, n, p) != v) Gm2DrumParams.Set(gm2Target, c, n, p, v);
            var gsTarget = ChannelMixer.GsMode ? device : null;
            foreach (var (c, p, _) in GsPartParams.Fixed().ToList())
                if (!GsPartFixed.ContainsKey((c, p))) GsPartParams.Clear(gsTarget, c, p);
            foreach (var ((c, p), v) in GsPartFixed)
                if (!GsPartParams.IsFixed(c, p) || GsPartParams.FixedValue(c, p) != v) GsPartParams.Set(gsTarget, c, p, v);
            foreach (var (m, n, p, _) in GsDrumMap.Fixed().ToList())
                if (!GsDrumFixed.ContainsKey((m, n, p)))
                    foreach (int reset in GsDrumMap.Clear(gsTarget, m, n, p)) ChannelMixer.ResetGsDrumMap(gsTarget, reset);
            foreach (var ((m, n, p), v) in GsDrumFixed)
                if (!GsDrumMap.IsFixed(m, n, p) || GsDrumMap.FixedValue(m, n, p) != v) GsDrumMap.Set(gsTarget, m, n, p, v);
            var drumTarget = ChannelMixer.XgMode ? device : null;
            foreach (var (c, p, _) in XgPartParams.Fixed().ToList())
                if (!PartFixed.ContainsKey((c, p))) XgPartParams.Clear(drumTarget, c, p);
            foreach (var ((c, p), v) in PartFixed)
                if (!XgPartParams.IsFixed(c, p) || XgPartParams.FixedValue(c, p) != v) XgPartParams.Set(drumTarget, c, p, v);
            for (int c = 0; c < 16; c++)
            {
                if (XgDrumSetup.PartModeOverride[c] == PartMode[c]) continue;
                if (PartMode[c] >= 0) XgDrumSetup.SetPartMode(drumTarget, c, PartMode[c]);
                else XgDrumSetup.ClearPartMode(drumTarget, c);
            }
            foreach (var (s, n, p, _) in XgDrumSetup.Fixed().ToList())
                if (!DrumFixed.ContainsKey((s, n, p))) XgDrumSetup.Clear(drumTarget, s, n, p);
            foreach (var ((s, n, p), v) in DrumFixed)
                if (!XgDrumSetup.IsFixed(s, n, p) || XgDrumSetup.FixedValue(s, n, p) != v) XgDrumSetup.Set(drumTarget, s, n, p, v);
        }
    }
}

// Marker of an insertion above the position bar.
public sealed class InsertMarker
{
    public double Fraction { get; init; }   // position in the track, 0..1
    public string ToolTip { get; init; }
    public ICommand RemoveCommand { get; init; }
}
