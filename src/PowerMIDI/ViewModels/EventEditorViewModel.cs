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
// FixedText: what replaces the event (a value fixed in the Mixer or Effects tab: the row is blue), or null.
public sealed class EventRowViewModel : ObservableObject
{
    public MidiEventRow Row { get; init; }
    public TempoMap TempoMap { get; init; }
    string position, time;

    public string Position => position ??= BarBeat(TimeConverter.ConvertTo<BarBeatTicksTimeSpan>(Row.Tick, TempoMap));
    public string Time => time ??= TimeSpan.FromSeconds(Seconds).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    public double Seconds => ((TimeSpan)TimeConverter.ConvertTo<MetricTimeSpan>(Row.Tick, TempoMap)).TotalSeconds;

    static string BarBeat(BarBeatTicksTimeSpan bbt) => $"{bbt.Bars + 1}:{bbt.Beats + 1}:{bbt.Ticks:000}";
    public string Track => Row.IsInserted ? "Ins" : Row.Track.ToString();
    public string Channel => Row.Channel >= 0 ? (Row.Channel + 1).ToString() : "";
    public string Type => Row.Type;
    public string Data => Row.Data;
    public string Description => Row.Description;

    string fixedText;
    public string FixedText { get => fixedText; private set { if (Set(ref fixedText, value)) OnPropertyChanged(nameof(IsFixed)); } }
    public bool IsFixed => fixedText != null;

    internal void UpdateFixed() => FixedText = MidiEventList.FixedText(Row);
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
    void Undo();
    void Redo();
}

// Event Editor (separate window): all the events of the current track in a list, filtered by track, channel, kind and
// text; the row being played is followed during playback (Follow); double click on a row: playback from there. The
// list is built again when the track changes (start, insertion, save, change). The edit panel under the list changes
// the selected event (Apply), deletes the selected ones, adds an event at the position of the selected one; undo /
// redo. The events written by the Insert button are shown but not changed here.
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
        UndoCommand = new RelayCommand(() => Change(editing.Undo, "Undone"));
        RedoCommand = new RelayCommand(() => Change(editing.Redo, "Redone"));
        View = CollectionViewSource.GetDefaultView(rows);
        AddKind = NewKinds[0];
    }

    public ICommand SeekCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand DeleteCommand { get; }   // parameter: the selected rows
    public ICommand AddCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }

    string title = "Events";
    public string Title { get => title; private set => Set(ref title, value); }

    ICollectionView view;
    public ICollectionView View { get => view; private set => Set(ref view, value); }

    // ---------- Track ----------

    // Builds the list of a track (file, or its copy with the changes) and of its insertions, in its standard (names of
    // the instruments). The filters are kept while it is the same file.
    public void Load(MidiFile midi, IEnumerable<(long Tick, MidiEvent Event)> inserted, string standard, string fileName)
    {
        tempoMap = midi.GetTempoMap();
        var list = MidiEventList.Build(midi, (c, program, msb, lsb, rhythm) => defs.Resolve(standard, c, program, msb, lsb, rhythm).Name,
                                       DescribeSysEx, inserted);
        rows = list.Select(r => new EventRowViewModel { Row = r, TempoMap = tempoMap }).ToList();
        RefreshFixed();
        Title = $"Events · {fileName}";

        // Tracks: their name (first Track Name of each).
        var names = list.Where(r => r.Kind == EventKind.Meta && r.Type == "Track Name").GroupBy(r => r.Track).ToDictionary(g => g.Key, g => g.First().Data);
        Tracks = new[] { "All tracks" }.Concat(list.Select(r => r.Track).Where(t => t > 0).Distinct().OrderBy(t => t)
                                                   .Select(t => names.TryGetValue(t, out var n) && n != "" ? $"{t} · {n}" : $"{t}")).ToList();
        var file = fileName.TrimEnd(' ', '*');
        if (file != loadedFile || trackFilter >= Tracks.Count) trackFilter = 0;
        if (file != loadedFile) { channelFilter = kindFilter = 0; searchText = ""; }
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

    // The values fixed in the Mixer or Effects tab changed: the rows they replace turn blue (or back).
    public void RefreshFixed()
    {
        foreach (var row in rows) row.UpdateFixed();
    }

    // ---------- Filters ----------

    public IReadOnlyList<string> Tracks { get => tracks; private set => Set(ref tracks, value); }
    IReadOnlyList<string> tracks = new[] { "All tracks" };

    public IReadOnlyList<string> Channels => ChannelNames;
    static readonly IReadOnlyList<string> ChannelNames = new[] { "All channels" }.Concat(Enumerable.Range(1, 16).Select(c => $"Channel {c}"))
                                                                                    .Append("No channel").ToList();

    public IReadOnlyList<string> Kinds => KindNames;
    static readonly IReadOnlyList<string> KindNames =
        new[] { "All events", "Notes", "Control Change", "Program Change", "Pitch Bend", "Pressure", "SysEx", "Meta / text" };
    static readonly EventKind?[] KindOf =
        { null, EventKind.Note, EventKind.Controller, EventKind.Program, EventKind.PitchBend, EventKind.Aftertouch, EventKind.SysEx, EventKind.Meta };

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
        if (KindOf[kindFilter] is { } kind && row.Kind != kind) return false;
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

    bool follow = true;
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
        }
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
    public string NotEditable { get => notEditable; private set { if (Set(ref notEditable, value)) OnPropertyChanged(nameof(CanApply)); } }
    public bool CanApply => form != null && notEditable == null;
    public string Error { get => error; private set => Set(ref error, value); }
    public string Status { get => status; private set => Set(ref status, value); }

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
        form = selectedRow != null ? MidiEventForm.Of(selectedRow.Row) : null;
        Fields.Clear();
        Error = null;
        if (form == null)
        {
            EditKind = "";
            HasChannel = false;
            TextLabel = null;
            NotEditable = "Select an event";
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
    void Change(Action action, string done)
    {
        if (editing.EditBlocked is { } blocked) { Error = blocked; return; }
        Follow = false;
        action();
        UpdateUndo();
        Status = done;
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

    // Deletes the selected events (not the inserted ones); the row after them is selected.
    void Delete(List<EventRowViewModel> selected)
    {
        var deleted = (selected ?? new List<EventRowViewModel>()).Where(r => !r.Row.IsInserted).ToList();
        if (deleted.Count == 0) { Error = "Select events of the file to delete"; return; }
        long tick = deleted.Min(r => r.Row.Tick);
        Change(() => editing.Edit(file =>
        {
            MidiEventEdits.Delete(file, deleted.SelectMany(r => r.Row.OffIndex >= 0
                ? new[] { (r.Row.Track, r.Row.Index), (r.Row.Track, r.Row.OffIndex) } : new[] { (r.Row.Track, r.Row.Index) }));
            return null;
        }), $"Deleted: {deleted.Count} event(s) (Ctrl+Z: undo)");
        var next = rows.FirstOrDefault(r => r.Row.Tick >= tick && Matches(r)) ?? LastAtOrBefore(tick);
        if (next != null) Select(next);
    }

    // Adds an event of the chosen kind at the position of the selected row (its track and channel), with default
    // values: the panel then shows it.
    void Add()
    {
        var at = selectedRow?.Row;
        int track = at is { Track: > 0 } ? at.Track : 1;
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
