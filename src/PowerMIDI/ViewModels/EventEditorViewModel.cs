using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using PowerMIDI.Core;

namespace PowerMIDI.ViewModels;

// A row of the Event Editor: an event of the current track (see MidiEventList), with its position as bar:beat:tick and
// its time, worked out when the row is shown (the list only shows its visible rows: a long file is listed at once).
// FixedText: what replaces the event (a value fixed in the Mixer or Effects tab: the row is blue, its description says
// it), or null.
public sealed class EventRowViewModel : ObservableObject
{
    public MidiEventRow Row { get; init; }
    public TempoMap TempoMap { get; init; }
    public Func<MidiEventRow, string> FixedOf { get; init; }   // what replaces an event (EventEditorViewModel.FixedOf)
    string position, time;

    public string Position => position ??= BarBeat(TimeConverter.ConvertTo<BarBeatTicksTimeSpan>(Row.Tick, TempoMap));
    public string Time => time ??= TimeSpan.FromSeconds(Seconds).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    public double Seconds => ((TimeSpan)TimeConverter.ConvertTo<MetricTimeSpan>(Row.Tick, TempoMap)).TotalSeconds;

    static string BarBeat(BarBeatTicksTimeSpan bbt) => $"{bbt.Bars + 1}:{bbt.Beats + 1}:{bbt.Ticks:000}";
    public string Track => Row.Track > 0 ? Row.Track.ToString() : "";   // a setting: none
    public string Channel => Row.Channel >= 0 ? (Row.Channel + 1).ToString() : "";
    public string Type => Row.Type;
    public string Data => Row.Data;
    public string Description => fixedText == null || Row.Kind == EventKind.Setting ? Row.Description
                               : Row.Description == "" ? $"→ {fixedText}" : $"{Row.Description} → {fixedText}";

    string fixedText;
    public string FixedText
    {
        get => fixedText;
        private set
        {
            if (!Set(ref fixedText, value)) return;
            OnPropertyChanged(nameof(IsFixed));
            OnPropertyChanged(nameof(Description));
        }
    }
    public bool IsFixed => fixedText != null;

    internal void UpdateFixed() => FixedText = FixedOf(Row);
}

// A value of the edit panel: its label and its text.
public sealed class EditField : ObservableObject
{
    public string Label { get; init; }
    string value;
    public string Value { get => value; set => Set(ref this.value, value); }
}

// Changes of the track, made by the main view model (see MainViewModel.Events.cs): on a copy of the track in memory,
// played at once; undo / redo.
public interface IEventEditing
{
    string EditBlocked { get; }   // why no change can be made now (null: they can)
    bool CanUndo { get; }
    bool CanRedo { get; }
    int TicksPerQuarter { get; }
    // Makes a change on the track; it returns the event to select afterwards (track, place), if any.
    void Edit(Func<MidiFile, (int Track, int Index)?> change);
    // Append / Replace started: the settings in blue so far are written in the whole track (as the save does), so that
    // the settings changed from here are the ones written at the end.
    void WriteSettingsInTrack();
    // Writes the settings in blue (Mixer / Effects tabs) as events: after the event at index of a track (-1: before the
    // events at tick), or instead of the events replaced; returns what was written (null: nothing in blue).
    string WriteFixed(int track, int index, long tick, IReadOnlyList<(int Track, int Index)> replaced);
    // Audition: plays a note on its own (Note Off after length).
    void PlayNote(int channel, int note, int velocity, TimeSpan length);
    // Append / Replace cancelled: the settings in blue go back to the file's values.
    void DropFixed();
    void Undo();
    void Redo();
}

// Event Editor (separate window): all the events of the current track in a list, filtered by track, channel, kind and
// text; the row being played is followed during playback (Follow); double click on a row: playback from there. The
// list is built again when the track changes (start, insertion, save, change). The edit panel under the list changes
// the selected event (Apply), deletes the selected ones, adds an event at the position of the selected one; undo /
// redo. Append / Replace, in two clicks: the first one starts (the settings in blue so far go into the whole track), the
// settings are then changed in the Mixer / Effects tabs, the second click writes them as events after the selected
// event, or instead of the selected events (see MainViewModel.Insert.cs); Cancel drops them. The values fixed in the
// Mixer and Effects tabs are listed first, in blue, as settings sent at the start (not events of the file: changed in
// those tabs).
public sealed class EventEditorViewModel : ObservableObject
{
    readonly InstrumentDefinitions defs;
    readonly EffectsViewModel effects;
    readonly IEventEditing editing;
    List<EventRowViewModel> rows = new();
    TempoMap tempoMap;
    string loadedFile;

    public EventEditorViewModel(InstrumentDefinitions defs, EffectsViewModel effects, Action<double> seek, IEventEditing editing)
    {
        this.defs = defs;
        this.effects = effects;
        this.editing = editing;
        SeekCommand = new RelayCommand(p => { if (p is EventRowViewModel row) seek(row.Seconds); });
        ApplyCommand = new RelayCommand(Apply);
        DeleteCommand = new RelayCommand(p => Delete((p as System.Collections.IList)?.Cast<EventRowViewModel>().ToList()));
        AddCommand = new RelayCommand(Add);
        AppendCommand = new RelayCommand(p => Write("Append", (p as System.Collections.IList)?.Cast<EventRowViewModel>().ToList()));
        ReplaceCommand = new RelayCommand(p => Write("Replace", (p as System.Collections.IList)?.Cast<EventRowViewModel>().ToList()));
        CancelCommand = new RelayCommand(Cancel);
        UndoCommand = new RelayCommand(() => Change(editing.Undo, "Undone"));
        RedoCommand = new RelayCommand(() => Change(editing.Redo, "Redone"));
        View = CollectionViewSource.GetDefaultView(rows);
        AddKind = NewKinds[0];
    }

    public ICommand SeekCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand DeleteCommand { get; }   // parameter: the selected rows
    public ICommand AddCommand { get; }
    public ICommand AppendCommand { get; }
    public ICommand ReplaceCommand { get; }   // parameter: the selected rows
    public ICommand CancelCommand { get; }    // Append / Replace: drops the settings changed
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }

    string title = "Events";
    public string Title { get => title; private set => Set(ref title, value); }

    ICollectionView view;
    public ICollectionView View { get => view; private set => Set(ref view, value); }

    // ---------- Track ----------

    // Builds the list of a track (file, or its copy with the changes), in its standard (names of the instruments). The
    // filters are kept while it is the same file.
    public void Load(MidiFile midi, string standard, string fileName)
    {
        tempoMap = midi.GetTempoMap();
        Func<int, int, int, int, int, string> name = (c, program, msb, lsb, rhythm) => defs.Resolve(standard, c, program, msb, lsb, rhythm).Name;
        var list = MidiEventList.Build(midi, name, DescribeSysEx);
        instrumentName = name;
        rows = list.Select(r => new EventRowViewModel { Row = r, TempoMap = tempoMap, FixedOf = FixedOf }).ToList();
        settingCount = 0;
        RefreshFixed(false);
        Title = $"Events · {fileName}";

        // Tracks: their name (first Track Name of each).
        var names = list.Where(r => r.Kind == EventKind.Meta && r.Type == "Track Name").GroupBy(r => r.Track).ToDictionary(g => g.Key, g => g.First().Data);
        Tracks = new[] { "All tracks" }.Concat(list.Select(r => r.Track).Distinct().OrderBy(t => t)
                                                   .Select(t => names.TryGetValue(t, out var n) && n != "" ? $"{t} · {n}" : $"{t}")).ToList();
        var file = fileName.TrimEnd(' ', '*');
        if (file != loadedFile || trackFilter >= Tracks.Count) trackFilter = 0;
        if (file != loadedFile) { channelFilter = kindFilter = 0; searchText = ""; SetPending(null); }   // another track: Append / Replace over
        loadedFile = file;
        OnPropertyChanged(nameof(TrackFilter));
        OnPropertyChanged(nameof(ChannelFilter));
        OnPropertyChanged(nameof(KindFilter));
        OnPropertyChanged(nameof(SearchText));
        current = null;
        View = CollectionViewSource.GetDefaultView(rows);
        View.Filter = Matches;
        UpdateCount();
        UpdateUndo();
        ShowForm();
    }

    // No track any more (removed from the playlist): an empty list.
    public void Clear()
    {
        SetPending(null);
        tempoMap = null;
        rows = new List<EventRowViewModel>();
        settingCount = 0;
        loadedFile = null;
        current = null;
        Title = "Events";
        Tracks = new[] { "All tracks" };
        trackFilter = 0;
        OnPropertyChanged(nameof(TrackFilter));
        View = CollectionViewSource.GetDefaultView(rows);
        View.Filter = Matches;
        SelectedRow = null;
        Status = "";
        Error = null;
        UpdateCount();
        UpdateUndo();
        ShowForm();
    }

    // SysEx: the effect parameters it writes (GS / XG, GM2, MT-32 reverb), otherwise MidiEventList's description.
    string DescribeSysEx(byte[] d)
    {
        var texts = GsXgEffects.ValuesOf(d).Select(x => effects.DescribeEffect(x.Param, x.Value)).ToList();
        var gm2 = new int[Gm2Effects.Count];
        var positions = new List<(int Position, int Param)>();
        if (texts.Count == 0 && Gm2Effects.ApplyBytes(d, gm2, positions))
            texts = positions.Select(x => effects.DescribeGm2(x.Param, gm2[x.Param])).ToList();
        var mt32 = new int[Mt32Effects.Count];
        positions.Clear();
        if (texts.Count == 0 && Mt32Effects.ApplyBytes(d, mt32, new int[9], positions) != Mt32Effects.Changes.None)
            texts = positions.Select(x => x.Param).Distinct().Select(p => effects.DescribeMt32(p, mt32[p])).ToList();
        if (texts.Count == 0) return MidiEventList.DescribeSysEx(d);
        return string.Join(" · ", texts.Take(3)) + (texts.Count > 3 ? $" · +{texts.Count - 3} more" : "");
    }

    // What replaces an event (blue row): MidiEventList.FixedText, with the values of the Effects tab named as by Append /
    // Replace: those of the settings modules whose messages write the same parameters as the event.
    Func<int, int, int, int, int, string> instrumentName;

    string FixedOf(MidiEventRow row)
    {
        if (row.Kind == EventKind.Setting)
            return row.Description.StartsWith("Instrument from ", StringComparison.Ordinal) ? "Sent from that time: change it in Mixer tab"
                                                                                             : "Sent at start: change it in Mixer / Effects tab";
        var text = MidiEventList.FixedText(row, instrumentName);
        if (text == null || !text.StartsWith("Fixed in Effects tab", StringComparison.Ordinal)) return text;
        var texts = new List<string>();
        foreach (var m in ModuleSettings.All)
        {
            if (!m.IsActive() || m.Describe == null) continue;
            foreach (var (key, value) in m.Fixed())
                if (Writes(m.Events(key, value).ToList(), row)) texts.Add(m.Describe(key, value));
        }
        texts = texts.Distinct().ToList();
        if (texts.Count == 0) return text;
        return "Effects: " + string.Join(" · ", texts.Take(3)) + (texts.Count > 3 ? $" · +{texts.Count - 3} more" : "");
    }

    // The messages of a fixed value write a parameter of the event: a SysEx at an address it covers (same module
    // model), or the RPN / NRPN of a data entry on its channel.
    static bool Writes(List<MidiEvent> events, MidiEventRow row)
    {
        if (row.Event is SysExEvent sx)
            return SysExSpan(sx.Data) is { } target &&
                   events.Any(e => e is SysExEvent own && SysExSpan(own.Data) is { } span && span.Model == target.Model &&
                                   span.Address < target.Address + target.Count && target.Address < span.Address + span.Count);
        if (row.Event is not ControlChangeEvent cc || row.Entry is not { } entry) return false;
        int msbNumber = entry.Rpn ? 101 : 99, lsbNumber = entry.Rpn ? 100 : 98;
        var own = events.OfType<ControlChangeEvent>().Where(e => e.Channel == cc.Channel).ToList();
        return own.Any(e => e.ControlNumber == msbNumber && e.ControlValue == entry.Msb) && own.Any(e => e.ControlNumber == lsbNumber && e.ControlValue == entry.Lsb);
    }

    // Module (manufacturer and model), first address and number of data bytes of a SysEx: Roland DT1 (41 dev model 12
    // a a a data sum), Yamaha parameter change (43 1n model a a a data); another SysEx: its bytes but the last value.
    static (int Model, int Address, int Count)? SysExSpan(byte[] d)
    {
        int end = d.Length > 0 && d[^1] == 0xF7 ? d.Length - 1 : d.Length;
        if (end >= 9 && d[0] == 0x41 && d[3] == 0x12) return (0x4100 | d[2], d[4] << 14 | d[5] << 7 | d[6], end - 8);
        if (end >= 7 && d[0] == 0x43 && (d[1] & 0xF0) == 0x10) return (0x4300 | d[2], d[3] << 14 | d[4] << 7 | d[5], end - 6);
        if (end < 2) return null;
        int hash = 17;
        for (int i = 0; i < end - 1; i++) hash = hash * 31 + d[i];
        return (hash, 0, 1);
    }

    // The values fixed in the Mixer or Effects tab changed: the settings at the top of the list are listed again, the
    // rows they replace turn blue (or back).
    public void RefreshFixed() => RefreshFixed(true);

    int settingCount;   // the first rows: the settings

    void RefreshFixed(bool refresh)
    {
        var selected = selectedRow;
        rows.RemoveRange(0, settingCount);
        // No track loaded: no settings to list either.
        var settings = (tempoMap == null ? Enumerable.Empty<(int Channel, string Tab, string Text)>() : Settings()).Select(s => new EventRowViewModel
        {
            Row = new MidiEventRow(0, 0, s.Channel, EventKind.Setting, "Setting", s.Tab, s.Text), TempoMap = tempoMap, FixedOf = FixedOf,
        }).ToList();
        rows.InsertRange(0, settings);
        settingCount = settings.Count;
        foreach (var row in rows) row.UpdateFixed();
        if (!refresh) return;
        View.Refresh();
        UpdateCount();
        // A setting selected: the same one again if it is still there (same channel, tab and name).
        if (selected?.Row.Kind == EventKind.Setting)
            SelectedRow = settings.FirstOrDefault(r => r.Row.Channel == selected.Row.Channel && r.Row.Data == selected.Row.Data &&
                                                       r.Row.Description.Split(':')[0] == selected.Row.Description.Split(':')[0]);
    }

    // The values fixed in the Mixer tab (instruments, drum parts, controllers, maps) and in the Effects tab (the settings
    // modules of the mode of the track), named as by Append / Replace; channel -1: none.
    IEnumerable<(int Channel, string Tab, string Text)> Settings()
    {
        var instruments = ChannelMixer.Instruments();
        for (int c = 0; c < 16; c++)
        {
            // One per passage (see InstrumentPassages): named by the time of its Program Change.
            foreach (var (_, passage, msb, lsb, program) in instruments.Where(x => x.Channel == c))
            {
                var name = instrumentName?.Invoke(c, program, msb, lsb, ChannelMixer.Rhythm(c));
                var from = passage == InstrumentPassages.First ? "" : $" from {MidiTools.FormatTime(TimeConverter.ConvertTo<MetricTimeSpan>(passage, tempoMap))}";
                yield return (c, "Mixer", $"Instrument{from}: {(string.IsNullOrEmpty(name) ? "" : name + " ")}(bank {msb} / {lsb}, PC {program + 1})");
            }
            if (ChannelMixer.RhythmOverride[c] >= 0)
                yield return (c, "Mixer", ChannelMixer.RhythmOverride[c] == 0 ? "Part: normal" : $"Part: drum part {ChannelMixer.RhythmOverride[c]}");
            for (int k = 0; k < ChannelMixer.ControllerNames.Length; k++)
                if (ChannelMixer.IsFixed(k, c))
                    yield return (c, "Mixer", $"{char.ToUpperInvariant(ChannelMixer.ControllerNames[k][0])}{ChannelMixer.ControllerNames[k][1..]}: {ChannelMixer.FixedValue(k, c)}");
        }
        // The maps chosen in the Mixer tab play only in their mode (see ChannelMixer.ApplyVoiceMap, ApplyGsDefaultMap).
        if (ChannelMixer.XgMode && ChannelMixer.VoiceMapOverride >= 0) yield return (-1, "Mixer", $"XG voice map: {MixerViewModel.VoiceMapNames[2 - ChannelMixer.VoiceMapOverride]}");
        if (ChannelMixer.GsMode && ChannelMixer.GsDefaultMapOverride >= 1) yield return (-1, "Mixer", $"GS default map: {MixerViewModel.GsDefaultMapNames[5 - ChannelMixer.GsDefaultMapOverride]}");
        foreach (var m in ModuleSettings.All)
        {
            if (!m.IsActive()) continue;
            foreach (var (key, value) in m.Fixed())
                yield return (m.ChannelOf(key), "Effects", m.Describe?.Invoke(key, value) ?? $"{m.Name} {key.A}/{key.B}/{key.C} = {value}");
        }
    }

    // ---------- Filters ----------

    public IReadOnlyList<string> Tracks { get => tracks; private set => Set(ref tracks, value); }
    IReadOnlyList<string> tracks = new[] { "All tracks" };

    public IReadOnlyList<string> Channels => ChannelNames;
    static readonly IReadOnlyList<string> ChannelNames = new[] { "All channels" }.Concat(Enumerable.Range(1, 16).Select(c => $"Channel {c}"))
                                                                                    .Append("No channel").ToList();

    public IReadOnlyList<string> Kinds => KindNames;
    static readonly IReadOnlyList<string> KindNames =
        new[] { "All events", "Notes", "Control Change", "Program Change", "CC + PC", "Pitch Bend", "Pressure", "SysEx", "Meta / text",
                "Mixer / Effects settings" };
    // Kinds shown by each item of the list (null: all).
    static readonly EventKind[][] KindOf =
        { null, new[] { EventKind.Note }, new[] { EventKind.Controller }, new[] { EventKind.Program }, new[] { EventKind.Controller, EventKind.Program },
          new[] { EventKind.PitchBend }, new[] { EventKind.Aftertouch }, new[] { EventKind.SysEx }, new[] { EventKind.Meta }, new[] { EventKind.Setting } };

    int trackFilter, channelFilter, kindFilter;
    string searchText = "";

    public int TrackFilter { get => trackFilter; set { if (Set(ref trackFilter, value)) Refilter(); } }
    public int ChannelFilter { get => channelFilter; set { if (Set(ref channelFilter, value)) Refilter(); } }
    public int KindFilter { get => kindFilter; set { if (Set(ref kindFilter, value)) Refilter(); } }
    public string SearchText { get => searchText; set { if (Set(ref searchText, value ?? "")) Refilter(); } }

    int TrackNumber(int index) => index <= 0 ? -1 : int.Parse(Tracks[index].Split(' ')[0], CultureInfo.InvariantCulture);

    bool Matches(object item)
    {
        var row = ((EventRowViewModel)item).Row;
        if (trackFilter > 0 && row.Track != TrackNumber(trackFilter)) return false;
        if (channelFilter is > 0 and <= 16 && row.Channel != channelFilter - 1) return false;
        if (channelFilter == 17 && row.Channel >= 0) return false;
        if (KindOf[kindFilter] is { } kinds && !kinds.Contains(row.Kind)) return false;
        return searchText == "" || row.Type.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               row.Data.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               row.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    void Refilter()
    {
        View.Refresh();
        current = null;
        UpdateCount();
    }

    string countText = "";
    public string CountText { get => countText; private set => Set(ref countText, value); }
    void UpdateCount() => CountText = $"{View.Cast<object>().Count()} / {rows.Count} events";

    // ---------- Follow and selection ----------

    bool follow;   // off at the start: the list stays where it is
    public bool Follow { get => follow; set { if (Set(ref follow, value) && !value) current = null; } }

    EventRowViewModel current;
    bool following;   // the selection is being moved by Follow
    EventRowViewModel selectedRow;
    // Row shown in the edit panel. Chosen by hand during playback: Follow stops, so that it stays.
    public EventRowViewModel SelectedRow
    {
        get => selectedRow;
        set
        {
            if (!Set(ref selectedRow, value)) return;
            if (!following && value != null && follow && value != current) Follow = false;
            ShowForm();
            if (!following) PlaySelected(value);
        }
    }

    // Audition: the note selected by hand is played (its channel, key and velocity; at most 2 s).
    bool audition;
    public bool Audition { get => audition; set => Set(ref audition, value); }

    void PlaySelected(EventRowViewModel row)
    {
        if (!audition || row?.Row.Event is not NoteOnEvent on || on.Velocity == 0) return;
        double seconds = row.Row.Length > 0 && tempoMap != null
            ? ((TimeSpan)LengthConverter.ConvertTo<MetricTimeSpan>(row.Row.Length, row.Row.Tick, tempoMap)).TotalSeconds : 0.5;
        editing.PlayNote(on.Channel, on.NoteNumber, on.Velocity, TimeSpan.FromSeconds(Math.Clamp(seconds, 0.1, 2)));
    }

    // The row to bring into view (the window scrolls to it).
    public event Action<EventRowViewModel> FollowRow;

    // Playback position (seconds): with Follow, the last row shown at or before it is selected.
    public void UpdatePosition(double seconds)
    {
        if (!follow || rows.Count == 0) return;
        long tick = TimeConverter.ConvertFrom(new MetricTimeSpan((long)(seconds * 1_000_000)), tempoMap);
        var row = LastAtOrBefore(tick);
        if (row == null || row == current) return;
        current = row;
        Select(row);
    }

    // Last row shown at or before a position.
    EventRowViewModel LastAtOrBefore(long tick)
    {
        int lo = 0, hi = rows.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (rows[mid].Row.Tick <= tick) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        while (found >= 0 && !Matches(rows[found])) found--;
        return found >= 0 ? rows[found] : null;
    }

    void Select(EventRowViewModel row)
    {
        following = true;
        try { SelectedRow = row; }
        finally { following = false; }
        FollowRow?.Invoke(row);
    }

    // After a change: the event changed or added (track, place).
    public void SelectEvent(int track, int index)
    {
        var row = rows.FirstOrDefault(r => r.Row.Track == track && r.Row.Index == index);
        if (row != null && Matches(row)) Select(row);
    }

    // ---------- Edit panel ----------

    EventForm form;
    public System.Collections.ObjectModel.ObservableCollection<EditField> Fields { get; } = new();

    string editKind = "", channelText = "", textLabel, text, notEditable, error, status = "";
    bool hasChannel;
    public string EditKind { get => editKind; private set => Set(ref editKind, value); }
    public bool HasChannel { get => hasChannel; private set => Set(ref hasChannel, value); }
    public string ChannelText { get => channelText; set => Set(ref channelText, value); }
    public string TextLabel { get => textLabel; private set { if (Set(ref textLabel, value)) OnPropertyChanged(nameof(HasText)); } }
    public bool HasText => textLabel != null;
    public string Text { get => text; set => Set(ref text, value); }
    // Why the selected event cannot be changed (null: it can), or why nothing can be changed now.
    public string NotEditable { get => notEditable; private set { if (Set(ref notEditable, value)) { OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(MessagesToolTip)); } } }
    public bool CanApply => form != null && notEditable == null;
    public bool HasForm => form != null;   // an event is selected: its fields and Apply are shown
    public string Error { get => error; private set { if (Set(ref error, value)) OnPropertyChanged(nameof(MessagesToolTip)); } }
    // Status: short (on the line of the buttons, cut if too long); its tooltip shows it whole, with the details of what was
    // written (Append / Replace).
    public string Status { get => status; private set { Set(ref status, value); SetStatusDetail(null); } }
    string statusDetail;
    public string StatusToolTip => string.IsNullOrEmpty(status) ? null : statusDetail != null ? $"{status}\n{statusDetail}" : status;

    void SetStatusDetail(string detail)
    {
        statusDetail = detail;
        OnPropertyChanged(nameof(StatusToolTip));
        OnPropertyChanged(nameof(MessagesToolTip));
    }

    // Tooltip of the messages of the line of the buttons (error, why the event cannot be changed, status): whole.
    public string MessagesToolTip
    {
        get
        {
            var all = new[] { error, notEditable, StatusToolTip }.Where(m => !string.IsNullOrEmpty(m)).ToList();
            return all.Count > 0 ? string.Join("\n", all) : null;
        }
    }

    public IReadOnlyList<string> NewKinds => MidiEventForm.NewKinds;
    string addKind;
    public string AddKind { get => addKind; set => Set(ref addKind, value); }

    bool canUndo, canRedo;
    public bool CanUndo { get => canUndo; private set => Set(ref canUndo, value); }
    public bool CanRedo { get => canRedo; private set => Set(ref canRedo, value); }
    void UpdateUndo() => (CanUndo, CanRedo) = (editing.CanUndo, editing.CanRedo);

    // Fields of the selected row.
    void ShowForm()
    {
        form = selectedRow is { Row.Kind: not EventKind.Setting } ? MidiEventForm.Of(selectedRow.Row) : null;
        Fields.Clear();
        Error = null;
        OnPropertyChanged(nameof(HasForm));
        if (form == null)
        {
            EditKind = "";
            HasChannel = false;
            TextLabel = null;
            NotEditable = selectedRow != null ? "Setting: change it in Mixer / Effects tab" : "Select an event";
            return;
        }
        EditKind = form.Kind;
        HasChannel = form.HasChannel;
        ChannelText = form.HasChannel ? (form.Channel + 1).ToString(CultureInfo.InvariantCulture) : "";
        for (int i = 0; i < form.Labels.Count; i++) Fields.Add(new EditField { Label = form.Labels[i], Value = form.Values[i] });
        TextLabel = form.TextLabel;
        Text = form.Text;
        NotEditable = form.NotEditable ?? editing.EditBlocked;
        OnPropertyChanged(nameof(CanApply));
    }

    // Makes a change (Follow stops: the selection is the event changed) and shows the result.
    bool Change(Action action, string done)
    {
        if (editing.EditBlocked is { } blocked) { Error = blocked; return false; }
        if (pending != null) { Error = $"{pending}: click {pending} again or Cancel first"; return false; }
        Follow = false;
        action();
        UpdateUndo();
        Status = done;
        return true;
    }

    // Apply: the selected event gets the values of the panel.
    void Apply()
    {
        if (!CanApply) return;
        int channel = form.HasChannel && int.TryParse(ChannelText?.Trim(), out int c) ? c - 1 : form.HasChannel ? -1 : 0;
        var result = MidiEventForm.Read(form, channel, Fields.Select(f => f.Value).ToList(), Text);
        if (result.Error != null) { Error = result.Error; return; }
        var row = selectedRow.Row;
        Change(() => editing.Edit(file =>
        {
            if (result.Note is { } n) MidiEventEdits.SetNote(file, row.Track, row.Index, row.OffIndex, n.Channel, n.Note, n.Velocity, n.Length);
            else MidiEventEdits.Replace(file, row.Track, row.Index, result.Event);
            return (row.Track, row.Index);
        }), $"Changed: {form.Kind} at {selectedRow.Position} (Ctrl+Z: undo)");
    }

    // Deletes the selected events; the row after them is selected.
    void Delete(List<EventRowViewModel> selected)
    {
        var deleted = (selected ?? new List<EventRowViewModel>()).Where(r => r.Row.Kind != EventKind.Setting).ToList();
        if (deleted.Count == 0) { Error = selected?.Count > 0 ? "Settings: change them in Mixer / Effects tabs" : "Select events to delete"; return; }
        long tick = deleted.Min(r => r.Row.Tick);
        if (!Change(() => editing.Edit(file =>
        {
            MidiEventEdits.Delete(file, deleted.SelectMany(r => r.Row.OffIndex >= 0
                ? new[] { (r.Row.Track, r.Row.Index), (r.Row.Track, r.Row.OffIndex) } : new[] { (r.Row.Track, r.Row.Index) }));
            return null;
        }), $"Deleted: {deleted.Count} event(s) (Ctrl+Z: undo)")) return;
        var next = rows.FirstOrDefault(r => r.Row.Tick >= tick && Matches(r)) ?? LastAtOrBefore(tick);
        if (next != null) Select(next);
    }

    // ---------- Append / Replace ----------

    // "Append" or "Replace" while the settings are being changed (between the two clicks), otherwise null; the rows
    // chosen at the first click (Append: the selected one, none: at the start of the first track; Replace: the selected
    // ones), by their track, position, type and rank among the same ones there: the first click may write the settings
    // so far in the track, which changes the places of the events (and their values).
    string pending;
    List<(int Track, long Tick, string Type, int Rank)> targets = new();

    // Pressed while pending (the click itself changes nothing: the commands do, then the state is shown again).
    public bool IsAppending { get => pending == "Append"; set => OnPropertyChanged(); }
    public bool IsReplacing { get => pending == "Replace"; set => OnPropertyChanged(); }
    public bool CanAppend => pending != "Replace";
    public bool CanReplace => pending != "Append";
    public bool IsPending => pending != null;

    void SetPending(string value)
    {
        pending = value;
        foreach (var name in new[] { nameof(IsAppending), nameof(IsReplacing), nameof(CanAppend), nameof(CanReplace), nameof(IsPending) })
            OnPropertyChanged(name);
    }

    (int Track, long Tick, string Type, int Rank) Target(EventRowViewModel row) =>
        (row.Row.Track, row.Row.Tick, row.Row.Type, rows.Count(r => Same(r.Row, row.Row) && r.Row.Index < row.Row.Index));

    static bool Same(MidiEventRow a, MidiEventRow b) => a.Track == b.Track && a.Tick == b.Tick && a.Type == b.Type;

    EventRowViewModel Find((int Track, long Tick, string Type, int Rank) t) =>
        rows.Where(r => r.Row.Kind != EventKind.Setting && r.Row.Track == t.Track && r.Row.Tick == t.Tick && r.Row.Type == t.Type)
            .OrderBy(r => r.Row.Index).Skip(t.Rank).FirstOrDefault();

    // First click: remembers where to write, writes the settings in blue so far in the track; second click: writes the
    // settings changed since.
    void Write(string kind, List<EventRowViewModel> selected)
    {
        try { Start(kind, selected); }
        finally { SetPending(pending); }   // the buttons show the real state
    }

    void Start(string kind, List<EventRowViewModel> selected)
    {
        if (pending == kind) { Confirm(); return; }
        if (pending != null) return;
        if (editing.EditBlocked is { } blocked) { Error = blocked; return; }
        var chosen = (kind == "Append" ? new[] { selectedRow }.ToList() : selected ?? new List<EventRowViewModel>())
            .Where(r => r != null && r.Row.Kind != EventKind.Setting)
            .OrderBy(r => r.Row.Tick).ThenBy(r => r.Row.Track).ThenBy(r => r.Row.Index).ToList();
        if (kind == "Replace" && chosen.Count == 0) { Error = "Select events to replace"; return; }
        Follow = false;
        Error = null;
        targets = chosen.Select(Target).ToList();
        editing.WriteSettingsInTrack();
        UpdateUndo();
        SetPending(kind);
        Status = $"{kind}: change settings, then click {kind} again";
    }

    void Confirm()
    {
        string kind = pending;
        var found = targets.Select(Find).ToList();
        SetPending(null);
        if (found.Any(r => r == null)) { Error = "Events changed meanwhile: nothing written"; editing.DropFixed(); return; }
        int track = 1, index = -1;
        long tick = 0;
        string position = "1:1:000";
        var replaced = new List<(int Track, int Index)>();
        if (found.Count > 0)
        {
            (track, index, tick, position) = (found[0].Row.Track, found[0].Row.Index, found[0].Row.Tick, found[0].Position);
            if (kind == "Replace")
                replaced = found.SelectMany(r => r.Row.OffIndex >= 0 ? new[] { (r.Row.Track, r.Row.Index), (r.Row.Track, r.Row.OffIndex) }
                                                                     : new[] { (r.Row.Track, r.Row.Index) }).ToList();
        }
        string written = null;
        Change(() => written = editing.WriteFixed(track, index, tick, replaced), "");
        if (editing.EditBlocked != null) return;
        if (written == null) { Status = "No setting changed: nothing written"; return; }
        Status = $"{(kind == "Append" ? "Appended" : $"Replaced {found.Count} event(s)")} at {position} (Ctrl+Z: undo)";
        SetStatusDetail(written);   // in the tooltip: the line would be too long
    }

    void Cancel()
    {
        if (pending == null) return;
        string kind = pending;
        SetPending(null);
        editing.DropFixed();
        Status = $"{kind} cancelled";
    }

    // Adds an event of the chosen kind at the position of the selected row (its track and channel), with default
    // values: the panel then shows it.
    void Add()
    {
        var at = selectedRow?.Row;
        int track = at is { Track: > 0 } ? at.Track : 1;   // a setting (track 0): first track
        long tick = at?.Tick ?? 0;
        int channel = at is { Channel: >= 0 } ? at.Channel : 0;
        var empty = MidiEventForm.New(AddKind, channel, editing.TicksPerQuarter);
        var result = MidiEventForm.Read(empty, channel, empty.Values, empty.Text);
        if (result.Error != null) { Error = result.Error; return; }
        Change(() => editing.Edit(file => (track, result.Note is { } n
            ? MidiEventEdits.AddNote(file, track, tick, n.Channel, n.Note, n.Velocity, n.Length)
            : MidiEventEdits.Add(file, track, tick, result.Event))), $"Added: {AddKind} (change its values, then Apply)");
    }
}
